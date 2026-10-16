using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Speech;
using Windows.Foundation;
using Windows.UI;

namespace Mindtalk.App.Onboarding;

// The introduction's own pieces (the shared ones at the bottom of the Mac's OnboardingView.swift):
// composition springs, movable shadows, raised surfaces, the glow button, progress segments,
// the download status, the keycap, the typewriter, the living logo, confetti.

/// <summary>SwiftUI's animations on composition visuals.</summary>
internal static class Anim
{
    private static readonly Vector2 EaseOutA = new(0, 0), EaseOutB = new(0.58f, 1);

    public static Visual Of(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    /// Lets an element be moved by "Translation", on top of where layout puts it — from
    /// <paramref name="start"/>, or from wherever it is now.
    public static Visual Movable(UIElement element, Vector3? start = null)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Of(element);
        if (start is { } at) visual.Properties.InsertVector3("Translation", at);
        else if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        return visual;
    }

    /// Scales about the element's centre, wherever layout puts it.
    public static Visual Scalable(UIElement element)
    {
        var visual = Of(element);
        var centre = visual.Compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");
        visual.StartAnimation("CenterPoint", centre);
        return visual;
    }

    // spring(duration:bounce:) — stepped per frame by Animated, which has SwiftUI's own
    // mapping. (Composition's natural-motion springs settle several times slower than the
    // Mac's for the same period and damping.) A new target keeps the current velocity, as
    // SwiftUI retargets a running spring. Translation, Scale and Opacity of a visual.
    public static void Spring(Visual target, string property, Vector3 to, double duration, double bounce, double delay = 0, Action? done = null) =>
        Drive(target, property, [to.X, to.Y, to.Z], duration, bounce, delay, done);

    public static void Spring(Visual target, string property, float to, double duration, double bounce, double delay = 0, Action? done = null) =>
        Drive(target, property, [to], duration, bounce, delay, done);

    private sealed class Driver(Animated[] parts)
    {
        public readonly Animated[] Parts = parts;
        public float[] Targets = [];
        public double Duration, Bounce;
        public long StartAt;
        public bool Started;
        public Action? Done;
    }

    private static readonly Dictionary<(Visual, string), Driver> Drivers = [];
    private static long _lastFrame;
    private static bool _ticking;

    private static void Drive(Visual target, string property, float[] to, double duration, double bounce, double delay, Action? done)
    {
        var key = (target, property);
        if (!Drivers.TryGetValue(key, out var driver))
        {
            driver = new Driver(Read(target, property).Select(v => new Animated(v)).ToArray());
            Drivers[key] = driver;
        }
        else
        {
            // Set by hand since the last frame (a bounce starting small): start from there.
            var current = Read(target, property);
            for (var i = 0; i < current.Length; i++)
                if (Math.Abs(current[i] - driver.Parts[i].Value) > 1e-3) driver.Parts[i].Set(current[i]);
        }
        target.StopAnimation(property);
        driver.Targets = to;
        driver.Duration = duration;
        driver.Bounce = bounce;
        driver.StartAt = Environment.TickCount64 + (long)(delay * 1000);
        driver.Started = false;
        driver.Done = done;
        if (delay <= 0) Begin(driver);
        if (_ticking) return;
        _ticking = true;
        _lastFrame = Environment.TickCount64;
        CompositionTarget.Rendering += Tick;
    }

    private static void Begin(Driver driver)
    {
        driver.Started = true;
        for (var i = 0; i < driver.Parts.Length; i++) driver.Parts[i].Spring(driver.Targets[i], driver.Duration, driver.Bounce);
    }

