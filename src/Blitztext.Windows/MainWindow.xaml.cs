using System.Windows;
using System.Windows.Controls;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using Blitztext.Core.Workflows;

namespace Blitztext.Windows;

public partial class MainWindow : Window
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly ILocalModelService _localModelService;
    private readonly BlitztextWorkflowRunner _runner;
    private readonly IPasteService _pasteService;
    private readonly IStartupService _startupService;
    private readonly IHotkeyService _hotkeyService;
    private SettingsContainer _settings = new();
    private HotkeyWindow? _hotkeyWindow;
    private bool _isLoading;
    private bool _isStartingOrStopping;
    private IntPtr _pasteTarget;

    /// <summary>
    /// Raised for status the background pill should reflect even when the settings window
    /// is hidden (e.g. a pre-flight error like a missing API key in push-to-talk mode).
    /// </summary>
    public event EventHandler<WorkflowPhase>? StatusChanged;

    public MainWindow(
        ISettingsStore settingsStore,
        ISecretStore secretStore,
        ILocalModelService localModelService,
        BlitztextWorkflowRunner runner,
        IPasteService pasteService,
        IStartupService startupService,
        IHotkeyService hotkeyService)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _localModelService = localModelService;
        _runner = runner;
        _pasteService = pasteService;
        _startupService = startupService;
        _hotkeyService = hotkeyService;

        InitializeComponent();
        VersionText.Text = AppInfo.DisplayVersion;
        Title = $"Blitztext {AppInfo.DisplayVersion}";
        _runner.PhaseChanged += (_, phase) => Dispatcher.Invoke(() => ApplyPhase(phase));
        _runner.OutputProduced += async (_, text) => await Dispatcher.InvokeAsync(async () => await PasteWorkflowOutputAsync(text));
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            WorkflowSettings.EnsureDefaults(_settings.App);

            HotkeyModeCombo.ItemsSource = Enum.GetValues<HotkeyMode>();
            ToneCombo.ItemsSource = Enum.GetValues<TextTone>();
            EmojiDensityCombo.ItemsSource = Enum.GetValues<EmojiDensity>();

            HotkeyModeCombo.SelectedItem = _settings.App.HotkeyMode;
            ToneCombo.SelectedItem = _settings.TextImprovement.Tone;
            EmojiDensityCombo.SelectedItem = _settings.EmojiText.EmojiDensity;
            SecureLocalModeCheckBox.IsChecked = _settings.App.SecureLocalModeEnabled;
            LanguageTextBox.Text = _settings.Transcription.Language;
            CustomTermsTextBox.Text = string.Join(", ", _settings.TextImprovement.CustomTerms);
            ContextTextBox.Text = _settings.TextImprovement.Context;
            ImproverPromptTextBox.Text = _settings.TextImprovement.SystemPrompt;
            DampfPromptTextBox.Text = _settings.DampfAblassen.SystemPrompt;
            StartupCheckBox.IsChecked = _startupService.IsEnabled();

            _hotkeyService.Mode = _settings.App.HotkeyMode;
            ConfigureHotkeys();
            _hotkeyWindow?.RefreshFromSettings();
            RefreshLocalRuntime();
            RefreshLocalModels();
            await RefreshCredentialStateAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void PrepareForManualInteraction()
    {
        _pasteTarget = _pasteService.CaptureCurrentTarget();
    }

    /// <summary>Opens the slim, dedicated shortcut editor (also reachable from the pill and tray).</summary>
    public void ShowHotkeyWindow()
    {
        _hotkeyWindow ??= new HotkeyWindow(() => _settings, ApplyHotkeyChangesAsync, _hotkeyService);
        _hotkeyWindow.RefreshFromSettings();
        _hotkeyWindow.Show();
        _hotkeyWindow.WindowState = WindowState.Normal;
        _hotkeyWindow.Activate();
    }

    private async Task ApplyHotkeyChangesAsync()
    {
        _hotkeyService.Mode = _settings.App.HotkeyMode;
        ConfigureHotkeys();
        await _settingsStore.SaveAsync(_settings);
    }

    /// <summary>Last saved position of the floating pill, or null to use the default.</summary>
    public (double Left, double Top)? GetOverlayPosition()
    {
        return _settings.App.OverlayLeft is double left && _settings.App.OverlayTop is double top
            ? (left, top)
            : null;
    }

    public void SaveOverlayPosition(double left, double top)
    {
        _settings.App.OverlayLeft = left;
        _settings.App.OverlayTop = top;
        _ = _settingsStore.SaveAsync(_settings);
    }

    public async Task HandleHotkeyAsync(HotkeyEvent hotkeyEvent)
    {
        if (hotkeyEvent.WorkflowType is null || !IsWorkflowEnabled(hotkeyEvent.WorkflowType.Value))
        {
            return;
        }

        if (hotkeyEvent.Kind == HotkeyEventKind.Down)
        {
            if (_settings.App.HotkeyMode == HotkeyMode.Toggle && _runner.Phase.IsActive)
            {
                await StopWorkflowAsync();
                return;
            }

            _pasteTarget = _pasteService.CaptureCurrentTarget();
            await StartWorkflowAsync(hotkeyEvent.WorkflowType.Value, showWindow: _settings.App.HotkeyMode == HotkeyMode.Toggle);
            return;
        }

        if (hotkeyEvent.Kind == HotkeyEventKind.Up && _settings.App.HotkeyMode == HotkeyMode.Hold)
        {
            await StopWorkflowAsync();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OpenHotkeysButton_Click(object sender, RoutedEventArgs e) => ShowHotkeyWindow();

    private async void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            await _secretStore.SaveAsync(SecretKey.OpenAIApiKey, key);
            ApiKeyBox.Clear();
        }

        await SaveSettingsFromUiAsync();
        await RefreshCredentialStateAsync();
        StatusText.Text = "Einstellungen gespeichert.";
    }

    private async void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _startupService.SetEnabled(StartupCheckBox.IsChecked == true);
        await SaveSettingsFromUiAsync();
    }

    private async void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSettingsFromUiAsync();
    }

    private async void SettingsTextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSettingsFromUiAsync();
    }

    private async void InstallModelButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedModel = LocalModelCombo.SelectedValue as string ?? _settings.App.SelectedLocalTranscriptionModelName;
        ModelProgressBar.Value = 0;
        ModelProgressBar.Visibility = Visibility.Visible;
        InstallModelButton.IsEnabled = false;
        LocalModelStatusText.Text = "Download startet ...";

        try
        {
            var progress = new Progress<double>(value =>
            {
                ModelProgressBar.Value = value;
                LocalModelStatusText.Text = $"Download {Math.Round(value * 100)} %";
            });
            var installed = await _localModelService.InstallAsync(selectedModel, progress);
            _settings.App.SelectedLocalTranscriptionModelName = installed.Id;
            await _settingsStore.SaveAsync(_settings);
            RefreshLocalModels();
            LocalModelStatusText.Text = $"{installed.DisplayName} ist installiert.";
        }
        catch (Exception ex)
        {
            LocalModelStatusText.Text = ex.Message;
        }
        finally
        {
            ModelProgressBar.Visibility = Visibility.Collapsed;
            InstallModelButton.IsEnabled = true;
        }
    }

    private async void InstallRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeProgressBar.Value = 0;
        RuntimeProgressBar.Visibility = Visibility.Visible;
        InstallRuntimeButton.IsEnabled = false;
        LocalRuntimeStatusText.Text = "whisper.cpp wird geladen ...";

        try
        {
            var progress = new Progress<double>(value =>
            {
                RuntimeProgressBar.Value = value;
                LocalRuntimeStatusText.Text = $"Runtime-Download {Math.Round(value * 100)} %";
            });
            var runtime = await _localModelService.InstallRuntimeAsync(progress);
            LocalRuntimeStatusText.Text = runtime.IsInstalled
                ? $"Installiert: {runtime.ExecutablePath}"
                : "Runtime konnte nicht installiert werden.";
        }
        catch (Exception ex)
        {
            LocalRuntimeStatusText.Text = ex.Message;
        }
        finally
        {
            RuntimeProgressBar.Visibility = Visibility.Collapsed;
            InstallRuntimeButton.IsEnabled = true;
            RefreshLocalRuntime();
        }
    }

    private async void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        await _secretStore.DeleteAsync(SecretKey.OpenAIApiKey);
        _settings = new SettingsContainer();
        WorkflowSettings.EnsureDefaults(_settings.App);
        await _settingsStore.SaveAsync(_settings);
        await InitializeAsync();
        StatusText.Text = "Lokale Einstellungen wurden zurückgesetzt.";
    }

    private async Task StartWorkflowAsync(WorkflowType type, bool showWindow)
    {
        if (_isStartingOrStopping)
        {
            return;
        }

        await SaveSettingsFromUiAsync();
        var availabilityError = await AvailabilityErrorAsync(type);
        if (availabilityError is not null)
        {
            StatusText.Text = availabilityError;
            StatusChanged?.Invoke(this, WorkflowPhase.Error(availabilityError));
            if (showWindow)
            {
                Show();
                Activate();
            }
            return;
        }

        try
        {
            _isStartingOrStopping = true;
            await _runner.StartAsync(type, _settings);
            if (showWindow)
            {
                Show();
                Activate();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusChanged?.Invoke(this, WorkflowPhase.Error(ex.Message));
        }
        finally
        {
            _isStartingOrStopping = false;
        }
    }

    private async Task StopWorkflowAsync()
    {
        if (_isStartingOrStopping || !_runner.Phase.IsActive)
        {
            return;
        }

        try
        {
            _isStartingOrStopping = true;
            await _runner.StopAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _isStartingOrStopping = false;
        }
    }

    private async Task PasteWorkflowOutputAsync(string text)
    {
        Hide();
        try
        {
            if (_pasteTarget != IntPtr.Zero)
            {
                await _pasteService.PasteAsync(text, _pasteTarget);
                StatusText.Text = "Eingefügt.";
            }
            else
            {
                await _pasteService.CopyAsync(text);
                StatusText.Text = "Kein Zielfenster – in die Zwischenablage kopiert (Strg+V).";
                StatusChanged?.Invoke(this, WorkflowPhase.Error("In Zwischenablage · Strg+V"));
            }
        }
        catch (Exception ex)
        {
            try
            {
                await _pasteService.CopyAsync(text);
            }
            catch
            {
                // Clipboard fallback is best-effort.
            }

            StatusText.Text = $"Einfügen fehlgeschlagen: {ex.Message}. Text ist in der Zwischenablage (Strg+V).";
            StatusChanged?.Invoke(this, WorkflowPhase.Error("Einfügen ging nicht · Strg+V"));
        }
    }

    private void ApplyPhase(WorkflowPhase phase)
    {
        StatusText.Text = phase.Kind switch
        {
            WorkflowPhaseKind.Idle => "Bereit",
            WorkflowPhaseKind.Running => phase.Message,
            WorkflowPhaseKind.Done => "Fertig.",
            WorkflowPhaseKind.Error => phase.Message,
            _ => phase.Message
        };
    }

    private async Task SaveSettingsFromUiAsync()
    {
        if (_isLoading)
        {
            return;
        }

        WorkflowSettings.EnsureDefaults(_settings.App);
        _settings.App.SecureLocalModeEnabled = SecureLocalModeCheckBox.IsChecked == true;
        _settings.App.HotkeyMode = HotkeyModeCombo.SelectedItem is HotkeyMode mode ? mode : HotkeyMode.Hold;
        _settings.Transcription.Language = string.IsNullOrWhiteSpace(LanguageTextBox.Text) ? "de" : LanguageTextBox.Text.Trim();
        _settings.TextImprovement.Tone = ToneCombo.SelectedItem is TextTone tone ? tone : TextTone.Neutral;
        _settings.TextImprovement.CustomTerms = CustomTermsTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.TextImprovement.Context = ContextTextBox.Text.Trim();
        _settings.TextImprovement.SystemPrompt = ImproverPromptTextBox.Text.Trim();
        _settings.DampfAblassen.SystemPrompt = DampfPromptTextBox.Text.Trim();
        _settings.EmojiText.EmojiDensity = EmojiDensityCombo.SelectedItem is EmojiDensity density ? density : EmojiDensity.Mittel;

        if (LocalModelCombo.SelectedValue is string selectedModel)
        {
            _settings.App.SelectedLocalTranscriptionModelName = selectedModel;
        }

        _hotkeyService.Mode = _settings.App.HotkeyMode;
        await _settingsStore.SaveAsync(_settings);
    }

    private async Task<string?> AvailabilityErrorAsync(WorkflowType type)
    {
        if (!IsWorkflowEnabled(type))
        {
            return $"{type.DisplayName()} ist deaktiviert.";
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(await _secretStore.LoadAsync(SecretKey.OpenAIApiKey));
        var selectedLocalModelInstalled = _localModelService.IsModelInstalled(_settings.App.SelectedLocalTranscriptionModelName);
        var localRuntimeInstalled = _localModelService.GetRuntimeInfo().IsInstalled;

        return type switch
        {
            WorkflowType.LocalTranscription when !localRuntimeInstalled => "whisper.cpp fehlt. Installiere die Runtime im Bereich 'Lokales Whisper'.",
            WorkflowType.LocalTranscription when !selectedLocalModelInstalled => "Lokales Whisper-Modell fehlt.",
            WorkflowType.Transcription when _settings.App.SecureLocalModeEnabled && !localRuntimeInstalled => "whisper.cpp fehlt. Installiere die Runtime im Bereich 'Lokales Whisper'.",
            WorkflowType.Transcription when _settings.App.SecureLocalModeEnabled && !selectedLocalModelInstalled => "Lokales Whisper-Modell fehlt.",
            WorkflowType.Transcription when !_settings.App.SecureLocalModeEnabled && !hasApiKey => "OpenAI API Key fehlt.",
            WorkflowType.TextImprover or WorkflowType.DampfAblassen or WorkflowType.EmojiText when _settings.App.SecureLocalModeEnabled => "Rewrite-Workflows sind im sicheren lokalen Modus pausiert.",
            WorkflowType.TextImprover or WorkflowType.DampfAblassen or WorkflowType.EmojiText when !hasApiKey => "OpenAI API Key fehlt.",
            _ => null
        };
    }

    private async Task RefreshCredentialStateAsync()
    {
        var key = await _secretStore.LoadAsync(SecretKey.OpenAIApiKey);
        ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(key)
            ? "Noch kein API Key gespeichert."
            : $"Gespeichert: {MaskKey(key)}";
    }

    private void RefreshLocalModels()
    {
        var models = _localModelService.GetModelOptions();
        LocalModelCombo.ItemsSource = models;
        LocalModelCombo.DisplayMemberPath = nameof(LocalModelInfo.DisplayName);
        LocalModelCombo.SelectedValuePath = nameof(LocalModelInfo.Id);
        LocalModelCombo.SelectedValue = _settings.App.SelectedLocalTranscriptionModelName;

        var selected = models.FirstOrDefault(model => model.Id == _settings.App.SelectedLocalTranscriptionModelName);
        LocalModelStatusText.Text = selected is null
            ? "Kein lokales Modell ausgewählt."
            : selected.IsInstalled
                ? $"{selected.DisplayName} ist installiert."
                : $"{selected.DisplayName} ist noch nicht installiert. whisper-cli.exe muss zusätzlich lokal bereitliegen.";
    }

    private void RefreshLocalRuntime()
    {
        var runtime = _localModelService.GetRuntimeInfo();
        LocalRuntimeStatusText.Text = runtime.IsInstalled
            ? $"Installiert: {runtime.ExecutablePath}"
            : "Nicht installiert. Für lokale Transkription wird whisper-cli.exe benötigt.";
        InstallRuntimeButton.Content = runtime.IsInstalled ? "whisper.cpp erneut installieren" : "whisper.cpp installieren";
    }

    private void ConfigureHotkeys()
    {
        WorkflowSettings.EnsureDefaults(_settings.App);
        var activeBindings = _settings.App.Workflows
            .Where(item => item.Value.Enabled)
            .ToDictionary(item => item.Key, item => item.Value.Hotkey);

        _hotkeyService.Configure(activeBindings);
    }

    private bool IsWorkflowEnabled(WorkflowType workflowType)
    {
        WorkflowSettings.EnsureDefaults(_settings.App);
        return _settings.App.Workflows[workflowType].Enabled;
    }

    private static string MaskKey(string key)
    {
        return key.Length <= 8 ? "********" : $"{key[..4]} ********";
    }
}
