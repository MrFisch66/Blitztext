using System.Windows;
using System.Windows.Threading;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.Windows;

/// <summary>
/// Slim, dedicated window for editing the per-workflow shortcuts, reachable from the pill's
/// right-click menu and the tray. The actual editing UI lives in <see cref="HotkeyEditorControl"/>,
/// which is shared with the settings window's "Tastenkürzel" section.
/// </summary>
public partial class HotkeyWindow : Window
{
    public HotkeyWindow(
        Func<SettingsContainer> getSettings,
        Func<Task> applyChangesAsync,
        IHotkeyService hotkeyService)
    {
        InitializeComponent();
        Title = $"Blitztext – Tastenkürzel · {AppInfo.DisplayVersion}";
        Editor.Initialize(getSettings, applyChangesAsync, hotkeyService);
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public void RefreshFromSettings() => Editor.RefreshFromSettings();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Keep the window alive so it can be reopened; just hide it.
        e.Cancel = true;
        Editor.Deactivate();
        Hide();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Editor.RefreshFromSettings();
            // Park focus on the Close button so no shortcut field is "armed" until clicked.
            Dispatcher.BeginInvoke(new Action(() => CloseButton.Focus()), DispatcherPriority.Input);
        }
        else
        {
            Editor.Deactivate();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();
}