    private static void Tick(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _lastFrame) / 1000.0);
        _lastFrame = now;
        List<(Visual, string)>? finished = null;
        foreach (var ((visual, property), driver) in Drivers)
        {
            if (!driver.Started)
            {
                if (now < driver.StartAt) continue;
                Begin(driver);
            }
            var moving = false;
            foreach (var part in driver.Parts) moving |= part.Step(dt);
            Write(visual, property, driver.Parts.Select(p => (float)p.Value).ToArray());
            if (!moving) (finished ??= []).Add((visual, property));
        }
        if (finished is not null)
        {
            foreach (var key in finished)
            {
                var done = Drivers[key].Done;
                Drivers.Remove(key);
                done?.Invoke();
            }
        }
        if (Drivers.Count > 0) return;
        _ticking = false;
        CompositionTarget.Rendering -= Tick;
    }

    private static float[] Read(Visual visual, string property) => property switch
    {
        "Opacity" => [visual.Opacity],
        "Scale" => [visual.Scale.X, visual.Scale.Y, visual.Scale.Z],
        _ => visual.Properties.TryGetVector3(property, out var v) == CompositionGetValueStatus.Succeeded ? [v.X, v.Y, v.Z] : [0, 0, 0],
    };

    private static void Write(Visual visual, string property, float[] values)
    {
        switch (property)
        {
            case "Opacity": visual.Opacity = Math.Clamp(values[0], 0, 1); break;
            case "Scale": visual.Scale = new Vector3(values[0], values[1], values[2]); break;
            default: visual.Properties.InsertVector3(property, new Vector3(values[0], values[1], values[2])); break;
        }
    }

    /// easeOut(duration:) — cubic-bezier(0, 0, 0.58, 1).
    public static void EaseOut(CompositionObject target, string property, float to, double duration)
    {
        var animation = target.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, to, target.Compositor.CreateCubicBezierEasingFunction(EaseOutA, EaseOutB));
        animation.Duration = TimeSpan.FromSeconds(Math.Max(0.001, duration));
        target.StartAnimation(property, animation);
    }

    public static void EaseOut(CompositionObject target, string property, Vector3 to, double duration)
    {
        var animation = target.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, to, target.Compositor.CreateCubicBezierEasingFunction(EaseOutA, EaseOutB));
        animation.Duration = TimeSpan.FromSeconds(Math.Max(0.001, duration));
        target.StartAnimation(property, animation);
    }

    /// Runs <paramref name="start"/> once the element is in the tree (its visual has a size then).
    public static void WhenLoaded(FrameworkElement element, Action start)
    {
        if (element.IsLoaded) { start(); return; }
        void Once(object sender, RoutedEventArgs e) { element.Loaded -= Once; start(); }
        element.Loaded += Once;
    }
}

/// <summary>Colours of the design for code that needs a Color rather than a brush.</summary>
internal static class Paint
{
    public static Color Of(string key) => UI.Color(key);

    public static Color Gray(double white)
    {
        var v = (byte)Math.Round(white * 255);
        return Color.FromArgb(255, v, v, v);
    }

    /// The accent (ink) with a soft sheen — selection rings, progress, the typing cursor.
    public static LinearGradientBrush Accent() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = Of("AccentSoftBrush"), Offset = 0 },
            new GradientStop { Color = Of("AccentBrush"), Offset = 1 },
        },
    };

    public static TextBlock Centered(string text, double size, Windows.UI.Text.FontWeight? weight = null, string brush = "PrimaryBrush")
    {
        var block = UI.Text(text, size, weight, brush, wrap: true);
        block.TextAlignment = TextAlignment.Center;
        block.HorizontalAlignment = HorizontalAlignment.Center;
        return block;
    }
}

/// <summary>A button that is only its content: no hover or pressed look of its own.</summary>
internal static class Bare
{
    private static Style? _style;

    public static Style Style => _style ??= (Style)XamlReader.Load(
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Setter Property='Background' Value='Transparent'/>" +
        "<Setter Property='Padding' Value='0'/>" +
        "<Setter Property='UseSystemFocusVisuals' Value='True'/>" +
        "<Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
        "<Setter Property='VerticalContentAlignment' Value='Stretch'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<ContentPresenter Background='{TemplateBinding Background}' Content='{TemplateBinding Content}' Padding='{TemplateBinding Padding}' " +
        "HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}'/>" +
        "</ControlTemplate></Setter.Value></Setter></Style>");

    public static Button Button(UIElement content, Action action)
    {
        var button = new Button { Style = Style, Content = content };
        button.Click += (_, _) => action();
        return button;
    }
}

/// <summary>
/// SwiftUI's .shadow(color:radius:y:) under a rounded surface, whose colour, blur and offset
/// can change (and animate) — the glow under the primary button, the keycap's, a card's.
/// </summary>
internal sealed class Shade
{
    private const float Room = 72;
    private readonly DropShadow _shadow;
    private readonly CompositionRoundedRectangleGeometry _geometry;

