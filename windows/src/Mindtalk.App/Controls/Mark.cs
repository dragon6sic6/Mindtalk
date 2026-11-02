using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Mindtalk.App.Controls;

/// <summary>
/// Mindtalk's mark — voice becomes text: a sound wave that settles into a straight line of
/// text and ends in a text cursor. One geometry, as in the Mac app's Mark.swift, in the
/// icon's 1024 design space (y up) laid out on the 824-pt tile of the macOS grid.
/// </summary>
public static class Mark
{
    public const double StrokeWidth = 30;

    /// The wave's centre line. <paramref name="amplitude"/> scales the swell (1 = the icon);
    /// <paramref name="phase"/> slides the wave along, for animation.
    public static List<Point> Wave(double amplitude = 1, double phase = 0)
    {
        const double x0 = 230, xMid = 540, x1 = 700, y = 512;
        const int steps = 160;
        var points = new List<Point>(steps + 2) { new(x0, y) };
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            // The swell rises and fades to nothing, so the wave flows into the line.
            var amp = 150 * amplitude * Math.Sin(t * Math.PI) * (1 - t * 0.35);
            points.Add(new Point(x0 + (xMid - x0) * t, y + amp * Math.Sin(t * 4.2 * Math.PI + phase)));
        }
        points.Add(new Point(x1, y));
        return points;
    }

    /// The I-beam text cursor at the end of the line: three capsules.
    public static Rect[] Cursor
    {
        get
        {
            const double cx = 760, cy = 512, h = 300, stem = 28, serif = 96;
            return
            [
                new Rect(cx - stem / 2, cy - h / 2, stem, h),
                new Rect(cx - serif / 2, cy + h / 2 - stem, serif, stem),
                new Rect(cx - serif / 2, cy - h / 2, serif, stem),
            ];
        }
    }

    /// Maps design space onto a tile of <paramref name="size"/> points (y down), as macOS
    /// lays the icon's glyph over its full-bleed tile.
    public static Point OnTile(Point p, double size)
    {
        const double k = 1024.0 / 824;
        var s = size / 1024;
        return new Point(s * ((p.X - 512) * k + 512), size - s * ((p.Y - 512) * k + 512));
    }
}

/// <summary>
/// The app icon — the mark on its black tile — drawn live. Idle it's the icon; animated,
/// the wave rolls along and the cursor blinks; given levels (while dictating) the wave
/// swells with your voice.
/// </summary>
public sealed partial class LogoTile : Grid
{
    private readonly double _size;
    private readonly Polyline _wave = new();
    private readonly Microsoft.UI.Xaml.Shapes.Path _cursor = new();
    private readonly Func<float[]?>? _levels;
    private bool _animated, _rendering;

    public LogoTile(double size, bool animated = false, Func<float[]?>? levels = null)
    {
        _size = size;
        _levels = levels;
        Width = Height = size;
        var radius = new CornerRadius(size * 0.225);
        // Deep black with a little light at the top, and a fine edge.
        Children.Add(new Border
        {
            CornerRadius = radius,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
                GradientStops = { new GradientStop { Color = Gray(0.24), Offset = 0 }, new GradientStop { Color = Gray(0.04), Offset = 1 } },
            },
        });
        Children.Add(new Border
        {
            CornerRadius = radius,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 0.5),
                GradientStops = { new GradientStop { Color = Color.FromArgb(41, 255, 255, 255), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 } },
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
            BorderThickness = new Thickness(Math.Max(0.5, size / 118)),
        });
        var line = Mark.StrokeWidth * size / 824;
        _wave.Stroke = new SolidColorBrush(Colors.White);
        _wave.StrokeThickness = line;
        _wave.StrokeLineJoin = PenLineJoin.Round;
        _wave.StrokeStartLineCap = _wave.StrokeEndLineCap = PenLineCap.Round;
        _cursor.Fill = new SolidColorBrush(Colors.White);
        var glyph = new Canvas();
        glyph.Children.Add(_wave);
        glyph.Children.Add(_cursor);
        Children.Add(glyph);
        _cursor.Data = CursorGeometry(size);
        Draw(0);
        Animated = animated;
        Unloaded += (_, _) => StopFrames();
        Loaded += (_, _) => { if (_animated || _levels is not null) StartFrames(); };
    }

    /// The wave rolls and the cursor blinks.
    public bool Animated
    {
        get => _animated;
        set
        {
            _animated = value;
            if (value || _levels is not null) StartFrames();
            else { StopFrames(); Draw(0); }
        }
    }

    private static Color Gray(double white)
    {
        var v = (byte)Math.Round(white * 255);
        return Color.FromArgb(255, v, v, v);
    }

    /// The cursor's three capsules, on a tile of <paramref name="size"/>.
    private static Geometry CursorGeometry(double size)
    {
        var path = new PathGeometry();
        foreach (var r in Mark.Cursor)
        {
            var a = Mark.OnTile(new Point(r.X, r.Y + r.Height), size);
            var b = Mark.OnTile(new Point(r.X + r.Width, r.Y), size);
            path.Figures.Add(Capsule(new Rect(a, b)));
        }
        return path;
    }

    private static PathFigure Capsule(Rect r)
    {
        var radius = Math.Min(r.Width, r.Height) / 2;
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        var arc = new Size(radius, radius);
        if (r.Width >= r.Height)
        {
            figure.StartPoint = new Point(r.Left + radius, r.Top);
            figure.Segments.Add(new LineSegment { Point = new Point(r.Right - radius, r.Top) });
            figure.Segments.Add(new ArcSegment { Point = new Point(r.Right - radius, r.Bottom), Size = arc, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = new Point(r.Left + radius, r.Bottom) });
            figure.Segments.Add(new ArcSegment { Point = new Point(r.Left + radius, r.Top), Size = arc, SweepDirection = SweepDirection.Clockwise });
        }
        else
        {
            figure.StartPoint = new Point(r.Left, r.Top + radius);
            figure.Segments.Add(new ArcSegment { Point = new Point(r.Right, r.Top + radius), Size = arc, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = new Point(r.Right, r.Bottom - radius) });
            figure.Segments.Add(new ArcSegment { Point = new Point(r.Left, r.Bottom - radius), Size = arc, SweepDirection = SweepDirection.Clockwise });
        }
        return figure;
    }

    private void StartFrames()
    {
        if (_rendering || !IsLoaded) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e) => Draw(Environment.TickCount64 / 1000.0);

    private void Draw(double t)
    {
        var levels = _levels?.Invoke();
        double amplitude;
        if (levels is { Length: > 0 })
        {
            // Follow the voice: quiet is a ripple, speech is a full swell.
            var recent = levels[^Math.Min(4, levels.Length)..].Average();
            amplitude = 0.25 + Math.Min(1, Math.Sqrt(Math.Max(levels[^1], recent)) * 1.1) * 0.95;
        }
        else amplitude = _animated ? 0.9 + 0.12 * Math.Sin(t * 1.7) : 1;
        var phase = _animated || levels is not null ? -t * 4 : 0;
        var points = new PointCollection();
        foreach (var p in Mark.Wave(amplitude, phase)) points.Add(Mark.OnTile(p, _size));
        _wave.Points = points;
        var on = !_animated || (int)(t * 1.9) % 2 == 0;
        _cursor.Opacity = on ? 1 : 0.2;
    }
}
