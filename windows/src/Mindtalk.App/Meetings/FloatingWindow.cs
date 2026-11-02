using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.App.Interop;
using Windows.Foundation;
using Windows.Graphics;

namespace Mindtalk.App.Meetings;

/// <summary>
/// A small card floating over every app that never takes the keyboard: borderless,
/// see-through around the card, topmost, not in Alt+Tab, and a click on it leaves the app
/// you're in active (WS_EX_NOACTIVATE and MA_NOACTIVATE, as the dictation pill).
/// </summary>
internal abstract class FloatingWindow
{
    /// Transparent room around the card for its shadow, in points.
    protected const double Room = 24;
    private const double EdgeMargin = 14;

    protected enum Corner { TopRight, BottomRight }

    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private Window? _window;
    private nint _hwnd;
    private readonly List<(nint Window, nint Previous, Win32.WndProc Proc)> _subclassed = [];
    private RectInt32 _placed;

    protected FloatingWindow(App app, double width)
    {
        App = app;
        Width = width;
        app.ThemeChanged += () => { if (_window is not null) Build(); };
    }

    protected App App { get; }
    protected double Width { get; }
    protected abstract Corner Where { get; }
    public bool IsShown { get; private set; }
    public nint Handle => _hwnd;

    /// The card's content, built again when the appearance changes.
    protected abstract UIElement Content();

    private Border? _card;

    protected void Build()
    {
        _root.RequestedTheme = ElementTheme.Dark;
        _root.Children.Clear();
        _card = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Child = Content(),
            Width = Width,
            RequestedTheme = UI.Theme,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Room),
        };
        UI.Bind(_card, Border.BackgroundProperty, "PaperBrush");
        UI.Bind(_card, Border.BorderBrushProperty, "FieldStrokeBrush");
        _card.SizeChanged += (_, _) => { if (IsShown) Place(); };
        _root.Children.Add(SoftShadow.Around(_card, 16, 18, 6, UI.Dark ? 0.6 : 0.2));
    }

    /// The card's content again, for what's changed.
    protected void Refill()
    {
        if (_card is null) return;
        _card.Child = Content();
        if (IsShown) Place();
    }

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
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (nint)((long)style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST));
        Chrome.SeeThrough(_hwnd);
        _window.AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); };
        // A click in the shadow around the card goes nowhere.
        Build();
    }

    /// Shows (or updates) the card without taking focus from the app in front.
    protected void ShowCard()
    {
        Prepare();
        Place();
        if (!IsShown)
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            IsShown = true;
            NeverActivate();
        }
    }

    public virtual void Hide()
    {
        if (!IsShown) return;
        IsShown = false;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    }

    /// Sized to the card, in a corner of the screen's work area.
    protected void Place()
    {
        if (_window is null || _card is null) return;
        var display = DisplayArea.Primary;
        var work = display.WorkArea;
        var scale = Win32.GetDpiForMonitor(display) / 96.0;
        _card.Measure(new Size(Width, double.PositiveInfinity));
        var width = (Width + 2 * Room) * scale;
        var height = (Math.Ceiling(_card.DesiredSize.Height) + 2 * Room) * scale;
        var margin = (EdgeMargin - Room) * scale;
        var x = work.X + work.Width - width - margin;
        var y = Where == Corner.TopRight ? work.Y + margin : work.Y + work.Height - height - margin;
        var rect = new RectInt32((int)Math.Round(x), (int)Math.Round(y), (int)Math.Ceiling(width), (int)Math.Ceiling(height));
        if (rect.Equals(_placed)) return;
        _placed = rect;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, Win32.SWP_NOACTIVATE);
    }

    /// Clicks reach the buttons, but the window never becomes the active one.
    private void NeverActivate()
    {
        void Hook(nint window)
        {
            if (_subclassed.Exists(s => s.Window == window)) return;
            nint previous = 0;
            Win32.WndProc proc = (h, msg, w, l) =>
            {
                if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
                return Win32.CallWindowProcW(previous, h, msg, w, l);
            };
            previous = Win32.SetWindowLongPtr(window, Win32.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(proc));
            _subclassed.Add((window, previous, proc));
        }
        Hook(_hwnd);
        Win32.EnumChildWindows(_hwnd, (child, _) => { Hook(child); return true; }, 0);
    }
}