    /// Draws the shadow of a rounded rectangle the size of <paramref name="host"/>.
    public Shade(FrameworkElement host, double cornerRadius)
    {
        var compositor = Anim.Of(host).Compositor;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2((float)cornerRadius);
        var shape = compositor.CreateSpriteShape(_geometry);
        shape.FillBrush = compositor.CreateColorBrush(Colors.Black);
        var shapes = compositor.CreateShapeVisual();
        shapes.Shapes.Add(shape);
        shapes.Offset = new Vector3(Room, Room, 0);
        var holder = compositor.CreateContainerVisual();
        holder.Children.InsertAtTop(shapes);

        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = holder;
        var mask = compositor.CreateSurfaceBrush(surface);
        mask.Stretch = CompositionStretch.None;
        mask.HorizontalAlignmentRatio = 0;
        mask.VerticalAlignmentRatio = 0;

        _shadow = compositor.CreateDropShadow();
        _shadow.Mask = mask;
        _shadow.Opacity = 0;
        var sprite = compositor.CreateSpriteVisual();
        sprite.Offset = new Vector3(-Room, -Room, 0);
        sprite.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);

        void Resize()
        {
            var size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
            _geometry.Size = size;
            shapes.Size = size;
            var outer = size + new Vector2(2 * Room);
            sprite.Size = outer;
            surface.SourceSize = outer;
        }
        host.SizeChanged += (_, _) => Resize();
        Resize();
    }

    /// Wraps <paramref name="surface"/> in a grid that draws its shadow underneath.
    public static Grid Around(FrameworkElement surface, double cornerRadius, out Shade shade)
    {
        var host = new Grid { IsHitTestVisible = false };
        var grid = new Grid { HorizontalAlignment = surface.HorizontalAlignment, VerticalAlignment = surface.VerticalAlignment };
        grid.Children.Add(host);
        grid.Children.Add(surface);
        shade = new Shade(host, cornerRadius);
        return grid;
    }

    public double CornerRadius { set => _geometry.CornerRadius = new Vector2((float)value); }

    /// .shadow(color: color.opacity(opacity), radius: radius, y: y), eased over <paramref name="duration"/> (0: at once).
    public void Set(Color color, double opacity, double radius, double y, double duration = 0)
    {
        _shadow.Color = Color.FromArgb(255, color.R, color.G, color.B);
        if (duration <= 0)
        {
            _shadow.Opacity = (float)opacity;
            _shadow.BlurRadius = (float)(radius * 2);
            _shadow.Offset = new Vector3(0, (float)y, 0);
            return;
        }
        Anim.EaseOut(_shadow, "Opacity", (float)opacity, duration);
        Anim.EaseOut(_shadow, "BlurRadius", (float)(radius * 2), duration);
        Anim.EaseOut(_shadow, "Offset", new Vector3(0, (float)y, 0), duration);
    }
}

/// <summary>
/// A raised surface (the Mac's .raised): paper-white with a clear edge and a soft shadow,
/// so it stands off the page in light mode as well as dark. Chosen: an ink edge, more lift.
/// </summary>
internal sealed partial class Raised : Grid
{
    private readonly Border _edge, _hover;
    private readonly Shade _shade;
    private bool _selected, _hovering;

    public Raised(UIElement content, double cornerRadius = 20, Thickness? padding = null)
    {
        var host = new Grid { IsHitTestVisible = false };
        _shade = new Shade(host, cornerRadius);
        var face = new Border { CornerRadius = new CornerRadius(cornerRadius), Padding = padding ?? new Thickness(22), Child = content };
        UI.Bind(face, Border.BackgroundProperty, "FieldBrush");
        // The edges are drawn over the content, as SwiftUI's overlay: they never move it.
        _edge = new Border { CornerRadius = new CornerRadius(cornerRadius), IsHitTestVisible = false };
        // Hover without choosing: a hint of the ink edge.
        _hover = new Border { CornerRadius = new CornerRadius(cornerRadius), BorderThickness = new Thickness(1), IsHitTestVisible = false, Opacity = 0 };
        UI.Bind(_hover, Border.BorderBrushProperty, "InkBrush");
        Children.Add(host);
        Children.Add(face);
        Children.Add(_edge);
        Children.Add(_hover);
        Apply(0);
    }

