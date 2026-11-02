using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.Core.Stats;
using Windows.Foundation;
using Windows.UI;

namespace Mindtalk.App.Controls;

/// <summary>
/// Motion shared by the introduction and the main window (the Mac's Motion.swift).
/// Everything respects "Animation effects" being off in Windows: no drifting light, no
/// rising elements, numbers just appear.
/// </summary>
public static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    public static bool Reduced => !Settings.AnimationsEnabled;

    /// Elements of a page rise into place one after another.
    public static T Stagger<T>(T element, int index, double after = 0) where T : UIElement
    {
        if (Reduced) return element;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(0, 16, 0));
        var delay = TimeSpan.FromSeconds(after + index * 0.08 + 0.1);

        // spring(duration 0.7, bounce 0.15)
        var rise = compositor.CreateSpringVector3Animation();
        rise.FinalValue = Vector3.Zero;
        rise.Period = TimeSpan.FromSeconds(0.7 / (2 * Math.PI));   // composition: 1/ω; SwiftUI: ω = 2π/duration
        rise.DampingRatio = 0.85f;
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1)));
        fade.Duration = TimeSpan.FromSeconds(0.5);
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        void Start()
        {
            visual.StartAnimation("Translation", rise);
            visual.StartAnimation("Opacity", fade);
        }
        if (element is FrameworkElement { IsLoaded: false } fe) fe.Loaded += (_, _) => Start();
        else Start();
        return element;
    }

    /// Pages in the main window rise a little and come into focus.
    public static void PageIn(UIElement element)
    {
        if (Reduced) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(0, 12, 0));
        // spring(duration 0.45)
        var rise = compositor.CreateSpringVector3Animation();
        rise.FinalValue = Vector3.Zero;
        rise.Period = TimeSpan.FromSeconds(0.45 / (2 * Math.PI));
        rise.DampingRatio = 1f;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromSeconds(0.3);
        visual.StartAnimation("Translation", rise);
        visual.StartAnimation("Opacity", fade);
    }
}

/// <summary>A number that counts up to its value when it first appears (and animates between values).</summary>
public sealed partial class CountUp : Grid
{
    private readonly Animated _value = new(0);
    private long _lastFrame;
    private bool _rendering, _shown;
    private int _target;

    /// The number itself, to style.
    public TextBlock Label { get; } = new();

    public CountUp(int target)
    {
        _target = target;
        Children.Add(Label);
        Label.Text = StatsStore.Number(0);
        Loaded += (_, _) =>
        {
            if (Motion.Reduced) { _value.Set(_target); Label.Text = StatsStore.Number(_target); return; }
            // After a moment, so the page has settled.
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(150);
                _shown = true;
                _value.EaseOut(_target, 1.1);
                Start();
            });
        };
        Unloaded += (_, _) => Stop();
    }

    public int Target
    {
        get => _target;
        set
        {
            if (value == _target) return;
            _target = value;
            if (!_shown || Motion.Reduced) { _value.Set(value); Label.Text = StatsStore.Number(value); return; }
            _value.EaseOut(value, 0.5);
            Start();
        }
    }

    private void Start()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var moving = _value.Step(Math.Min(0.1, (now - _lastFrame) / 1000.0));
        _lastFrame = now;
        Label.Text = StatsStore.Number((int)Math.Round(_value.Value));
        if (!moving) Stop();
    }
}

/// <summary>
/// Soft light drifting slowly behind the paper — full strength on the introduction's
/// welcome, a soft wash behind page headers elsewhere. (The Mac draws a 3×3 mesh gradient;
/// here four radial washes move the same way.)
/// </summary>
public sealed partial class WarmGlow : Grid
{
    private readonly (Rectangle Shape, RadialGradientBrush Brush)[] _lights = new (Rectangle, RadialGradientBrush)[4];
    private readonly Rectangle _fade = new();
    private bool _rendering;
    private double _intensity;
    private readonly Animated _shown;

