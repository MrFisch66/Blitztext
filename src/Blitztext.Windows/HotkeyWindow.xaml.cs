using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Blitztext.Windows;

/// <summary>
/// Slim, dedicated editor for the per-workflow shortcuts. Pressing a combination is applied
/// live (so it shows up in the field immediately) and persisted via the supplied callback.
/// While this window is open the global hotkeys are paused so editing an already-assigned
/// combination cannot accidentally trigger a recording.
/// </summary>
public partial class HotkeyWindow : Window
{
    private readonly Func<SettingsContainer> _getSettings;
    private readonly Func<Task> _applyChangesAsync;
    private readonly IHotkeyService _hotkeyService;

    private readonly Dictionary<WorkflowType, TextBox> _boxes;
    private readonly Dictionary<WorkflowType, CheckBox> _checks;

    private bool _isLoading;
    private WorkflowType? _captureWorkflow;
    private bool _capCtrl;
    private bool _capAlt;
    private bool _capShift;
    private bool _capWin;
    private int _heldModifiers;
    private bool _committed;

    public HotkeyWindow(
        Func<SettingsContainer> getSettings,
        Func<Task> applyChangesAsync,
        IHotkeyService hotkeyService)
    {
        _getSettings = getSettings;
        _applyChangesAsync = applyChangesAsync;
        _hotkeyService = hotkeyService;

        InitializeComponent();
        Title = $"Blitztext – Tastenkürzel · {AppInfo.DisplayVersion}";

        _boxes = new Dictionary<WorkflowType, TextBox>
        {
            [WorkflowType.Transcription] = TranscriptionHotkeyBox,
            [WorkflowType.LocalTranscription] = LocalTranscriptionHotkeyBox,
            [WorkflowType.TextImprover] = TextImproverHotkeyBox
        };
        _checks = new Dictionary<WorkflowType, CheckBox>
        {
            [WorkflowType.Transcription] = TranscriptionEnabledCheckBox,
            [WorkflowType.LocalTranscription] = LocalTranscriptionEnabledCheckBox,
            [WorkflowType.TextImprover] = TextImproverEnabledCheckBox
        };

        IsVisibleChanged += OnIsVisibleChanged;
    }

    public void RefreshFromSettings()
    {
        _isLoading = true;
        try
        {
            var app = _getSettings().App;
            WorkflowSettings.EnsureDefaults(app);
            foreach (var (workflowType, box) in _boxes)
            {
                box.Text = app.Workflows[workflowType].Hotkey.DisplayText;
            }

            foreach (var (workflowType, check) in _checks)
            {
                check.IsChecked = app.Workflows[workflowType].Enabled;
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Keep the window alive so it can be reopened; just hide it.
        e.Cancel = true;
        _hotkeyService.Paused = false;
        Hide();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = (bool)e.NewValue;
        _hotkeyService.Paused = visible;
        _captureWorkflow = null;
        if (visible)
        {
            RefreshFromSettings();
            // Park focus on the Close button so no shortcut field is "armed" until clicked.
            Dispatcher.BeginInvoke(new Action(() => CloseButton.Focus()), DispatcherPriority.Input);
        }
    }

    private void Hotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && TryWorkflow(box, out var workflowType))
        {
            BeginCapture(workflowType, box);
        }
    }