    public bool Selected
    {
        get => _selected;
        set { if (value == _selected) return; _selected = value; Apply(0.35); }
    }

    public bool Hovering
    {
        set { _hovering = value; Apply(0.3); }
    }

    private void Apply(double duration)
    {
        _edge.BorderThickness = new Thickness(_selected ? 2 : 1);
        UI.Bind(_edge, Border.BorderBrushProperty, _selected ? "InkBrush" : "FieldStrokeBrush");
        _hover.Opacity = _hovering && !_selected ? 0.25 : 0;
        _shade.Set(Colors.Black, _selected ? 0.14 : 0.06, _selected ? 18 : 8, _selected ? 8 : 3, duration);
    }
}

/// <summary>Serif title with a line under it, centred.</summary>
internal sealed partial class StepHeader : StackPanel
{
    private readonly TextBlock _title, _subtitle;

    public StepHeader(string title, string subtitle)
    {
        Spacing = 10;
        MaxWidth = 560;
        HorizontalAlignment = HorizontalAlignment.Center;
        _title = new TextBlock
        {
            Text = title,
            FontFamily = UI.Serif,
            FontSize = 38,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        UI.Bind(_title, TextBlock.ForegroundProperty, "PrimaryBrush");
        _subtitle = Paint.Centered(subtitle, 15.5, brush: "MutedBrush");
        Children.Add(_title);
        Children.Add(_subtitle);
    }

    /// New words cross-fade in (contentTransition(.opacity)).
    public void Set(string title, string subtitle)
    {
        if (title == _title.Text && subtitle == _subtitle.Text) return;
        foreach (var (block, text) in new[] { (_title, title), (_subtitle, subtitle) })
        {
            if (block.Text == text) continue;
            block.Text = text;
            var visual = Anim.Of(block);
            visual.Opacity = 0;
            Anim.EaseOut(visual, "Opacity", 1, 0.3);
        }
    }
}

/// <summary>The ink primary button, with an arrow that leans forward on hover.</summary>
internal sealed partial class GlowButton : Button
{
    private readonly TextBlock _title;
    private readonly Visual _arrow, _body;
    private readonly Shade _shade;
    private bool _hovering, _pressed;

    public GlowButton(Action action)
    {
        Style = Bare.Style;
        VerticalAlignment = VerticalAlignment.Center;
        _title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        UI.Bind(_title, TextBlock.ForegroundProperty, "OnInkBrush");
        var arrow = UI.Icon(Icons.ArrowRight, 12, "OnInkBrush");
        arrow.FontWeight = FontWeights.Bold;
        var face = new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Padding = new Thickness(22, 0, 22, 0),
            Child = UI.HStack(8, _title, arrow),
        };
        UI.Bind(face, Border.BackgroundProperty, "InkBrush");
        var body = Shade.Around(face, 21, out _shade);
        Content = body;
        _arrow = Anim.Movable(arrow);
        _body = Anim.Scalable(body);
        Apply();

        Click += (_, _) => action();
        PointerEntered += (_, _) => { _hovering = true; Apply(); };
        PointerExited += (_, _) => { _hovering = false; _pressed = false; Apply(); };
        // The button handles presses itself; listen in anyway for the press-in.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { _pressed = true; Apply(); }), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _pressed = false; Apply(); }), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _pressed = false; Apply(); }), true);
    }

    public string Title
    {
        set { _title.Text = value; AutomationProperties.SetName(this, value); }
    }

    private void Apply()
    {
        // spring(duration 0.3) on hover; the press sinks a little (spring 0.2).
        Anim.Spring(_arrow, "Translation", new Vector3(_hovering ? 3 : 0, 0, 0), 0.3, 0);
        var scale = (_hovering ? 1.02f : 1) * (_pressed ? 0.97f : 1);
        Anim.Spring(_body, "Scale", new Vector3(scale, scale, 1), _pressed ? 0.2 : 0.3, 0);
        _shade.Set(Paint.Of("AccentBrush"), _hovering ? 0.45 : 0.2, _hovering ? 18 : 10, 6, 0.3);
    }
}

/// <summary>Thin segments across the top: done, current, still to come.</summary>
internal sealed partial class ProgressSegments : StackPanel
{
    private readonly Rectangle[] _bars;
    private readonly Animated[] _widths, _opacities;
    private readonly int _count;
    private long _lastFrame;
    private bool _rendering;

