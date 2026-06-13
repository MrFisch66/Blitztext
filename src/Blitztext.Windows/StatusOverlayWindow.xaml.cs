using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Blitztext.Core.Models;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Blitztext.Windows;

/// <summary>
/// Small always-on-top "pill" docked at the bottom of the screen. It is the only
/// permanently visible part of Blitztext while it runs in the background: it shows
/// the current state (idle / recording / processing) and turns green while recording.
/// </summary>
public partial class StatusOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int BarCount = 5;

    private static readonly Color IdleBackground = (Color)ColorConverter.ConvertFromString("#E6111827");
    private static readonly Color RecordingBackground = (Color)ColorConverter.ConvertFromString("#E6052E1B");
    private static readonly Color ProcessingBackground = (Color)ColorConverter.ConvertFromString("#E6422006");
    private static readonly Color ErrorBackground = (Color)ColorConverter.ConvertFromString("#E64C0519");

    private static readonly Color IdleDot = (Color)ColorConverter.ConvertFromString("#94A3B8");
    private static readonly Color RecordingColor = (Color)ColorConverter.ConvertFromString("#22C55E");
    private static readonly Color ProcessingColor = (Color)ColorConverter.ConvertFromString("#F59E0B");
    private static readonly Color ErrorColor = (Color)ColorConverter.ConvertFromString("#EF4444");

    private static readonly double[] BarFactors = [0.55, 0.85, 1.0, 0.8, 0.5];

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly float[] _barLevels = new float[BarCount];
    private readonly Random _random = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _revertTimer;
    private readonly string _idleLabel = $"Blitztext {AppInfo.DisplayVersion}";

    private VisualState _state = VisualState.Idle;

    private Point _dragStart;
    private bool _mouseDown;
    private bool _dragging;
    private bool _userPositioned;
    private double? _pendingLeft;
    private double? _pendingTop;

    public StatusOverlayWindow()
    {
        InitializeComponent();
        BuildLevelBars();
        StatusLabel.Text = _idleLabel;

        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _animationTimer.Tick += (_, _) => UpdateActiveVisual();

        _revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _revertTimer.Tick += (_, _) =>
        {
            _revertTimer.Stop();
            ApplyIdle();
        };

        Loaded += (_, _) => ApplyInitialPosition();
        SizeChanged += (_, _) =>
        {
            // Keep it centered as the label width changes — but only until the user moves it.
            if (!_userPositioned)
            {
                PositionAtBottomCenter();
            }
        };
    }

    /// <summary>Provides the current microphone peak (0..1) used to animate the level bars.</summary>
    public Func<float>? AudioLevelProvider { get; set; }

    /// <summary>Reports whether audio is being captured right now (recording vs. processing).</summary>
    public Func<bool>? RecordingProvider { get; set; }

    /// <summary>Left-click / "Tastenkürzel" — opens the slim shortcut editor.</summary>
    public event EventHandler? OpenHotkeysRequested;

    /// <summary>"Einstellungen" — opens the full settings window.</summary>
    public event EventHandler? OpenSettingsRequested;

    public event EventHandler? QuitRequested;

    /// <summary>Raised after the user drags the pill, with its new screen position.</summary>
    public event EventHandler<Point>? Moved;

    /// <summary>Restores a previously saved position (call before showing the window).</summary>
    public void SetInitialPosition(double left, double top)
    {
        _pendingLeft = left;
        _pendingTop = top;
        _userPositioned = true;
    }

    public void SetPhase(WorkflowPhase phase)
    {
        _revertTimer.Stop();

        switch (phase.Kind)
        {
            case WorkflowPhaseKind.Running:
                if (!_animationTimer.IsEnabled)
                {
                    _animationTimer.Start();
                }

                UpdateActiveVisual();
                break;

            case WorkflowPhaseKind.Done:
                StopAnimation();
                ApplyVisual(VisualState.Recording, "Fertig");
                ScheduleRevert(TimeSpan.FromSeconds(1.2));
                break;

            case WorkflowPhaseKind.Error:
                StopAnimation();
                ApplyVisual(VisualState.Error, ShortMessage(phase.Message));
                ScheduleRevert(TimeSpan.FromSeconds(2.8));
                break;

            default:
                StopAnimation();
                ApplyIdle();
                break;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Never take focus (so dictation lands in the app the user was typing in) and
        // stay out of Alt+Tab.
        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow);
    }

    private void BuildLevelBars()
    {
        var fill = new SolidColorBrush(RecordingColor);
        for (var index = 0; index < BarCount; index++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(index == 0 ? 0 : 2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = fill
            };
            _bars[index] = bar;
            LevelBars.Children.Add(bar);
        }
    }

    private void UpdateActiveVisual()
    {
        var recording = RecordingProvider?.Invoke() ?? false;
        if (recording)
        {
            ApplyVisual(VisualState.Recording, "Höre zu …");
            AnimateBars(AudioLevelProvider?.Invoke() ?? 0f);
        }
        else
        {
            ApplyVisual(VisualState.Processing, "Verarbeite …");
        }
    }

    private void AnimateBars(float level)
    {
        for (var index = 0; index < BarCount; index++)
        {
            var jitter = (float)(_random.NextDouble() * 0.25);
            var target = Math.Clamp((level + jitter) * (float)BarFactors[index], 0f, 1f);
            _barLevels[index] += (target - _barLevels[index]) * 0.55f;
            _bars[index].Height = 3 + (_barLevels[index] * 13);
        }
    }

    private void ApplyVisual(VisualState state, string label)
    {
        StatusLabel.Text = label;

        if (state == _state)
        {
            return;
        }

        _state = state;
        var (background, accent, foreground, showBars) = state switch
        {
            VisualState.Recording => (RecordingBackground, RecordingColor, "#DCFCE7", true),
            VisualState.Processing => (ProcessingBackground, ProcessingColor, "#FDE68A", false),
            VisualState.Error => (ErrorBackground, ErrorColor, "#FECACA", false),
            _ => (IdleBackground, IdleDot, "#E5E7EB", false)
        };

        PillBorder.Background = new SolidColorBrush(background);
        StatusDot.Fill = new SolidColorBrush(accent);
        StatusLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        LevelBars.Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;

        if (!showBars)
        {
            Array.Clear(_barLevels);
            foreach (var bar in _bars)
            {
                bar.Height = 3;
            }
        }
    }

    private void ApplyIdle() => ApplyVisual(VisualState.Idle, _idleLabel);

    private void ScheduleRevert(TimeSpan delay)
    {
        _revertTimer.Stop();
        _revertTimer.Interval = delay;
        _revertTimer.Start();
    }

    private void StopAnimation()
    {
        if (_animationTimer.IsEnabled)
        {
            _animationTimer.Stop();
        }
    }

    private void ApplyInitialPosition()
    {
        if (_userPositioned && _pendingLeft is double left && _pendingTop is double top)
        {
            var (clampedLeft, clampedTop) = ClampToScreen(left, top);
            Left = clampedLeft;
            Top = clampedTop;
        }
        else
        {
            PositionAtBottomCenter();
        }
    }

    private void PositionAtBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - 12;
    }

    private (double Left, double Top) ClampToScreen(double left, double top)
    {
        var width = ActualWidth > 0 ? ActualWidth : 120;
        var height = ActualHeight > 0 ? ActualHeight : 40;
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + SystemParameters.VirtualScreenWidth - width;
        var maxTop = minTop + SystemParameters.VirtualScreenHeight - height;
        return (Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft)),
                Math.Clamp(top, minTop, Math.Max(minTop, maxTop)));
    }

    private static string ShortMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Fehler";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 38 ? trimmed : trimmed[..37] + "…";
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _mouseDown = true;
        _dragging = false;
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || _dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragging = true;
        _userPositioned = true;
        _mouseDown = false;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove throws if the mouse button was already released; ignore.
        }

        var (left, top) = ClampToScreen(Left, Top);
        Left = left;
        Top = top;
        Moved?.Invoke(this, new Point(left, top));
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        _mouseDown = false;
        _dragging = false;

        // A plain click (no drag) opens the shortcut editor.
        if (!wasDragging)
        {
            OpenHotkeysRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HotkeysMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenHotkeysRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void QuitMenuItem_Click(object sender, RoutedEventArgs e)
        => QuitRequested?.Invoke(this, EventArgs.Empty);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private enum VisualState
    {
        Idle,
        Recording,
        Processing,
        Error
    }
}
