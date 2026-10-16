using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.App.Interop;
using Mindtalk.App.TrayPanel;
using Windows.Foundation;
using Windows.Graphics;

namespace Mindtalk.App;

/// <summary>
/// The tray panel (the Mac's menu bar panel). Clicking Mindtalk in the tray (left or right)
/// opens it next to the icon. It is built like a menu — rows with a quiet hover, round badges
/// filled when chosen, keyboard shortcuts at the trailing edge — but dressed like the app: its
/// logo, its keycaps, its serif numbers, and room to breathe. It takes focus, so Esc and the
/// shortcuts work, and closes when you click anywhere else.
/// </summary>
public sealed class StatusPanel
{
    /// Transparent room around the card for its shadow, in points.
    private const double Room = 28;
    /// Space between the panel and the taskbar, and the least space to the screen's edges.
    private const double Gap = 6, EdgeMargin = 8;

    private enum Edge { Bottom, Top, Left, Right }

    private readonly App _app;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private Window? _window;
    private nint _hwnd;
    private PanelView? _view;
    private DispatcherQueueTimer? _clock;
    private Rect _anchor;
    private RectInt32 _placed;
    private int _generation;
    /// Until when a click on the tray icon is the one that just closed the panel.
    private long _ignoreToggleUntil;

    public StatusPanel(App app)
    {
        _app = app;
        _app.Changed += () =>
        {
            if (!IsShown) return;
            _view?.Update();
            Place();
        };
        _app.ThemeChanged += () =>
        {
            if (_window is null) return;
            Build();
            if (IsShown) Place();
        };
    }

    public bool IsShown { get; private set; }

    /// Opens next to the tray icon at <paramref name="anchor"/> (physical pixels), or closes.
    public void Toggle(Rect anchor)
    {
        if (IsShown) { Close(); return; }
        // Clicking the icon to close the panel takes its focus first, which already closed it.
        if (Environment.TickCount64 < _ignoreToggleUntil) { _ignoreToggleUntil = 0; return; }
        Show(anchor);
    }

    private void Show(Rect anchor)
    {
        Prepare();
        _anchor = anchor;
        IsShown = true;
        _generation++;
        _view!.Live = true;
        _placed = default;
        Place();
        _clock!.Start();

        var visual = ElementCompositionPreview.GetElementVisual(_root);
        visual.Opacity = 0;
        _window!.AppWindow.Show(true);
        _window.Activate();
        // Take focus so Esc and the keyboard reach the panel (a tray app has no window in front).
        Win32.SetForegroundWindow(_hwnd);
        _root.Focus(FocusState.Programmatic);
        Fade(1, 0.16, null);
    }

    public void Close()
    {
        if (!IsShown) return;
        IsShown = false;
        _clock?.Stop();
        if (_view is not null) _view.Live = false;
        var generation = ++_generation;
        Fade(0, 0.12, () =>
        {
            if (generation == _generation) _window?.AppWindow.Hide();
        });
    }

    private void Fade(double to, double seconds, Action? done)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        if (Motion.Reduced)
        {
            visual.Opacity = (float)to;
            done?.Invoke();
            return;
        }
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, (float)to, compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
        fade.Duration = TimeSpan.FromSeconds(seconds);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        if (done is not null) batch.Completed += (_, _) => done();
    }

    // ---- The window -----------------------------------------------------------------

    /// A borderless, transparent, topmost window — the card and its shadow are all there is
    /// of it — that never shows in Alt+Tab or the taskbar. (Made like the HUD's.)
    private void Prepare()
    {
        if (_window is not null) return;
        _window = new Window { Content = _root, SystemBackdrop = new TransparentBackdrop() };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Title = "Mindtalk";

        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (nint)((long)style | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST));
        var square = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        var noBorder = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        // The frame "extended" over the whole window: where nothing is drawn, DWM shows
        // what's behind instead of the window's black background.
        var sheet = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(_hwnd, ref sheet);

        // Alt+F4 closes the panel, not the window.
        _window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Close();
        };
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || !IsShown) return;
#if DEBUG
            if (Demo.On) return;   // screenshots: stay open whatever else takes focus