    public ProgressSegments(int count, int current)
    {
        _count = count;
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Center;
        _bars = new Rectangle[count];
        _widths = new Animated[count];
        _opacities = new Animated[count];
        for (var i = 0; i < count; i++)
        {
            _bars[i] = new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2 };
            UI.Bind(_bars[i], Shape.FillProperty, "InkBrush");
            _widths[i] = new Animated(SegmentWidth(i, current));
            _opacities[i] = new Animated(SegmentOpacity(i, current));
            Children.Add(_bars[i]);
        }
        Apply();
        Describe(current);
        Unloaded += (_, _) => Stop();
    }

    // Done: half ink. Now: full ink, wider. Still to come: faint but visible.
    private static double SegmentWidth(int i, int current) => i == current ? 34 : 18;
    private static double SegmentOpacity(int i, int current) => i == current ? 1 : i < current ? 0.45 : 0.16;

    public int Current
    {
        set
        {
            for (var i = 0; i < _count; i++)
            {
                // spring(duration 0.5, bounce 0.2)
                _widths[i].Spring(SegmentWidth(i, value), 0.5, 0.2);
                _opacities[i].Spring(SegmentOpacity(i, value), 0.5, 0.2);
            }
            Describe(value);
            if (_rendering) return;
            _rendering = true;
            _lastFrame = Environment.TickCount64;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void Describe(int current) =>
        AutomationProperties.SetName(this, Loc.T($"Steg {current + 1} av {_count}", $"Step {current + 1} of {_count}"));

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _lastFrame) / 1000.0);
        _lastFrame = now;
        var moving = false;
        for (var i = 0; i < _count; i++) moving |= _widths[i].Step(dt) | _opacities[i].Step(dt);
        Apply();
        if (!moving) Stop();
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void Apply()
    {
        for (var i = 0; i < _count; i++)
        {
            _bars[i].Width = Math.Max(4, _widths[i].Value);
            _bars[i].Opacity = Math.Clamp(_opacities[i].Value, 0, 1);
        }
    }
}

/// <summary>A small ring filling up clockwise from the top, in the accent.</summary>
internal sealed partial class ProgressArc : Grid
{
    private const double Thickness = 2.5;
    private readonly Microsoft.UI.Xaml.Shapes.Path _arc = new();
    private readonly double _size;

    public ProgressArc(double size)
    {
        _size = size;
        Width = Height = size;
        var track = new Ellipse { StrokeThickness = Thickness };
        UI.Bind(track, Shape.StrokeProperty, "DividerBrush");
        _arc.StrokeThickness = Thickness;
        _arc.StrokeStartLineCap = _arc.StrokeEndLineCap = PenLineCap.Round;
        _arc.Stroke = Paint.Accent();
        Children.Add(track);
        Children.Add(_arc);
    }

    public double Progress
    {
        set
        {
            var p = Math.Clamp(value, 0.02, 0.9999);
            var c = _size / 2;
            var r = c - Thickness / 2;
            var end = -Math.PI / 2 + p * 2 * Math.PI;
            var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(c + r * Math.Cos(end), c + r * Math.Sin(end)),
                Size = new Size(r, r),
                IsLargeArc = p > 0.5,
                SweepDirection = SweepDirection.Clockwise,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            _arc.Data = geometry;
        }
    }
}

/// <summary>
/// The downloads, next to the Back button: a ring, the model, MB and time left — or what
/// went wrong, with a way to try again.
/// </summary>
internal sealed partial class DownloadStatus : StackPanel
{
    private readonly App _app;
    private readonly Dictionary<string, (ProgressArc Ring, TextBlock Detail)> _rows = [];
    private string _shown = "";