    private void Hotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && TryWorkflow(box, out var workflowType) && _captureWorkflow == workflowType)
        {
            _captureWorkflow = null;
            RefreshField(workflowType);
        }
    }

    private async void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !TryWorkflow(box, out var workflowType) || _captureWorkflow != workflowType)
        {
            return;
        }

        e.Handled = true;
        var key = NormalizePressedKey(e);

        if (key == Key.Escape)
        {
            await CommitAsync(workflowType, WorkflowSettings.CreateDefaults()[workflowType].Hotkey, "Standard wiederhergestellt");
            ResetCaptureState(committed: true);
            return;
        }

        if (IsModifierKey(key))
        {
            if (!e.IsRepeat)
            {
                _heldModifiers++;
            }

            Accumulate(key);
            var preview = new HotkeyBinding(_capCtrl, _capAlt, _capShift, _capWin);
            box.Text = preview.DisplayText;

            if (preview.ModifierCount >= 2)
            {
                await CommitAsync(workflowType, preview);
            }
            else
            {
                StatusLabel.Text = $"{workflowType.DisplayName()}: {preview.DisplayText} …";
            }

            return;
        }

        var modifiers = Keyboard.Modifiers;
        var binding = new HotkeyBinding(
            Control: modifiers.HasFlag(ModifierKeys.Control),
            Alt: modifiers.HasFlag(ModifierKeys.Alt),
            Shift: modifiers.HasFlag(ModifierKeys.Shift),
            Windows: modifiers.HasFlag(ModifierKeys.Windows),
            Key: NormalizeKeyName(key));

        if (binding.IsUsable())
        {
            await CommitAsync(workflowType, binding);
        }
        else
        {
            box.Text = "Tasten drücken …";
            StatusLabel.Text = "Bitte mindestens einen Modifier (Ctrl, Alt, Shift oder Win) zusammen mit einer Taste.";
        }
    }

    private void Hotkey_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !TryWorkflow(box, out var workflowType) || _captureWorkflow != workflowType)
        {
            return;
        }

        e.Handled = true;
        if (!IsModifierKey(NormalizePressedKey(e)))
        {
            return;
        }

        if (_heldModifiers > 0)
        {
            _heldModifiers--;
        }

        if (_heldModifiers <= 0)
        {
            if (!_committed)
            {
                // Discard an incomplete preview (e.g. only one modifier was pressed).
                RefreshField(workflowType);
            }

            ResetCaptureState(committed: false);
        }
    }

    private async void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not CheckBox check || !TryWorkflow(check, out var workflowType))
        {
            return;
        }

        var app = _getSettings().App;
        WorkflowSettings.EnsureDefaults(app);
        app.Workflows[workflowType].Enabled = check.IsChecked == true;
        await _applyChangesAsync();
        StatusLabel.Text = $"{workflowType.DisplayName()}: {(check.IsChecked == true ? "aktiv" : "deaktiviert")}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void BeginCapture(WorkflowType workflowType, TextBox box)
    {
        _captureWorkflow = workflowType;
        ResetCaptureState(committed: false);
        box.Text = "Tasten drücken …";
        StatusLabel.Text = "Neue Kombination drücken. Reine Modifier wie Ctrl + Win brauchen mindestens zwei Tasten. Escape = Standard.";
    }

    private async Task CommitAsync(WorkflowType workflowType, HotkeyBinding binding, string? note = null)
    {
        var app = _getSettings().App;
        WorkflowSettings.EnsureDefaults(app);
        app.Workflows[workflowType].Hotkey = binding;

        // Update the field and mark committed synchronously, before the (async) save, so a
        // key-release arriving mid-save does not revert the field to the previous value.
        _committed = true;
        if (_boxes.TryGetValue(workflowType, out var box))
        {
            box.Text = binding.DisplayText;
        }

        StatusLabel.Text = note is null
            ? $"{workflowType.DisplayName()}: {binding.DisplayText}"
            : $"{workflowType.DisplayName()}: {note} ({binding.DisplayText})";

        await _applyChangesAsync();
    }

    private void RefreshField(WorkflowType workflowType)
    {
        var app = _getSettings().App;
        WorkflowSettings.EnsureDefaults(app);
        if (_boxes.TryGetValue(workflowType, out var box))
        {
            box.Text = app.Workflows[workflowType].Hotkey.DisplayText;
        }
    }

    private void ResetCaptureState(bool committed)
    {
        _capCtrl = false;
        _capAlt = false;
        _capShift = false;
        _capWin = false;
        _heldModifiers = 0;
        _committed = committed;
    }

    private void Accumulate(Key key)
    {
        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
                _capCtrl = true;
                break;
            case Key.LeftAlt:
            case Key.RightAlt:
                _capAlt = true;
                break;
            case Key.LeftShift:
            case Key.RightShift:
                _capShift = true;
                break;
            case Key.LWin:
            case Key.RWin:
                _capWin = true;
                break;
        }
    }

    private bool TryWorkflow(TextBox box, out WorkflowType workflowType)
    {
        foreach (var (type, candidate) in _boxes)
        {
            if (ReferenceEquals(candidate, box))
            {
                workflowType = type;
                return true;
            }
        }

        workflowType = default;
        return false;
    }

    private bool TryWorkflow(CheckBox check, out WorkflowType workflowType)
    {
        foreach (var (type, candidate) in _checks)
        {
            if (ReferenceEquals(candidate, check))
            {
                workflowType = type;
                return true;
            }
        }

        workflowType = default;
        return false;
    }

    private static Key NormalizePressedKey(KeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            return e.SystemKey;
        }

        return e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static string NormalizeKeyName(Key key)
    {
        return key switch
        {
            Key.D0 => "D0",
            Key.D1 => "D1",
            Key.D2 => "D2",
            Key.D3 => "D3",
            Key.D4 => "D4",
            Key.D5 => "D5",
            Key.D6 => "D6",
            Key.D7 => "D7",
            Key.D8 => "D8",
            Key.D9 => "D9",
            _ => key.ToString()
        };
    }
}
