using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.App.Interop;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Mindtalk.App;

/// <summary>
/// The dictation pill: a small black capsule at the bottom of the screen while you dictate —
/// just the waveform. Locked (hands-free), it grows a ✕ to cancel and a ✓ — our text cursor —
/// to type it in, both clickable. When you finish, the wave settles into a line and ends in a
/// blinking cursor: speech becoming text, as in the icon. Never takes focus from the app
/// you're typing in. (Port of the Mac app's HUD.)
/// </summary>
public sealed partial class HudWindow : Window
{
    /// The window is a fixed stage; the pill animates inside it, centred at the bottom.
    /// (Wider than the Mac's 260 so a two-line message never touches its edges.)
    private const double StageWidth = 320, StageHeight = 64;
    /// Space under the pill inside the stage, for its shadow.
    private const double StagePadding = 10;
    private const float ShadowRoom = 20;
    private static readonly Color White = Colors.White;

    private readonly Func<DictationState> _state;
    private Func<int, float[]> _levels;
    private readonly Action _cancel, _finish;
    private readonly nint _hwnd;

    private readonly RoundButton _cancelButton, _finishButton;
    private readonly Waveform _wave = new() { Width = 52, Height = 16, Gap = 2.5 };
    private readonly Grid _badge = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly SettlingLine _settling = new();
    private readonly FontIcon _warning = new()
    {
        Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 149, 0)),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _message = new()
    {
        FontSize = 12, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(White),
        TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center,
        // Two lines of 12 pt fit the 32-pt capsule, as on the Mac.
        LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    private readonly FontIcon _clipboard = new()
    {
        Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _copied = new()
    {
        FontSize = 12, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(White),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // The capsule's width and the elements that come and go, animated like the Mac's.
    private readonly Animated _width = new(60);
    private readonly Animated _buttons = new(0), _language = new(0), _content = new(1);
    private SpriteVisual? _shadowLayer;
    private CompositionVisualSurface? _shadowSurface;
    private CompositionRoundedRectangleGeometry? _shadowShape;
    private ShapeVisual? _shadowVisual;
    private DictationState? _shown;
    private bool _visible, _rendering, _clickable;
    private long _lastFrame;

    public HudWindow(Func<DictationState> state, Func<int, float[]> levels, Action cancel, Action finish)
    {
        InitializeComponent();
        _state = state;
        _levels = levels;
        _cancel = cancel;
        _finish = finish;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        _cancelButton = new RoundButton(filled: false, Loc.T("Avbryt (esc)", "Cancel (esc)"), XGlyph(), () => _cancel());
        _finishButton = new RoundButton(filled: true, Loc.T("Skriv in", "Type It In"),
            new CursorGlyph { Width = 8, Height = 13, Fill = new SolidColorBrush(Colors.Black) }, () => _finish());
        _wave.Color = White;
        _copied.Text = Loc.T("Kopierad – klistra in med Ctrl+V", "Copied – paste with Ctrl+V");
        foreach (var child in new UIElement[] { _cancelButton, _wave, _badge, _finishButton, _settling, _warning, _message, _clipboard, _copied })
            Row.Children.Add(child);
        // Text is drawn on the dark capsule whatever the app's appearance.
        Root.RequestedTheme = ElementTheme.Dark;
        Root.AllowFocusOnInteraction = false;

        SystemBackdrop = new TransparentBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Mindtalk";

        // Buttons answer the first click, but the window never becomes the active one —
        // the app you're typing in keeps focus and receives the text.
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE,
            (nint)((long)style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST));
        Chrome.SeeThrough(_hwnd);
        SetClickable(false);

        BuildShadow();
    }

    public nint Handle => _hwnd;

    /// The window that was active while the pill showed: a click on the pill hands the focus back to it.
    public nint ActiveBefore { get; private set; }

    /// Shows or hides the pill for <paramref name="state"/>.
    public void Update(DictationState state)
    {
        if (!state.Visible)
        {
            Hide();
            return;
        }
        var previous = _shown;
        _shown = state;
        var recording = state.Phase == Phase.Recording;
        var locked = recording && state.HandsFree;

        _cancelButton.Visibility = _finishButton.Visibility = Show(locked);
        _wave.Visibility = Show(recording);
        _badge.Visibility = Show(recording && state.ShowsLanguage);
        _settling.Visibility = Show(state.Phase == Phase.Transcribing);
        _warning.Visibility = _message.Visibility = Show(state.Phase == Phase.Failed);
        _clipboard.Visibility = _copied.Visibility = Show(state.Phase == Phase.Copied);
        _message.Text = state.Message ?? "";
        if (state.ShowsLanguage) SetBadge(state.Engine);
        if (state.Phase == Phase.Transcribing && previous?.Phase != Phase.Transcribing) _settling.Restart();

        // Clickable only while locked, when it has buttons; otherwise it never gets in the way.
        SetClickable(locked);

        // What changed decides how it moves: a new phase eases out, the buttons and the
        // language badge spring in — as the Mac's animation modifiers do.
        var padding = locked ? 5 : 14;
        Row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(Row.DesiredSize.Width) + 2 * padding;
        if (!_visible)
        {
            _width.Set(width);
            _buttons.Set(locked ? 1 : 0);
            _language.Set(state.ShowsLanguage ? 1 : 0);
            _content.Set(1);
        }
        else
        {
            if (previous?.Phase != state.Phase)
            {
                _width.EaseOut(width, 0.25);
                _content.Set(0);
                _content.EaseOut(1, 0.25);
            }
            else if (previous?.HandsFree != state.HandsFree) _width.Spring(width, 0.32, 0.18);
            else if (previous?.ShowsLanguage != state.ShowsLanguage) _width.Spring(width, 0.32, 0.1);
            else _width.EaseOut(width, 0.25);
            if (previous?.HandsFree != locked) _buttons.Spring(locked ? 1 : 0, 0.32, 0.18);
            if (previous?.ShowsLanguage != state.ShowsLanguage) _language.Spring(state.ShowsLanguage ? 1 : 0, 0.32, 0.1);
        }
        if (locked && previous is { HandsFree: false }) { _buttons.Set(0); _buttons.Spring(1, 0.32, 0.18); }
        if (state.ShowsLanguage && previous is { ShowsLanguage: false }) { _language.Set(0); _language.Spring(1, 0.32, 0.1); }

        Apply();
        if (!_visible)
        {
            if (Win32.GetForegroundWindow() is var front && front != _hwnd) ActiveBefore = front;
            NeverActivate();
            Place();
            // Shown, on top, never activated: AppWindow.Show activates even when asked not to.
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            _visible = true;
        }
        StartFrames();
    }


#if DEBUG
    /// Screenshots: the pill in a given state, with made-up levels.
    public void Demo(DictationState state, Func<int, float[]> levels)
    {
        _levels = levels;
        Update(state);
    }
#endif

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void Hide()
    {
        _shown = null;
        if (!_visible) return;
        _visible = false;
        StopFrames();
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    }

    /// The language model as a small mark: "SV" for Swedish, a globe for the multilingual one.
    private void SetBadge(string? engine)
    {
        _badge.Children.Clear();
        _badge.Children.Add(engine == "swedish"
            ? new TextBlock
            {
                Text = "SV", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new FontIcon
            {
                Glyph = "", FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)),
            });
    }

    // ---- Frames -------------------------------------------------------------------

    private void StartFrames()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        // The app you're in — where the text goes if you click ✓.
        var front = Win32.GetForegroundWindow();
        if (front != _hwnd && front != 0) ActiveBefore = front;
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _lastFrame) / 1000.0, 0, 0.1);
        _lastFrame = now;
        _width.Step(dt);
        _buttons.Step(dt);
        _language.Step(dt);
        _content.Step(dt);
        if (_shown?.Phase == Phase.Recording) _wave.SetLevels(_levels(14));
        _settling.Tick(dt);
        Apply();
    }

    private void Apply()
    {
        var width = Math.Max(32, _width.Value);
        Pill.Width = width;
        ShadowHost.Width = width;
        if (_shadowShape is not null) _shadowShape.Size = new Vector2((float)width, 32);
        if (_shadowVisual is not null) _shadowVisual.Size = new Vector2((float)width, 32);
        var room = new Vector2((float)width + 2 * ShadowRoom, 32 + 2 * ShadowRoom);
        if (_shadowLayer is not null) _shadowLayer.Size = room;
        if (_shadowSurface is not null) _shadowSurface.SourceSize = room;
        Row.Opacity = _content.Value;
        Scale(_cancelButton, 0.4 + 0.6 * _buttons.Value, _buttons.Value);
        Scale(_finishButton, 0.4 + 0.6 * _buttons.Value, _buttons.Value);
        Scale(_badge, 0.6 + 0.4 * _language.Value, _language.Value);
    }

    private static void Scale(FrameworkElement element, double scale, double opacity)
    {
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.Scale = new Vector3((float)scale, (float)scale, 1);
        element.Opacity = Math.Clamp(opacity, 0, 1);
    }

    // ---- The shadow ----------------------------------------------------------------

    /// black 28 %, radius 6, 2 down — the Mac's shadow, cast by a capsule the same size as the pill.
    private void BuildShadow()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        _shadowShape = compositor.CreateRoundedRectangleGeometry();
        _shadowShape.CornerRadius = new Vector2(16, 16);
        _shadowShape.Size = new Vector2(60, 32);
        var shape = compositor.CreateSpriteShape(_shadowShape);
        shape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 18, 18, 18));
        _shadowVisual = compositor.CreateShapeVisual();
        _shadowVisual.Size = new Vector2(60, 32);
        _shadowVisual.Offset = new Vector3(ShadowRoom, ShadowRoom, 0);
        _shadowVisual.Shapes.Add(shape);
        var holder = compositor.CreateContainerVisual();
        holder.Children.InsertAtTop(_shadowVisual);

        // The capsule, rendered to a surface, is the shadow's mask — with room around it,
        // since a visual clips its shadow to its own size.
        _shadowSurface = compositor.CreateVisualSurface();
        _shadowSurface.SourceVisual = holder;
        _shadowSurface.SourceSize = new Vector2(60 + 2 * ShadowRoom, 32 + 2 * ShadowRoom);
        var mask = compositor.CreateSurfaceBrush(_shadowSurface);
        mask.Stretch = CompositionStretch.None;
        mask.HorizontalAlignmentRatio = 0;
        mask.VerticalAlignmentRatio = 0;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 12;
        shadow.Offset = new Vector3(0, 2, 0);
        shadow.Color = Color.FromArgb(71, 0, 0, 0);
        shadow.Mask = mask;
        _shadowLayer = compositor.CreateSpriteVisual();
        _shadowLayer.Offset = new Vector3(-ShadowRoom, -ShadowRoom, 0);
        _shadowLayer.Size = new Vector2(60 + 2 * ShadowRoom, 32 + 2 * ShadowRoom);
        _shadowLayer.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, _shadowLayer);
    }

    // ---- Window ---------------------------------------------------------------------

    /// Bottom centre of the screen you're on: just above the taskbar, or near the bottom
    /// edge when the taskbar is hidden or on the side.
    private void Place()
    {
        var foreground = Win32Interop.GetWindowIdFromWindow(Win32.GetForegroundWindow());
        var display = DisplayArea.GetFromWindowId(foreground, DisplayAreaFallback.Primary);
        RectInt32 full = display.OuterBounds, work = display.WorkArea;
        var scale = Win32.GetDpiForMonitor(display) / 96.0;
        var taskbarBelow = work.Y + work.Height < full.Y + full.Height - 1;
        var pillBottom = taskbarBelow ? work.Y + work.Height - 10 * scale : full.Y + full.Height - 14 * scale;
        var width = (int)Math.Round(StageWidth * scale);
        var height = (int)Math.Round(StageHeight * scale);
        AppWindow.MoveAndResize(new RectInt32(
            full.X + (full.Width - width) / 2,
            (int)Math.Round(pillBottom + StagePadding * scale) - height,
            width, height));
    }

    private readonly List<(nint Window, nint Previous, Win32.WndProc Proc)> _subclassed = [];

    /// A click on ✕ or ✓ must not make the pill the active window — the text goes to the
    /// app that's active. WS_EX_NOACTIVATE alone isn't enough: WinUI's input window answers
    /// "activate" to a click, so the pill and its child windows are told to say no.
    private void NeverActivate()
    {
        void Hook(nint window)
        {
            if (_subclassed.Exists(s => s.Window == window)) return;
            nint previous = 0;
            Win32.WndProc proc = (h, msg, w, l) =>
            {
                if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
                // Activated anyway (WinUI takes the focus on a click): straight back to the app you were in.
                if (msg == Win32.WM_ACTIVATE && (w & 0xFFFF) != 0 && h == _hwnd && ActiveBefore != 0)
                    DispatcherQueue.TryEnqueue(() => Win32.SetForegroundWindow(ActiveBefore));
                return Win32.CallWindowProcW(previous, h, msg, w, l);
            };
            previous = Win32.SetWindowLongPtr(window, Win32.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(proc));
            _subclassed.Add((window, previous, proc));
        }
        Hook(_hwnd);
        Win32.EnumChildWindows(_hwnd, (child, _) => { Hook(child); return true; }, 0);
    }

    private void SetClickable(bool clickable)
    {
        if (clickable == _clickable && _visible) return;
        _clickable = clickable;
        // Layered + transparent: mouse events go to whatever is underneath.
        var style = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        style = clickable ? style & ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED)
                          : style | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (nint)style);
        if (!clickable) Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
    }

    private static Path XGlyph() => new()
    {
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M0,0 L7,7 M7,0 L0,7"),
        Stroke = new SolidColorBrush(Colors.White),
        StrokeThickness = 1.7,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 7, Height = 7,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A 24-pt round button in the pill: ✕ on smoked glass, ✓ as a white disc.</summary>
    private sealed partial class RoundButton : Grid
    {
        private readonly Ellipse _disc = new();
        private readonly bool _filled;
        private readonly Animated _hover = new(0);
        private long _lastFrame;

        public RoundButton(bool filled, string help, UIElement glyph, Action action)
        {
            _filled = filled;
            Width = Height = 24;
            VerticalAlignment = VerticalAlignment.Center;
            // A click mustn't take keyboard focus — it stays in the app you're typing in.
            AllowFocusOnInteraction = false;
            Children.Add(_disc);
            Children.Add(glyph);
            ToolTipService.SetToolTip(this, help);
            AutomationProperties.SetName(this, help);
            Paint();
            PointerEntered += (_, _) => Hover(true);
            PointerExited += (_, _) => Hover(false);
            PointerCanceled += (_, _) => Hover(false);
            Tapped += (_, _) => action();
        }

        private void Hover(bool on)
        {
            _hover.EaseOut(on ? 1 : 0, 0.12);
            _lastFrame = Environment.TickCount64;
            CompositionTarget.Rendering -= OnFrame;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, object e)
        {
            var now = Environment.TickCount64;
            var moving = _hover.Step((now - _lastFrame) / 1000.0);
            _lastFrame = now;
            Paint();
            if (!moving) CompositionTarget.Rendering -= OnFrame;
        }

        private void Paint()
        {
            var h = _hover.Value;
            var alpha = _filled ? 0.92 + 0.08 * h : 0.18 + 0.10 * h;
            _disc.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255));
            // The hover lift rides on top of the transition's scale (set on the button itself).
            _disc.RenderTransformOrigin = new Point(0.5, 0.5);
            _disc.RenderTransform = new ScaleTransform { ScaleX = 1 + 0.06 * h, ScaleY = 1 + 0.06 * h };
        }
    }

    /// <summary>
    /// While the text is typed: the wave has settled into a line of text that ends in a
    /// blinking cursor — the icon, in motion.
    /// </summary>
    private sealed partial class SettlingLine : Grid
    {
        private readonly Rectangle _line = new()
        {
            Height = 2, Width = 8, RadiusX = 1, RadiusY = 1,
            Fill = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly CursorGlyph _cursor = new() { Width = 7, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        private double _elapsed;

        public SettlingLine()
        {
            Width = 60;
            Height = 16;
            VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(_line);
            row.Children.Add(_cursor);
            Children.Add(row);
            AutomationProperties.SetName(this, Loc.T("Skriver…", "Typing…"));
        }

        public void Restart() => _elapsed = 0;

        public void Tick(double dt)
        {
            if (Visibility != Visibility.Visible) return;
            _elapsed += dt;
            _line.Width = 8 + 36 * Ease.Out(Math.Min(1, _elapsed / 0.35));
            var on = (long)(DateTime.UtcNow.TimeOfDay.TotalSeconds / 0.45) % 2 == 0;
            _cursor.Opacity = on ? 1 : 0.25;
        }
    }
}