    public DownloadStatus(App app)
    {
        _app = app;
        Orientation = Orientation.Horizontal;
        Spacing = 18;
        Margin = new Thickness(10, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    public void Update()
    {
        var speech = _app.Speech;
        // What's shown per model: its download, its error, or nothing.
        var kinds = SpeechModel.All.Select(m => speech.DownloadProgress(m) is not null ? "d" : speech.DownloadError(m) is not null ? "e" : "-");
        var shown = string.Join("", kinds);
        if (shown != _shown)
        {
            _shown = shown;
            Children.Clear();
            _rows.Clear();
            foreach (var m in SpeechModel.All)
            {
                FrameworkElement? row = null;
                if (speech.DownloadProgress(m) is not null)
                {
                    var ring = new ProgressArc(20);
                    var detail = UI.Text("", 11.5, brush: "MutedBrush");
                    row = UI.HStack(9, ring, new StackPanel { Spacing = 1, Children = { UI.Text(m.Title, 12.5, FontWeights.SemiBold), detail } });
                    _rows[m.Id] = (ring, detail);
                }
                else if (speech.DownloadError(m) is not null)
                {
                    var retry = UI.PlainButton(UI.Text(Loc.T("Försök igen", "Try Again"), 11.5, FontWeights.SemiBold, "AccentBrush"), () => _app.DownloadModel(m));
                    retry.HorizontalAlignment = HorizontalAlignment.Left;
                    row = UI.HStack(8, UI.Icon(Icons.WarningFill, 14, "OrangeBrush"),
                        new StackPanel { Spacing = 1, Children = { UI.Text(Loc.T($"{m.Title} laddades inte ned", $"{m.Title} didn’t download"), 12.5, FontWeights.SemiBold), retry } });
                }
                if (row is null) continue;
                // easeOut(0.25) as rows come and go.
                var visual = Anim.Of(row);
                visual.Opacity = 0;
                Anim.WhenLoaded(row, () => Anim.EaseOut(visual, "Opacity", 1, 0.25));
                Children.Add(row);
            }
        }
        foreach (var m in SpeechModel.All)
        {
            if (!_rows.TryGetValue(m.Id, out var row) || speech.DownloadProgress(m) is not { } p) continue;
            row.Ring.Progress = p;
            row.Detail.Text = m.ProgressText(p, speech.DownloadStarted(m), DateTime.Now);
        }
    }
}

/// <summary>A big physical-looking key that sinks while you hold it.</summary>
internal sealed partial class Keycap : Grid
{
    private readonly Border _edge;
    private readonly TextBlock _text;
    private readonly Visual _top;
    private readonly Shade _shade;
    private bool? _pressed;

    public Keycap()
    {
        Height = 96;
        HorizontalAlignment = HorizontalAlignment.Center;
        var host = new Grid { IsHitTestVisible = false };
        _shade = new Shade(host, 20);
        // The key's side, visible below the top face.
        var side = new Border { CornerRadius = new CornerRadius(20), RenderTransform = new TranslateTransform { Y = 7 } };
        UI.Bind(side, Border.BackgroundProperty, "FieldStrokeBrush");
        var fill = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop { Color = Paint.Of("FieldBrush"), Offset = 0 },
                    new GradientStop { Color = Paint.Of("CardBrush"), Offset = 1 },
                },
            },
        };
        _edge = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.5) };
        _text = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
        };
        var top = new Grid();
        top.Children.Add(fill);
        top.Children.Add(_edge);
        top.Children.Add(_text);
        Children.Add(host);
        Children.Add(side);
        Children.Add(top);
        _top = Anim.Movable(top);
    }

    public void Set(string text, bool pressed, bool isCombination)
    {
        _text.Text = text;
        Width = isCombination ? 480 : 190;
        AutomationProperties.SetName(this, Loc.T($"Din genväg: {text}", $"Your shortcut: {text}"));
        if (pressed == _pressed) return;
        var first = _pressed is null;
        _pressed = pressed;
        if (pressed) _edge.BorderBrush = Paint.Accent();
        else UI.Bind(_edge, Border.BorderBrushProperty, "FieldStrokeBrush");
        UI.Bind(_text, TextBlock.ForegroundProperty, pressed ? "AccentBrush" : "PrimaryBrush");
        // spring(duration 0.2, bounce 0.35)
        Anim.Spring(_top, "Translation", new Vector3(0, pressed ? 6 : 0, 0), 0.2, 0.35);
        if (pressed) _shade.Set(Paint.Of("AccentBrush"), 0.35, 22, 4, first ? 0 : 0.2);
        else _shade.Set(Colors.Black, 0.12, 14, 10, first ? 0 : 0.2);
    }
}