    public WarmGlow(double intensity)
    {
        _intensity = intensity;
        _shown = new Animated(intensity);
        IsHitTestVisible = false;
        for (var i = 0; i < _lights.Length; i++)
        {
            var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
            var shape = new Rectangle { Fill = brush };
            _lights[i] = (shape, brush);
            Children.Add(shape);
        }
        // The light fades out towards the bottom: paper painted over it.
        Children.Add(_fade);
        ActualThemeChanged += (_, _) => Paint(0);
        Loaded += (_, _) => { Paint(0); Start(); };
        Unloaded += (_, _) => Stop();
    }

    public double Intensity
    {
        get => _intensity;
        set { _intensity = value; _shown.EaseOut(value, 0.9); Start(); }
    }

    private void Start()
    {
        if (_rendering || !IsLoaded) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private long _lastFrame, _lastPaint;

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _lastFrame) / 1000.0);
        _lastFrame = now;
        var fading = _shown.Step(dt);
        // 30 frames a second is plenty for light this slow.
        if (now - _lastPaint < 33 && !fading) return;
        _lastPaint = now;
        if (Motion.Reduced) { Paint(0); if (!fading) Stop(); return; }
        Paint(now / 1000.0 * 0.25);
    }

    private void Paint(double t)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var paper = dark ? Color.FromArgb(255, 14, 14, 14) : Color.FromArgb(255, 251, 251, 251);
        Color Tint(double white, double alpha, double darkAlpha) => dark
            ? Color.FromArgb((byte)(darkAlpha * 255), 255, 255, 255)
            : Color.FromArgb((byte)(alpha * 255), (byte)(white * 255), (byte)(white * 255), (byte)(white * 255));
        // The mesh's coloured points: top left, top middle (drifting), top right, and the centre.
        var lights = new (Point At, Color Color, double Radius)[]
        {
            (new Point(0, 0), Tint(0.80, 0.55, 0.10), 0.75),
            (new Point(0.5 + Math.Sin(t) * 0.12, 0), Tint(0.72, 0.50, 0.16), 0.7),
            (new Point(1, 0), Tint(0.86, 0.50, 0.07), 0.75),
            (new Point(0.5 + Math.Cos(t * 0.7) * 0.18, 0.5 + Math.Sin(t * 0.9) * 0.12), Tint(0.88, 0.35, 0.05), 0.5),
        };
        var strength = _shown.Value;
        for (var i = 0; i < lights.Length; i++)
        {
            var (at, color, radius) = lights[i];
            var brush = _lights[i].Brush;
            brush.Center = at;
            brush.GradientOrigin = at;
            brush.RadiusX = radius;
            brush.RadiusY = radius * 1.4;
            brush.GradientStops.Clear();
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 });
            _lights[i].Shape.Opacity = strength;
        }
        _fade.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0, paper.R, paper.G, paper.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(115, paper.R, paper.G, paper.B), Offset = 0.5 },
                new GradientStop { Color = paper, Offset = 1 },
            },
        };
    }
}

/// <summary>The "waveform" symbol: bars like the Mac's SF Symbol.</summary>
public sealed partial class WaveIcon : Canvas
{
    private static readonly double[] Heights = [0.32, 0.62, 1, 0.72, 0.46, 0.86, 0.38];

    public WaveIcon(double size, string brush = "PrimaryBrush")
    {
        Width = Height = size;
        var bar = size / 11.5;
        var gap = (size - bar * Heights.Length) / (Heights.Length - 1);
        for (var i = 0; i < Heights.Length; i++)
        {
            var h = Math.Max(bar, Heights[i] * size * 0.9);
            var r = new Rectangle { Width = bar, Height = h, RadiusX = bar / 2, RadiusY = bar / 2 };
            r.SetValue(Shape.FillProperty, UI.Brush(brush));
            SetLeft(r, i * (bar + gap));
            SetTop(r, (size - h) / 2);
            Children.Add(r);
        }
    }

    public void Recolor(string brush)
    {
        foreach (var child in Children.OfType<Rectangle>()) child.SetValue(Shape.FillProperty, UI.Brush(brush));
    }
}
