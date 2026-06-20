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

        HookGlobalExceptionHandlers();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            WindowsPaths.EnsureDirectories();
            await StartupAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup", ex);
            System.Windows.MessageBox.Show(
                $"Blitztext konnte nicht starten.\n\n{ex.Message}\n\nDetails: {WindowsPaths.LogPath}",
                "Blitztext",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Routes unexpected exceptions to the log instead of letting them tear down the tray app.
    /// A failed dictation or paste should abort that one action, not kill Blitztext. (Note: native
    /// corrupted-state exceptions such as access violations bypass these handlers, which is why the
    /// clipboard snapshot is sanitized rather than relying on catching them.)
    /// </summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Dispatcher", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("AppDomain", args.ExceptionObject as Exception,
                args.IsTerminating ? "Prozess wird beendet." : null);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Task", args.Exception);
            args.SetObserved();
        };
    }

    private async Task StartupAsync()
    {
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

        await _mainWindow.MaybeShowFirstRunOnboardingAsync();
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