/// <summary>Types its text one character at a time, with a blinking cursor at the end.</summary>
internal sealed partial class Typewriter : Grid
{
    private readonly string _text;
    private readonly TextBlock _shown;
    private readonly Border _cursor;
    private readonly DispatcherQueueTimerHolder _blink = new();
    private CancellationTokenSource? _typing;
    private int _count;

    public Typewriter(string text, double size, double delay)
    {
        _text = text;
        HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(this, text);
        TextBlock Line() => new() { FontFamily = UI.Serif, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        // The full line, invisible, keeps the layout still while typing.
        var full = Line();
        full.Text = text;
        full.Opacity = 0;
        _shown = Line();
        UI.Bind(_shown, TextBlock.ForegroundProperty, "PrimaryBrush");
        _cursor = new Border { Width = 3, Height = 46, CornerRadius = new CornerRadius(1.5), Background = Paint.Accent(), VerticalAlignment = VerticalAlignment.Center };
        Children.Add(full);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left, Children = { _shown, _cursor } });

        Loaded += (_, _) => Start(delay);
        Unloaded += (_, _) => { _typing?.Cancel(); _blink.Stop(); };
    }

    private async void Start(double delay)
    {
        _blink.Start(DispatcherQueue, 0.5, Blink);
        // Calm with reduced motion — and already typed when the step is only redrawn (delay < 0).
        if (Motion.Reduced || delay < 0) { Show(_text.Length); return; }
        _typing = new CancellationTokenSource();
        var token = _typing.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), token);
            for (var i = 0; i < _text.Length; i++)
            {
                Show(i + 1);
                // A small pause after a full stop, like someone speaking.
                await Task.Delay(_text[i] == '.' ? 260 : Random.Shared.Next(45, 86), token);
            }
        }
        catch (TaskCanceledException) { }
    }

    private void Show(int count)
    {
        _count = count;
        _shown.Text = _text[..count];
        Blink();
    }

    private void Blink()
    {
        var on = _count < _text.Length || Environment.TickCount64 / 500 % 2 == 0;
        _cursor.Opacity = on ? 1 : 0;
    }
}

/// <summary>A repeating DispatcherQueue timer that can be started and stopped again.</summary>
internal sealed class DispatcherQueueTimerHolder
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    public void Start(Microsoft.UI.Dispatching.DispatcherQueue queue, double seconds, Action tick)
    {
        Stop();
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>The app icon, alive — the wave rolls and the cursor blinks. It floats gently.</summary>
internal static class LogoMark
{
    public static FrameworkElement Make(double size)
    {
        var tile = new LogoTile(size, animated: !Motion.Reduced);
        var shadowed = Shade.Around(tile, size * 0.225, out var shade);
        shade.Set(Colors.Black, 0.35, 30, 14);
        var floating = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { shadowed } };
        AutomationProperties.SetAccessibilityView(floating, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (Motion.Reduced) return floating;
        // sin(t · 1.2) · 4: one slow breath every 2π / 1.2 seconds.
        var visual = Anim.Movable(floating);
        var compositor = visual.Compositor;
        var bob = compositor.CreateVector3KeyFrameAnimation();
        const int steps = 32;
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            bob.InsertKeyFrame(t, new Vector3(0, (float)(Math.Sin(t * 2 * Math.PI) * 4), 0), compositor.CreateLinearEasingFunction());
        }
        bob.Duration = TimeSpan.FromSeconds(2 * Math.PI / 1.2);
        bob.IterationBehavior = AnimationIterationBehavior.Forever;
        Anim.WhenLoaded(floating, () => visual.StartAnimation("Translation", bob));
        return floating;
    }
}

