using System.Net.Http;
using System.Windows;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Services;
using Blitztext.Core.Workflows;
using Blitztext.LocalTranscription;
using Blitztext.Platform.Windows;
using Forms = System.Windows.Forms;

namespace Blitztext.Windows;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private StatusOverlayWindow? _overlay;
    private WindowsHotkeyService? _hotkeyService;
    private WindowsAudioRecorder? _recorder;
    private BlitztextWorkflowRunner? _runner;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        WindowsPaths.EnsureDirectories();

        var secretStore = new CredentialManagerSecretStore();
        var settingsStore = new JsonSettingsStore(WindowsPaths.SettingsPath);
        var localTranscription = new WhisperCppLocalTranscriptionService(WindowsPaths.AppDataDirectory);
        _recorder = new WindowsAudioRecorder();
        _hotkeyService = new WindowsHotkeyService();

        var remoteTranscription = new OpenAITranscriptionClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(75) },
            cancellationToken => secretStore.LoadAsync(SecretKey.OpenAIApiKey, cancellationToken));
        var rewriteClient = new OpenAITextRewriteClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            cancellationToken => secretStore.LoadAsync(SecretKey.OpenAIApiKey, cancellationToken));

        _runner = new BlitztextWorkflowRunner(_recorder, remoteTranscription, localTranscription, rewriteClient);
        var pasteService = new WindowsPasteService();
        var executablePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? "Blitztext.exe";
        var startupService = new RegistryStartupService(executablePath);

        _mainWindow = new MainWindow(
            settingsStore,
            secretStore,
            localTranscription,
            _runner,
            pasteService,
            startupService,
            _hotkeyService);
        await _mainWindow.InitializeAsync();

        _overlay = new StatusOverlayWindow
        {
            AudioLevelProvider = () => _runner?.AudioLevel ?? 0f,
            RecordingProvider = () => _runner?.IsRecording ?? false
        };
        _overlay.ShowLastTextRequested += (_, _) => _mainWindow.ShowLastDictatedText();
        _overlay.OpenHotkeysRequested += (_, _) => _mainWindow.ShowHotkeyWindow();
        _overlay.OpenSettingsRequested += (_, _) => ShowMainWindow();
        _overlay.QuitRequested += (_, _) => Shutdown();
        _overlay.Moved += (_, point) => _mainWindow.SaveOverlayPosition(point.X, point.Y);
        _runner.PhaseChanged += (_, phase) =>
            _overlay.Dispatcher.BeginInvoke(() => _overlay.SetPhase(phase));
        _mainWindow.StatusChanged += (_, phase) =>
            _overlay.Dispatcher.BeginInvoke(() => _overlay.SetPhase(phase));

        if (_mainWindow.GetOverlayPosition() is { } savedPosition)
        {
            _overlay.SetInitialPosition(savedPosition.Left, savedPosition.Top);
        }

        _overlay.Show();

        _hotkeyService.Hotkey += async (_, hotkeyEvent) =>
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.HandleHotkeyAsync(hotkeyEvent);
            }
        };
        _hotkeyService.Start();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = $"Blitztext {AppInfo.DisplayVersion}",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _notifyIcon.Click += (_, args) =>
        {
            if (args is Forms.MouseEventArgs mouseEvent && mouseEvent.Button != Forms.MouseButtons.Left)
            {
                return;
            }

            ShowMainWindow();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _hotkeyService?.Dispose();
        _recorder?.Dispose();
        base.OnExit(e);
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Tastenkürzel", null, (_, _) => _mainWindow?.ShowHotkeyWindow());
        menu.Items.Add("Einstellungen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Beenden", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.PrepareForManualInteraction();
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}