#endif
            // Closes on any click outside — remembering when that click was on the tray icon,
            // which is about to ask for the panel again.
            Win32.GetCursorPos(out var cursor);
            if (_anchor.Contains(new Point(cursor.X, cursor.Y))) _ignoreToggleUntil = Environment.TickCount64 + 1000;
            Close();
        };
        // A click in the shadow around the card is a click outside it.
        _root.PointerPressed += (_, e) => { if (ReferenceEquals(e.OriginalSource, _root)) Close(); };

        // "4 min" moves on while the panel is open.
        _clock = _app.UI.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(20);
        _clock.Tick += (_, _) => _view?.Update();
        Build();
    }

    /// Draws the content again, in the app's appearance.
    private void Build()
    {
        if (_view is not null) _view.Live = false;
        _root.RequestedTheme = UI.Theme;
        _root.Children.Clear();
        _view = new PanelView(_app, Close, Room);
        _root.Children.Add(_view.Root);
        _view.Live = IsShown;
    }

    /// Next to the tray icon, on the side of the screen the taskbar is on: centred on the icon,
    /// kept inside the work area, just clear of the taskbar. Called again when the content
    /// changes size, so the edge by the taskbar stays put.
    private void Place()
    {
        if (_window is null || _view is null) return;
        var centre = new PointInt32((int)(_anchor.X + _anchor.Width / 2), (int)(_anchor.Y + _anchor.Height / 2));
        var display = DisplayArea.GetFromPoint(centre, DisplayAreaFallback.Nearest);
        RectInt32 work = display.WorkArea, full = display.OuterBounds;
        var scale = Win32.GetDpiForMonitor(display) / 96.0;

        _view.Card.Measure(new Size(Metrics.Width, double.PositiveInfinity));
        double width = Metrics.Width * scale, height = Math.Ceiling(_view.Card.DesiredSize.Height) * scale;
        double gap = Gap * scale, margin = EdgeMargin * scale, room = Room * scale;
        double left = work.X, top = work.Y, right = work.X + work.Width, bottom = work.Y + work.Height;
        double x = Math.Clamp(centre.X - width / 2, left + margin, Math.Max(left + margin, right - width - margin));
        double y = Math.Clamp(centre.Y - height / 2, top + margin, Math.Max(top + margin, bottom - height - margin));
        switch (EdgeOf(work, full))
        {
            case Edge.Bottom: y = Math.Max(top + margin, Math.Min(bottom, _anchor.Top) - gap - height); break;
            case Edge.Top: y = Math.Max(top, _anchor.Bottom) + gap; break;
            case Edge.Left: x = Math.Max(left, _anchor.Right) + gap; break;
            case Edge.Right: x = Math.Min(right, _anchor.Left) - gap - width; break;
        }
        var rect = new RectInt32(
            (int)Math.Round(x - room), (int)Math.Round(y - room),
            (int)Math.Ceiling(width + 2 * room), (int)Math.Ceiling(height + 2 * room));
        if (rect.Equals(_placed)) return;
        _placed = rect;
        _window.AppWindow.MoveAndResize(rect);
    }

    /// Where the taskbar is: the side of the work area the icon is beyond — or, with the
    /// taskbar hidden (or the icon in the overflow), the screen edge nearest to the icon.
    private Edge EdgeOf(RectInt32 work, RectInt32 full)
    {
        double left = work.X, top = work.Y, right = work.X + work.Width, bottom = work.Y + work.Height;
        if (bottom < full.Y + full.Height && _anchor.Top >= bottom - 1) return Edge.Bottom;
        if (top > full.Y && _anchor.Bottom <= top + 1) return Edge.Top;
        if (left > full.X && _anchor.Right <= left + 1) return Edge.Left;
        if (right < full.X + full.Width && _anchor.Left >= right - 1) return Edge.Right;
        double cx = _anchor.X + _anchor.Width / 2, cy = _anchor.Y + _anchor.Height / 2;
        var distances = new (Edge Edge, double Distance)[]
        {
            (Edge.Bottom, full.Y + full.Height - cy), (Edge.Top, cy - full.Y),
            (Edge.Left, cx - full.X), (Edge.Right, full.X + full.Width - cx),
        };
        return distances.MinBy(d => d.Distance).Edge;
    }
}