/// <summary>
/// Mindtalk's mark as the tray shows it (the Mac's menu bar template, MarkGlyph): the wave
/// and line fitted to 24 × 16, the cursor drawn heavier so it reads at that size.
/// </summary>
internal sealed partial class MarkGlyph : Canvas
{
    public MarkGlyph(string brush = "PrimaryBrush")
    {
        // The image is 24 wide in a 20-wide frame, as on the Mac: it overhangs a little.
        Width = 20;
        Height = 16;
        var glyph = new Canvas { Width = 24, Height = 16, Margin = new Thickness(-2, 0, -2, 0) };
        const double minX = 215, waveEnd = 700, mid = 8;
        var scale = 17.5 / (waveEnd - minX);
        var line = new Polyline
        {
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        UI.Bind(line, Shape.StrokeProperty, brush);
        var points = new PointCollection();
        // Design space is y up; the canvas is y down.
        foreach (var p in Mark.Wave()) points.Add(new Point(1 + (p.X - minX) * scale, mid - (p.Y - 512) * scale * 1.25));
        line.Points = points;
        glyph.Children.Add(line);
        double cx = 1 + (760 - minX) * scale + 0.6, h = 13, stem = 1.7, serif = 5;
        foreach (var r in new[] { new Rect(cx - stem / 2, mid - h / 2, stem, h), new Rect(cx - serif / 2, mid - h / 2, serif, stem), new Rect(cx - serif / 2, mid + h / 2 - stem, serif, stem) })
        {
            var bar = new Rectangle { Width = r.Width, Height = r.Height, RadiusX = stem / 2, RadiusY = stem / 2 };
            UI.Bind(bar, Shape.FillProperty, brush);
            SetLeft(bar, r.X);
            SetTop(bar, r.Y);
            glyph.Children.Add(bar);
        }
        Children.Add(glyph);
    }
}

/// <summary>A short burst of paper in black, white and greys, falling with a little spin.</summary>
internal sealed partial class Confetti : Canvas
{
    private sealed record Piece(double X, double Vx, double Vy, double Spin, double Size, double Delay, Shape Shape, CompositeTransform Transform);

    private const double Duration = 3.4;
    private Piece[] _pieces = [];
    private long _start;
    private bool _rendering;

    public Confetti()
    {
        IsHitTestVisible = false;
        // Paper outside the step's area isn't drawn, as on the Mac's canvas.
        SizeChanged += (_, e) => Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        Unloaded += (_, _) => Stop();
    }

    public void Burst()
    {
        if (Motion.Reduced) return;
        Stop();
        Children.Clear();
        Color[] colors = [Paint.Of("InkBrush"), Paint.Gray(0.55), Paint.Gray(0.75), Paint.Gray(0.35), Paint.Gray(0.9)];
        var random = Random.Shared;
        double Between(double a, double b) => a + random.NextDouble() * (b - a);
        // Two cannons in the bottom corners, aimed up and inwards.
        _pieces = Enumerable.Range(0, 160).Select(i =>
        {
            var left = i % 2 == 0;
            var size = Between(6, 12);
            var round = random.Next(2) == 0 && random.Next(2) == 0;
            Shape shape = round ? new Ellipse { Width = size, Height = size } : new Rectangle { Width = size, Height = size / 2, RadiusX = 1.5, RadiusY = 1.5 };
            shape.Fill = new SolidColorBrush(colors[random.Next(colors.Length)]);
            // The piece's own origin sits a quarter of its size down from its top edge.
            var transform = new CompositeTransform { CenterX = size / 2, CenterY = size / 4 };
            shape.RenderTransform = transform;
            shape.Visibility = Visibility.Collapsed;
            SetLeft(shape, -size / 2);
            SetTop(shape, -size / 4);
            Children.Add(shape);
            return new Piece(left ? 0.04 : 0.96, (left ? 1 : -1) * Between(180, 620), Between(-1060, -620),
                             Between(-10, 10), size, Between(0, 0.25), shape, transform);
        }).ToArray();
        _start = Environment.TickCount64;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = (Environment.TickCount64 - _start) / 1000.0;
        if (now >= Duration) { Stop(); Children.Clear(); return; }
        double width = ActualWidth, height = ActualHeight;
        var opacity = Math.Clamp(Duration - now, 0, 1);
        foreach (var p in _pieces)
        {
            var t = now - p.Delay;
            // A little air resistance on the sideways drift, gravity downwards.
            var x = p.X * width + p.Vx * (1 - Math.Exp(-1.6 * t)) / 1.6;
            var y = height + 10 + p.Vy * t + 620 * t * t;
            if (t <= 0 || y >= height + 40) { p.Shape.Visibility = Visibility.Collapsed; continue; }
            p.Shape.Visibility = Visibility.Visible;
            p.Shape.Opacity = opacity;
            p.Transform.TranslateX = x;
            p.Transform.TranslateY = y;
            p.Transform.Rotation = p.Spin * t * 180 / Math.PI;
        }
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }
}
