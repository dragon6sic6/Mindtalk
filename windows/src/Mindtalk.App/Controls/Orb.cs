using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mindtalk.App.Controls;

/// <summary>
/// Mindtalk 2.0's mark: a sphere of dots that slowly turns — the app icon, Home's stats card,
/// Meetings and the introduction. Dots in front are larger and darker; the ones behind fade.
/// With <see cref="Level"/> it breathes with the voice. Drawn in software into a bitmap
/// (a few hundred dots), and stands still when animations are off.
/// </summary>
public sealed partial class Orb : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly (float X, float Y, float Z, float Size)[] _dots;
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private int _px;
    private double _angle;
    private long _last;
    private bool _running;
    private double _level, _shownLevel;
    private readonly bool _onDark;

    /// <param name="size">Diameter in DIPs.</param>
    /// <param name="onDark">White dots (on the black icon tile, or dark mode).</param>
    /// <param name="count">How many dots.</param>
    public Orb(double size, bool? onDark = null, int count = 720, bool animate = true)
    {
        Width = Height = size;
        _onDark = onDark ?? UI.Dark;
        IsHitTestVisible = false;
        Children.Add(_image);
        _dots = Dots(count);
        _angle = 0.6;

        Loaded += (_, _) =>
        {
            Draw();
            if (animate && !Motion.Reduced) Start();
        };
        Unloaded += (_, _) => Stop();
    }

    /// Voice level 0…1: the sphere swells a little and turns faster.
    public double Level { get => _level; set => _level = Math.Clamp(value, 0, 1); }

    /// Bigger, brighter dots (the app icon's bolder sphere); 1 is the mark's own size.
    public double DotScale { get; init; } = 1;

    private void Start()
    {
        if (_running) return;
        _running = true;
        _last = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _last) / 1000.0);
        // About 30 frames a second is plenty for a slow turn.
        if (dt < 1 / 32.0) return;
        _last = now;
        _shownLevel += (_level - _shownLevel) * Math.Min(1, dt * 10);
        _angle += dt * (0.22 + _shownLevel * 0.9);
        Draw();
    }

    private void Draw()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var px = Math.Max(8, (int)Math.Round(Width * scale));
        if (_bitmap is null || _px != px)
        {
            _px = px;
            _bitmap = new WriteableBitmap(px, px);
            _pixels = new byte[px * px * 4];
            _image.Source = _bitmap;
        }
        Array.Clear(_pixels);
        Paint(_pixels, px, _dots, _angle, _shownLevel, _onDark, DotScale);
        using (var stream = _bitmap.PixelBuffer.AsStream()) stream.Write(_pixels, 0, _pixels.Length);
        _bitmap.Invalidate();
    }

    /// A Fibonacci sphere, each dot a slightly different size — the Mac's mark is hand-scattered.
    internal static (float X, float Y, float Z, float Size)[] Dots(int count)
    {
        var random = new Random(7);
        var dots = new (float, float, float, float)[count];
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < count; i++)
        {
            var y = 1 - (i + 0.5) / count * 2;
            var r = Math.Sqrt(1 - y * y);
            var theta = golden * i + random.NextDouble() * 0.35;
            var jitter = 0.94 + random.NextDouble() * 0.08;
            dots[i] = ((float)(Math.Cos(theta) * r * jitter), (float)(y * jitter), (float)(Math.Sin(theta) * r * jitter),
                       (float)(0.55 + random.NextDouble() * 0.9));
        }
        return dots;
    }

    /// Draws the sphere into <paramref name="pixels"/> (premultiplied BGRA, px × px) — the
    /// orb on screen, and the app icon. <paramref name="dotScale"/> and <paramref name="minRadius"/>
    /// keep the dots legible at icon sizes.
    internal static void Paint(byte[] pixels, int px, (float X, float Y, float Z, float Size)[] dots, double angle, double level,
                               bool onDark, double dotScale = 1, double minRadius = 0, double fill = 0.47)
    {
        var center = px / 2.0;
        var radius = px * fill * (1 + level * 0.06);
        var dotBase = px / 150.0 * dotScale;
        var (sin, cos) = Math.SinCos(angle);
        const double tilt = 0.32;
        var (st, ct) = Math.SinCos(tilt);
        var ink = onDark ? (byte)255 : (byte)17;

        foreach (var (x0, y0, z0, size) in dots)
        {
            // Turn about the vertical axis, then tilt toward the viewer.
            var x = x0 * cos + z0 * sin;
            var z = -x0 * sin + z0 * cos;
            var y = y0 * ct - z * st;
            z = y0 * st + z * ct;
            var depth = (z + 1) / 2;                       // 0 behind … 1 in front
            var alpha = 0.18 + 0.82 * depth * depth;
            var r = Math.Max(minRadius, dotBase * size * (0.55 + 0.75 * depth));
            Disc(pixels, px, center + x * radius, center + y * radius, r, ink, alpha);
        }
    }

    /// An anti-aliased dot, blended over what's there (premultiplied BGRA).
    private static void Disc(byte[] pixels, int px, double cx, double cy, double r, byte ink, double alpha)
    {
        var x0 = (int)Math.Floor(cx - r - 1);
        var x1 = (int)Math.Ceiling(cx + r + 1);
        var y0 = (int)Math.Floor(cy - r - 1);
        var y1 = (int)Math.Ceiling(cy + r + 1);
        for (var y = Math.Max(0, y0); y < Math.Min(px, y1); y++)
        for (var x = Math.Max(0, x0); x < Math.Min(px, x1); x++)
        {
            var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
            var coverage = Math.Clamp(r + 0.5 - d, 0, 1) * alpha;
            if (coverage <= 0) continue;
            var i = (y * px + x) * 4;
            var a = pixels[i + 3] / 255.0;
            var outA = coverage + a * (1 - coverage);
            var c = (byte)Math.Round(ink * coverage + pixels[i] * (1 - coverage));
            pixels[i] = pixels[i + 1] = pixels[i + 2] = c;
            pixels[i + 3] = (byte)Math.Round(outA * 255);
        }
    }
}
