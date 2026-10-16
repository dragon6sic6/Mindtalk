using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Mindtalk.App.Controls;

/// <summary>
/// Bars scrolling right-to-left — the last moments of the mic, newest brightest. Used in
/// the dictation HUD, the menu panel, the introduction and the microphone picker.
/// </summary>
public sealed partial class Waveform : Canvas
{
    private Rectangle[] _bars = [];
    private float[] _targets = [];
    private double[] _heights = [];
    private Color _color = Colors.White;
    private double _dim = 1;
    private readonly Animated _dimmed = new(1);
    private long _lastFrame;
    private bool _rendering;

    public Waveform()
    {
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => Layout();
    }

    public double Gap { get; set; } = 3;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            foreach (var bar in _bars) bar.Fill = new SolidColorBrush(value);
            Layout();
        }
    }

    /// Faded while the text is being written.
    public bool Dimmed
    {
        set => _dimmed.EaseOut(value ? 0.4 : 1, 0.3);
    }

    /// The levels to show, oldest first (0…1); the count sets the number of bars.
    public void SetLevels(float[] levels)
    {
        if (levels.Length != _bars.Length) Rebuild(levels.Length);
        Array.Copy(levels, _targets, levels.Length);
        Start();
    }

    private void Rebuild(int count)
    {
        Children.Clear();
        _bars = new Rectangle[count];
        _targets = new float[count];
        _heights = new double[count];
        for (var i = 0; i < count; i++)
        {
            _bars[i] = new Rectangle { Fill = new SolidColorBrush(_color) };
            Children.Add(_bars[i]);
        }
        Layout();
    }

    private void Start()
    {
        if (_rendering || !IsLoaded) return;
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
        var dt = Math.Clamp((now - _lastFrame) / 1000.0, 0, 0.1);
        _lastFrame = now;
        var moving = _dimmed.Step(dt);
        _dim = _dimmed.Value;
        // Like SwiftUI's linear 0.06 s animation between level updates.
        var k = Math.Min(1, dt / 0.06);
        for (var i = 0; i < _bars.Length; i++)
        {
            var target = Height(_targets[i]);
            var delta = target - _heights[i];
            if (Math.Abs(delta) > 0.01) moving = true;
            _heights[i] += delta * k;
        }
        Layout();
        if (!moving) Stop();
    }

    private double BarWidth => _bars.Length == 0 ? 1 : Math.Max(1, (ActualWidth - Gap * (_bars.Length - 1)) / _bars.Length);

    /// Square root lifts quiet speech so the wave reads at a distance too.
    private double Height(float level) =>
        Math.Max(BarWidth, Math.Min(1, Math.Sqrt(Math.Max(0, level)) * 1.15) * ActualHeight);

    private void Layout()
    {
        var count = _bars.Length;
        if (count == 0 || ActualWidth <= 0) return;
        var width = BarWidth;
        var total = width * count + Gap * (count - 1);
        var x = (ActualWidth - total) / 2;
        for (var i = 0; i < count; i++)
        {
            var bar = _bars[i];
            var h = Math.Max(width, _heights[i]);
            bar.Width = width;
            bar.Height = h;
            bar.RadiusX = bar.RadiusY = width / 2;
            SetLeft(bar, x + i * (width + Gap));
            SetTop(bar, (ActualHeight - h) / 2);
            var fade = 0.35 + 0.65 * i / Math.Max(1, count - 1);
            bar.Opacity = fade * _dim;
        }
    }
}

/// <summary>Mindtalk's text cursor — the I-beam from the icon.</summary>
public sealed partial class CursorGlyph : Canvas
{
    private readonly Rectangle _stem = new(), _top = new(), _bottom = new();
    private const double Stroke = 1.8;

    public CursorGlyph()
    {
        Children.Add(_stem);
        Children.Add(_top);
        Children.Add(_bottom);
        SizeChanged += (_, _) => Layout();
        Fill = new SolidColorBrush(Colors.White);
    }

    public Brush Fill
    {
        set { _stem.Fill = _top.Fill = _bottom.Fill = value; }
    }

    private void Layout()
    {
        double w = ActualWidth, h = ActualHeight;
        foreach (var r in new[] { _stem, _top, _bottom }) r.RadiusX = r.RadiusY = Stroke / 2;
        _stem.Width = Stroke; _stem.Height = h;
        SetLeft(_stem, (w - Stroke) / 2); SetTop(_stem, 0);
        _top.Width = w; _top.Height = Stroke;
        SetLeft(_top, 0); SetTop(_top, 0);
        _bottom.Width = w; _bottom.Height = Stroke;
        SetLeft(_bottom, 0); SetTop(_bottom, h - Stroke);
    }
}
