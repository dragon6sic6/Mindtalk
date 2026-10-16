using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.Core;

namespace Mindtalk.App.Controls;

/// <summary>
/// Instead of a sidebar: a slim pill floating at the window's leading edge, icons only.
/// Point at it and the whole pill widens to show the labels — then folds away again,
/// leaving the page the full width of the window. (The Mac's TabBar.swift.)
/// </summary>
public sealed partial class FloatingTabBar : Grid
{
    private const double Folded = 40, Open = 196, RowHeight = 40, Spacing = 4;

    private readonly App _app;
    private readonly Action<Page> _select;
    private readonly List<(Page Page, Grid Row, Grid IconHost, TextBlock Label, TextBlock Count, Border Hover)> _tabs = [];
    private readonly Ellipse _selection = new() { Width = 40, Height = 40 };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7 };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _divider = new() { Height = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 6, 8, 6) };
    private readonly Animated _width = new(Folded), _selectionY = new(0), _labels = new(0);
    private Page _page;
    private long _lastFrame;
    private bool _rendering;

    public FloatingTabBar(App app, Page page, Action<Page> select)
    {
        _app = app;
        _page = page;
        _select = select;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

        var stack = new StackPanel { Spacing = Spacing };
        // The ink circle behind the chosen tab slides between them.
        var tabs = new Grid();
        var canvas = new Canvas { IsHitTestVisible = false };
        UI.Bind(_selection, Shape.FillProperty, "InkBrush");
        canvas.Children.Add(_selection);
        tabs.Children.Add(canvas);
        var rows = new StackPanel { Spacing = Spacing };
        foreach (var p in Enum.GetValues<Page>()) rows.Children.Add(Tab(p));
        tabs.Children.Add(rows);
        stack.Children.Add(tabs);

        UI.Bind(_divider, Shape.FillProperty, "DividerBrush");
        stack.Children.Add(_divider);
        stack.Children.Add(StatusRow());

        var pill = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(28),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        UI.Bind(pill, Border.BackgroundProperty, "CardBrush");
        UI.Bind(pill, Border.BorderBrushProperty, "FieldStrokeBrush");
        var raised = SoftShadow.Around(pill, 28, 16, 6, 0.12);
        Children.Add(raised);

        PointerEntered += (_, _) => Expand(true);
        PointerExited += (_, _) => Expand(false);
        _selectionY.Set(IndexOf(page) * (RowHeight + Spacing));
        Apply();
        Refresh();
    }

    private static int IndexOf(Page page) => Array.IndexOf(Enum.GetValues<Page>(), page);

    public static string Title(Page page) => page switch
    {
        Page.Dictation => Loc.T("Diktering", "Dictation"),
        Page.Recent => Loc.T("Senaste", "Recent"),
        Page.Vocabulary => Loc.T("Ordlista", "Vocabulary"),
        _ => Loc.T("Inställningar", "Settings"),
    };

    private UIElement Icon(Page page) => page switch
    {
        Page.Dictation => new WaveIcon(16),
        Page.Recent => UI.Icon(Icons.Clock, 16),
        Page.Vocabulary => UI.Icon(Icons.Book, 16),
        _ => UI.Icon(Icons.Sliders, 16),
    };

    private Button Tab(Page page)
    {
        var iconHost = new Grid { Width = 40, Height = 40 };
        iconHost.Children.Add(new ContentControl { Content = Icon(page), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsTabStop = false });
        var label = UI.Text(Title(page), 15);
        label.Margin = new Thickness(12, 0, 0, 0);
        var count = UI.Text("", 12, brush: "MutedBrush");
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.Margin = new Thickness(0, 0, 12, 0);
        var hover = new Border { CornerRadius = new CornerRadius(20), Opacity = 0 };
        UI.Bind(hover, Border.BackgroundProperty, "ChipBrush");

        var row = new Grid { Height = RowHeight, Width = Folded, HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(hover, 3);
        row.Children.Add(hover);
        row.Children.Add(iconHost);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        var button = new Button { Style = UI.Style("PlainButtonStyle"), Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, Title(page));
        ToolTipService.SetToolTip(button, Title(page));
        button.Click += (_, _) => { if (_page != page) _select(page); };
        button.PointerEntered += (_, _) => hover.Opacity = 0.7;
        button.PointerExited += (_, _) => hover.Opacity = 0;
        _tabs.Add((page, row, iconHost, label, count, hover));
        return button;
    }

    /// Ready or not, at the foot of the bar: a dot, and the words when open.
    private Grid StatusRow()
    {
        var row = new Grid { Height = 22, HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _dot.HorizontalAlignment = HorizontalAlignment.Center;
        _dot.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_dot);
        _status.Margin = new Thickness(12, 0, 0, 0);
        UI.Bind(_status, TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_status, 1);
        row.Children.Add(_status);
        _tabs.Add((Page.Dictation, row, new Grid(), _status, new TextBlock(), new Border()));
        return row;
    }

    public Page Page
    {
        get => _page;
        set
        {
            _page = value;
            _selectionY.Spring(IndexOf(value) * (RowHeight + Spacing), 0.45, 0.15);
            Start();
            Refresh();
        }
    }

    public void Refresh()
    {
        foreach (var tab in _tabs.Take(4))
        {
            var selected = tab.Page == _page;
            var icon = ((ContentControl)tab.IconHost.Children[0]).Content;
            var brush = selected ? "OnInkBrush" : "PrimaryBrush";
            if (icon is FontIcon f) f.SetValue(IconElement.ForegroundProperty, UI.Brush(brush));
            if (icon is WaveIcon w) w.Recolor(brush);
            tab.Label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            if (tab.Page == Page.Recent) tab.Count.Text = _app.Recent.Entries.Count > 0 ? _app.Recent.Entries.Count.ToString() : "";
        }
        UI.Bind(_dot, Shape.FillProperty, _app.NeedsSetup ? "OrangeBrush" : "GoodBrush");
        _status.Text = StatusText;
        ToolTipService.SetToolTip(_dot, _labels.Value < 0.5 ? StatusText : null);
    }

    private string StatusText => _app.Model switch
    {
        ModelState.Downloading => Loc.T($"Laddar ned {(int)(_app.EngineProgress * 100)} %", $"Downloading {(int)(_app.EngineProgress * 100)}%"),
        ModelState.Loading => Loc.T("Startar…", "Starting…"),
        _ => _app.Issue is { } issue ? _app.IssueTitle(issue) : Loc.T("Redo", "Ready"),
    };

    private void Expand(bool open)
    {
        // spring(duration 0.4, bounce 0.12)
        _width.Spring(open ? Open : Folded, 0.4, 0.12);
        _labels.Spring(open ? 1 : 0, 0.4, 0.12);
        Start();
    }

    private void Start()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _lastFrame) / 1000.0);
        _lastFrame = now;
        var moving = _width.Step(dt) | _selectionY.Step(dt) | _labels.Step(dt);
        Apply();
        if (!moving) { _rendering = false; CompositionTarget.Rendering -= OnFrame; }
    }

    private void Apply()
    {
        var width = Math.Max(Folded, _width.Value);
        foreach (var tab in _tabs)
        {
            tab.Row.Width = width;
            tab.Label.Opacity = Math.Clamp(_labels.Value, 0, 1);
            tab.Count.Opacity = tab.Label.Opacity;
            tab.Label.Visibility = width > Folded + 4 ? Visibility.Visible : Visibility.Collapsed;
        }
        _divider.Width = width - 16;
        Canvas.SetTop(_selection, _selectionY.Value);
    }
}
