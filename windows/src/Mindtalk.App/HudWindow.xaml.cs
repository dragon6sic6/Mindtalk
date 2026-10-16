using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Interop;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Windows.Foundation;
using Windows.Graphics;

namespace Mindtalk.App;

/// <summary>
/// The pill at the bottom of the screen while you dictate: a live level meter, then
/// "writing…", or what went wrong. Never takes focus from the app you're typing in.
/// </summary>
public sealed partial class HudWindow : Window
{
    private const int BarCount = 9;
    private const double PillHeight = 44;

    private readonly DictationController _dictation;
    private readonly Func<string> _keyName;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly float[] _levels = new float[BarCount];
    private readonly DispatcherQueueTimer _meter;
    private readonly nint _hwnd;
    private volatile float _latestLevel;
    private bool _shown;

    public HudWindow(DictationController dictation, Func<string> keyName)
    {
        InitializeComponent();
        _dictation = dictation;
        _keyName = keyName;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Center };
            _bars[i].SetValue(Shape.FillProperty, Application.Current.Resources["TextFillColorPrimaryBrush"]);
            Bars.Children.Add(_bars[i]);
        }

        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Clicks on âœ• / âœ“ still work, but the window never becomes the active one.
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE,
            (nint)((long)style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST));
        var round = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        _meter = DispatcherQueue.CreateTimer();
        _meter.Interval = TimeSpan.FromMilliseconds(33);
        _meter.Tick += (_, _) => PushLevel(_latestLevel);

        dictation.StateChanged += Update;
        dictation.LevelChanged += level => _latestLevel = level;
    }

    private void Update(DictationState state)
    {
        if (!state.Visible)
        {
            _meter.Stop();
            if (_shown) AppWindow.Hide();
            _shown = false;
            return;
        }

        var recording = state.Phase == Phase.Recording;
        Bars.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Spinner.Visibility = state.Phase == Phase.Transcribing ? Visibility.Visible : Visibility.Collapsed;
        Spinner.IsActive = state.Phase == Phase.Transcribing;
        CancelButton.Visibility = FinishButton.Visibility =
            recording && state.HandsFree ? Visibility.Visible : Visibility.Collapsed;
        Label.Text = state.Phase switch
        {
            Phase.Recording when state.HandsFree => Loc.T($"Låst – tryck {_keyName()} när du är klar",
                                                          $"Locked — press {_keyName()} when done"),
            Phase.Recording => Loc.T("Lyssnar…", "Listening…"),
            Phase.Transcribing => Loc.T("Skriver…", "Writing…"),
            _ => state.Message ?? "",
        };
        if (recording) { Array.Clear(_levels); _meter.Start(); } else _meter.Stop();

        Place();
        if (!_shown)
        {
            AppWindow.Show(false);
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            _shown = true;
        }
    }

    private void PushLevel(float level)
    {
        Array.Copy(_levels, 1, _levels, 0, BarCount - 1);
        _levels[^1] = level;
        for (var i = 0; i < BarCount; i++) _bars[i].Height = 3 + 19 * Math.Clamp(_levels[i], 0, 1);
    }

    /// Bottom centre of the screen you're working on, just above the taskbar.
    private void Place()
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling(Math.Max(Pill.DesiredSize.Width, 120) * scale);
        var height = (int)Math.Ceiling(Math.Max(PillHeight, Pill.DesiredSize.Height + 12) * scale);

        var foreground = Win32Interop.GetWindowIdFromWindow(Win32.GetForegroundWindow());
        var area = DisplayArea.GetFromWindowId(foreground, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + area.Height - height - (int)(24 * scale),
            width, height));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _dictation.CancelFromHud();
    private void OnFinish(object sender, RoutedEventArgs e) => _dictation.FinishFromHud();
}
