using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Stats;

namespace Mindtalk.App.Pages;

/// <summary>Three headline numbers and the last seven days, on the Dictation page.</summary>
public sealed partial class StatsCard : Grid
{
    private readonly App _app;
    private readonly (CountUp Number, TextBlock Unit, TextBlock Caption)[] _tiles = new (CountUp, TextBlock, TextBlock)[3];
    private readonly TextBlock _week = UI.Text("", 12, FontWeights.SemiBold, "MutedBrush");
    private readonly WeekChart _chart;
    private readonly FrameworkElement _weekTile;

    public StatsCard(App app)
    {
        _app = app;
        var tiles = new Grid();
        string[] titles = [Loc.T("I dag", "Today"), Loc.T("Den här veckan", "This week"), Loc.T("Totalt", "Total")];
        for (var i = 0; i < 3; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i < 2) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = Tile(titles[i], out _tiles[i]);
            Grid.SetColumn(tile, i * 2);
            tiles.Children.Add(tile);
            if (i == 1) _weekTile = tile;
            if (i < 2)
            {
                var divider = new Rectangle { Width = 1, Height = 58, Margin = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
                UI.Bind(divider, Shape.FillProperty, "DividerBrush");
                Grid.SetColumn(divider, i * 2 + 1);
                tiles.Children.Add(divider);
            }
        }
        _weekTile ??= tiles;
        ToolTipService.SetToolTip(_weekTile,
            Loc.T($"Sparad tid jämfört med att skriva samma ord på tangentbordet i {(int)StatsStore.TypingWordsPerMinute} ord per minut.",
                  $"Time saved compared with typing the same words on a keyboard at {(int)StatsStore.TypingWordsPerMinute} words per minute."));
        _chart = new WeekChart { Height = 118 };
        Children.Add(new Border { Style = UI.Style("CardStyle"), Padding = new Thickness(22), Child = UI.VStack(22, tiles, UI.VStack(10, _week, _chart)) });
        Refresh();
    }

    /// "ord" in Swedish either way; "word" / "words" in English.
    public static string WordUnit(int n) => n == 1 ? Loc.T("ord", "word") : Loc.T("ord", "words");

    private static StackPanel Tile(string title, out (CountUp, TextBlock, TextBlock) parts)
    {
        var number = new CountUp(0);
        number.Label.FontFamily = UI.Serif;
        number.Label.FontSize = 28;
        UI.Bind(number.Label, TextBlock.ForegroundProperty, "PrimaryBrush");
        var unit = UI.Text("", 13, brush: "MutedBrush");
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(0, 0, 0, 5);
        var caption = UI.Text("", 12, brush: "MutedBrush");
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        parts = (number, unit, caption);
        return UI.VStack(4, UI.Text(title, 12, FontWeights.SemiBold, "MutedBrush"), UI.HStack(5, number, unit), caption);
    }

    public void Refresh()
    {
        var stats = _app.Stats;
        var today = stats.Today;
        var weekSaved = stats.Week.Sum(d => d.Stat.SavedSeconds);
        Set(0, today.Words, today.Dictations == 1 ? Loc.T("1 diktering", "1 dictation") : Loc.T($"{today.Dictations} dikteringar", $"{today.Dictations} dictations"));
        Set(1, stats.WeekWords, weekSaved >= 1 ? Loc.T($"≈ {StatsStore.Duration(weekSaved)} sparad", $"≈ {StatsStore.Duration(weekSaved)} saved") : Loc.T("Inget än", "Nothing yet"));
        Set(2, stats.TotalWords, stats.TotalWords == 0 ? Loc.T("Inget än", "Nothing yet")
            : stats.Streak > 1 ? Loc.T($"{stats.Streak} dagar i rad", $"{stats.Streak} days in a row")
            : Loc.T($"≈ {StatsStore.Duration(stats.TotalSaved)} sparad totalt", $"≈ {StatsStore.Duration(stats.TotalSaved)} saved in total"));
        _week.Text = Loc.T($"Vecka {stats.WeekNumber}", $"Week {stats.WeekNumber}");
        _chart.Show(stats.Week);
    }

    private void Set(int i, int count, string caption)
    {
        _tiles[i].Number.Target = count;
        _tiles[i].Unit.Text = WordUnit(count);
        _tiles[i].Caption.Text = caption;
    }
}

