using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Stats;
using Windows.Foundation;

namespace Mindtalk.App.TrayPanel;

/// <summary>The panel's measures, as the Mac's PanelMetrics.</summary>
internal static class Metrics
{
    public const double Width = 330;
    /// Text starts here; row highlights sit 7 pt in from the edge.
    public const double Inset = 18;
    public const double HighlightInset = 7;
    public const double Corner = 18;
}

/// <summary>Small pieces shared by the panel's sections.</summary>
internal static class Parts
{
    /// A design colour at an opacity of its own (the shared brushes can't be changed).
    public static SolidColorBrush Tint(string key, double opacity)
    {
        var brush = (SolidColorBrush)UI.Brush(key);
        return new SolidColorBrush(brush.Color) { Opacity = brush.Opacity * opacity };
    }

    public static TextBlock SectionTitle(string text)
    {
        var title = UI.Text(text, 12, FontWeights.SemiBold, "MutedBrush");
        title.Margin = new Thickness(Metrics.Inset, 6, Metrics.Inset, 4);
        return title;
    }

    public static Rectangle Separator()
    {
        var line = new Rectangle { Height = 1, Margin = new Thickness(Metrics.Inset, 7, Metrics.Inset, 7) };
        UI.Bind(line, Shape.FillProperty, "DividerBrush");
        return line;
    }

    public static Ellipse Dot(string brush, double size = 6)
    {
        var dot = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        UI.Bind(dot, Shape.FillProperty, brush);
        return dot;
    }

    /// Title on the left, a shortcut ("Ctrl+,") quietly on the right.
    public static HoverRow Row(string title, Action action, string? shortcut = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = UI.Text(title, 14);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        grid.Children.Add(text);
        if (shortcut is not null)
        {
            var keys = UI.Text(shortcut, 13, brush: "FaintBrush");
            keys.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(keys, 1);
            grid.Children.Add(keys);
        }
        var row = new HoverRow(grid, action);
        AutomationProperties.SetName(row, title);
        return row;
    }

    /// An action with something before it (a red dot, a bulb), its title, and its key on the right.
    public static HoverRow IconRow(UIElement icon, string title, string? shortcut, Action action)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(icon);
        var text = UI.Text(title, 14);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (shortcut is { Length: > 0 })
        {
            var keys = UI.Text(shortcut, 13, brush: "FaintBrush");
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }
        var row = new HoverRow(grid, action, 32);
        AutomationProperties.SetName(row, title);
        return row;
    }
}

/// <summary>
/// A menu row: the hover is a soft rounded fill, like the system's menu bar menus. One row
/// is lit at a time — the one under the pointer.
/// </summary>
internal sealed partial class HoverRow : Button
{
    private static HoverRow? _lit;
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(9), Opacity = 0 };

    public HoverRow(UIElement content, Action action, double height = 30)
    {
        Style = UI.Style("PlainButtonStyle");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(Metrics.HighlightInset, 0, Metrics.HighlightInset, 0);
        // The keyboard's focus ring follows the hover's shape.
        CornerRadius = _fill.CornerRadius;
        UI.Bind(_fill, Border.BackgroundProperty, "HoverBrush");
        var grid = new Grid { MinHeight = height };
        grid.Children.Add(_fill);
        if (content is FrameworkElement element)
        {
            var padding = Metrics.Inset - Metrics.HighlightInset;
            element.Margin = new Thickness(padding, 0, padding, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        grid.Children.Add(content);
        Content = grid;
        Click += (_, _) => action();
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        PointerCanceled += (_, _) => Hover(false);
    }

    public bool Hovering { get; private set; }
    public event Action<bool>? HoverChanged;

    private void Hover(bool on)
    {
        if (on == Hovering) return;
        if (on && _lit != this) _lit?.Hover(false);
        _lit = on ? this : _lit == this ? null : _lit;
        Hovering = on;
        _fill.Opacity = on ? 1 : 0;
        HoverChanged?.Invoke(on);
    }

    /// The panel closed: no row is still lit when it opens again.
    public static void ClearHover() => _lit?.Hover(false);
}

/// <summary>
/// Monday to Sunday in seven slim bars: today in ink, other days grey, days without words a
/// hairline.
/// </summary>
internal static class WeekBars
{
    public const double Width = 7 * 6 + 6 * 3;

    public static StackPanel Make(IReadOnlyList<WeekDay> week)
    {
        var peak = Math.Max(1, week.Max(d => d.Stat.Words));
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        foreach (var day in week)
        {
            var words = day.Stat.Words;
            bars.Children.Add(new Border
            {
                Width = 6,
                Height = words > 0 ? Math.Max(3, (double)words / peak * 20) : 2,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Parts.Tint("InkBrush", day.IsToday ? 1 : words > 0 ? 0.3 : day.IsFuture ? 0.08 : 0.15),
            });
        }
        return bars;
    }
}

/// <summary>A small ring filling clockwise from the top: how far a download has come.</summary>
internal sealed partial class DownloadRing : Grid
{
    public DownloadRing(double progress)
    {
        Width = Height = 12;
        VerticalAlignment = VerticalAlignment.Center;
        var track = new Ellipse { StrokeThickness = 2 };
        UI.Bind(track, Shape.StrokeProperty, "DividerBrush");
        Children.Add(track);
        var p = Math.Clamp(progress, 0.02, 1);
        Shape arc;
        if (p >= 0.999) arc = new Ellipse { StrokeThickness = 2 };
        else
        {
            const double c = 6, r = 5;
            var angle = (p * 360 - 90) * Math.PI / 180;
            var figure = new PathFigure { StartPoint = new Point(c, c - r) };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(c + r * Math.Cos(angle), c + r * Math.Sin(angle)),
                Size = new Size(r, r),
                IsLargeArc = p > 0.5,
                SweepDirection = SweepDirection.Clockwise,
            });
            arc = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { figure } },
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
        }
        UI.Bind(arc, Shape.StrokeProperty, "InkBrush");
        Children.Add(arc);
    }
}