/// <summary>
/// Words per day this week, Monday to Sunday: one hue, today at full strength, days still
/// to come as faint placeholders. Values show on today's bar and on hover.
/// </summary>
public sealed partial class WeekChart : Grid
{
    private readonly Grid _bars = new() { ColumnSpacing = 10 };
    private readonly Grid _labels = new() { ColumnSpacing = 10, Padding = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _empty = UI.Muted(Loc.T("Din vecka syns här när du börjar diktera.", "Your week shows up here once you start dictating."), 12, wrap: false);
    private IReadOnlyList<WeekDay> _week = [];
    private int? _hovered;
    private bool _grown;
    private string? _key;

    public WeekChart()
    {
        for (var i = 0; i < 7; i++)
        {
            _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = new Rectangle { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        UI.Bind(line, Shape.FillProperty, "DividerBrush");
        Grid.SetRow(line, 1);
        Grid.SetRow(_labels, 2);
        Children.Add(_bars);
        Children.Add(line);
        Children.Add(_labels);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(0, 0, 0, 0);
        Children.Add(_empty);
    }

    private static readonly CultureInfo Culture = Loc.Culture;

    /// "måndag 21 september" → "Måndag 21 september" (only the first letter).
    private static string SentenceCase(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Culture) + s[1..];

    public void Show(IReadOnlyList<WeekDay> week)
    {
        var key = string.Join(",", week.Select(d => $"{d.Date:yyyyMMdd}:{d.Stat.Words}:{d.IsToday}"));
        if (key == _key) return;
        _key = key;
        _week = week;
        Build();
    }

    private void Build()
    {
        _bars.Children.Clear();
        _labels.Children.Clear();
        var peak = Math.Max(1, _week.Max(d => d.Stat.Words));
        _empty.Visibility = _week.All(d => d.Stat.Words == 0) ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _week.Count; i++)
        {
            var day = _week[i];
            var words = day.Stat.Words;
            var index = i;
            var value = UI.Text(StatsStore.Number(words), 11, FontWeights.SemiBold, "MutedBrush");
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var bar = new Border
            {
                Height = words == 0 ? 2 : Math.Max(4, (double)words / peak * 72),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var column = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            column.Children.Add(value);
            column.Children.Add(bar);
            void Paint()
            {
                var hovered = _hovered == index;
                value.Opacity = words > 0 && (day.IsToday || hovered) ? 1 : 0;
                // Days without words are a quiet baseline, not a bright stub.
                if (day.IsFuture || words == 0) UI.Bind(bar, Border.BackgroundProperty, "DividerBrush");
                else
                {
                    UI.Bind(bar, Border.BackgroundProperty, "AccentBrush");
                    bar.Opacity = day.IsToday || hovered ? 1 : 0.4;
                }
            }
            Paint();
            column.PointerEntered += (_, _) => { _hovered = index; Paint(); };
            column.PointerExited += (_, _) => { if (_hovered == index) _hovered = null; Paint(); };
            var full = SentenceCase(day.Date.ToString("dddd d MMMM", Culture));
            if (!day.IsFuture) ToolTipService.SetToolTip(column, Loc.T($"{full}: {StatsStore.Number(words)} ord", $"{full}: {StatsStore.Number(words)} words"));
            Grid.SetColumn(column, i);
            _bars.Children.Add(column);
            if (!_grown) Grow(bar, i);

            var name = Culture.DateTimeFormat.GetAbbreviatedDayName(day.Date.DayOfWeek).TrimEnd('.');
            var label = UI.Text(SentenceCase(name), 11, day.IsToday ? FontWeights.Bold : FontWeights.Normal,
                                day.IsToday ? "PrimaryBrush" : day.IsFuture ? "FaintBrush" : "MutedBrush");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new Grid();
            cell.Children.Add(label);
            if (day.IsToday)
            {
                var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -7) };
                UI.Bind(dot, Shape.FillProperty, "AccentBrush");
                cell.Children.Add(dot);
            }
            Grid.SetColumn(cell, i);
            _labels.Children.Add(cell);
        }
        _grown = true;
    }

    /// Bars grow up from the baseline, one after another.
    private static void Grow(FrameworkElement bar, int index)
    {
        if (Motion.Reduced) return;
        var visual = ElementCompositionPreview.GetElementVisual(bar);
        var compositor = visual.Compositor;
        visual.Scale = new Vector3(1, 0.02f, 1);
        bar.SizeChanged += (_, e) => visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height, 0);
        // spring(duration 0.7, bounce 0.25), 0.25 s in and 0.06 s apart.
        var grow = compositor.CreateSpringVector3Animation();
        grow.FinalValue = Vector3.One;
        grow.Period = TimeSpan.FromSeconds(0.7 / (2 * Math.PI));   // composition: 1/ω
        grow.DampingRatio = 0.75f;
        grow.DelayTime = TimeSpan.FromSeconds(0.25 + index * 0.06);
        grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        bar.Loaded += (_, _) => visual.StartAnimation("Scale", grow);
    }
}
