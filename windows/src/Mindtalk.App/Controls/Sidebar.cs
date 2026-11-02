using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.Core;

namespace Mindtalk.App.Controls;

/// <summary>
/// Mindtalk 2.0's sidebar: a floating card at the window's top-left with the pages — Home,
/// Dictation, Meetings, Thoughts (with a count), Vocabulary, Settings — and, under a line,
/// whether Mindtalk is ready. In Settings it turns into the settings' own list: Back, then
/// General, Language, Dictation, Meetings and AI apps, with the version at the foot.
/// The chosen row sits on an ink circle that slides between rows.
/// </summary>
public sealed partial class Sidebar : Grid
{
    public const double CardWidth = 228;
    private const double RowHeight = 46, Spacing = 2;

    private readonly App _app;
    private readonly Action<Page> _selectPage;
    private readonly Action<SettingsSection> _selectSection;
    private readonly StackPanel _rows = new() { Spacing = Spacing };
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Ellipse _selection = new() { Width = 40, Height = 40 };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7 };
    private readonly TextBlock _status = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _version = new() { FontSize = 13, Margin = new Thickness(52, 2, 0, 0) };
    private readonly List<(object Key, FrameworkElement Row, ContentControl Icon, TextBlock Label, TextBlock Count, int Index)> _items = [];
    private readonly Animated _selectionY = new(0);
    private long _lastFrame;
    private bool _rendering;
    private bool _settings;
    private Page _page;
    private SettingsSection _section;

    public Sidebar(App app, Page page, SettingsSection section, Action<Page> selectPage, Action<SettingsSection> selectSection)
    {
        _app = app;
        _page = page;
        _section = section;
        _selectPage = selectPage;
        _selectSection = selectSection;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        UI.Bind(_selection, Shape.FillProperty, "InkBrush");
        _canvas.Children.Add(_selection);
        var list = new Grid();
        list.Children.Add(_canvas);
        list.Children.Add(_rows);

        UI.Bind(_status, TextBlock.ForegroundProperty, "MutedBrush");
        UI.Bind(_version, TextBlock.ForegroundProperty, "MutedBrush");
        var divider = new Rectangle { Height = 1, Margin = new Thickness(14, 10, 14, 10) };
        UI.Bind(divider, Shape.FillProperty, "DividerBrush");
        var statusRow = new Grid { Height = 26 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dot.HorizontalAlignment = HorizontalAlignment.Center;
        statusRow.Children.Add(_dot);
        _status.Margin = new Thickness(12, 0, 4, 0);
        Grid.SetColumn(_status, 1);
        statusRow.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(list);
        stack.Children.Add(divider);
        stack.Children.Add(statusRow);
        stack.Children.Add(_version);

        var card = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(9, 9, 9, 14),
            CornerRadius = new CornerRadius(26),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        UI.Bind(card, Border.BackgroundProperty, "SidebarBrush");
        UI.Bind(card, Border.BorderBrushProperty, "FieldStrokeBrush");
        Children.Add(SoftShadow.Around(card, 26, 18, 6, 0.10));

        Build();
    }

    public static string Title(Page page) => page switch
    {
        Page.Home => Loc.T("Hem", "Home"),
        Page.Dictation => Loc.T("Diktering", "Dictation"),
        Page.Meetings => Loc.T("Möten", "Meetings"),
        Page.Thoughts => Loc.T("Tankar", "Thoughts"),
        Page.Vocabulary => Loc.T("Ordlista", "Vocabulary"),
        _ => Loc.T("Inställningar", "Settings"),
    };

    public static string Title(SettingsSection section) => section switch
    {
        SettingsSection.General => Loc.T("Allmänt", "General"),
        SettingsSection.Language => Loc.T("Språk", "Language"),
        SettingsSection.Dictation => Loc.T("Diktering", "Dictation"),
        SettingsSection.Meetings => Loc.T("Möten", "Meetings"),
        _ => Loc.T("AI-appar", "AI apps"),
    };

    public static string Glyph(Page page) => page switch
    {
        Page.Home => Icons.Home,
        Page.Dictation => Icons.Mic,
        Page.Meetings => Icons.People,
        Page.Thoughts => Icons.Lightbulb,
        Page.Vocabulary => Icons.Dictionary,
        _ => Icons.Sliders,
    };

    public static string Glyph(SettingsSection section) => section switch
    {
        SettingsSection.General => Icons.Gear,
        SettingsSection.Language => Icons.Globe,
        SettingsSection.Dictation => Icons.Font,
        SettingsSection.Meetings => Icons.People,
        _ => Icons.Nodes,
    };

    /// Shows the pages, or the settings' sections.
    public void Show(Page page, SettingsSection section)
    {
        var settings = page == Page.Settings;
        _page = page;
        _section = section;
        if (settings != _settings) { _settings = settings; Build(); return; }
        MoveSelection(animated: true);
        Refresh();
    }

    private void Build()
    {
        _rows.Children.Clear();
        _items.Clear();
        var index = 0;
        if (!_settings)
        {
            foreach (var page in Enum.GetValues<Page>())
                _rows.Children.Add(Row(page, Title(page), Glyph(page), () => { if (_page != page) _selectPage(page); }, index++));
        }
        else
        {
            _rows.Children.Add(Row("back", Loc.T("Tillbaka", "Back"), Icons.ChevronLeft, () => _selectPage(Page.Home), index++));
            var caption = new TextBlock
            {
                Text = Loc.T("INSTÄLLNINGAR", "SETTINGS"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 140,
                Margin = new Thickness(18, 12, 0, 8),
            };
            UI.Bind(caption, TextBlock.ForegroundProperty, "MutedBrush");
            _rows.Children.Add(caption);
            foreach (var section in Enum.GetValues<SettingsSection>())
                _rows.Children.Add(Row(section, Title(section), Glyph(section), () => { if (_section != section) _selectSection(section); }, index++));
        }
        _version.Visibility = _settings ? Visibility.Visible : Visibility.Collapsed;
        _version.Text = $"Mindtalk {App.Version}";
        MoveSelection(animated: false);
        Refresh();
    }

    private Button Row(object key, string title, string glyph, Action click, int index)
    {
        var icon = new ContentControl
        {
            Content = UI.Icon(glyph, 18),
            Width = 40,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false,
        };
        var label = UI.Text(title, 15);
        label.Margin = new Thickness(12, 0, 0, 0);
        var count = UI.Text("", 13, brush: "MutedBrush");
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.Margin = new Thickness(0, 0, 14, 0);
        var hover = new Border { CornerRadius = new CornerRadius(22), Opacity = 0 };
        UI.Bind(hover, Border.BackgroundProperty, "HoverBrush");

        var row = new Grid { Height = RowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(hover, 3);
        row.Children.Add(hover);
        row.Children.Add(icon);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        var button = new Button
        {
            Style = UI.Style("PlainButtonStyle"),
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        button.PointerEntered += (_, _) => hover.Opacity = IsSelected(key) ? 0 : 1;
        button.PointerExited += (_, _) => hover.Opacity = 0;
        _items.Add((key, button, icon, label, count, index));
        return button;
    }

    private bool IsSelected(object key) => _settings ? key is SettingsSection s && s == _section : key is Page p && p == _page;

    private bool _waitingForLayout;

    private void MoveSelection(bool animated)
    {
        var item = _items.FirstOrDefault(i => IsSelected(i.Key));
        _selection.Visibility = item.Row is null ? Visibility.Collapsed : Visibility.Visible;
        if (item.Row is null) return;
        if (item.Row.ActualHeight > 0) { Place(item.Row, animated); return; }
        // Not laid out yet. One wait, and it places whichever row is chosen by then — the
        // page may change again before layout (a meeting opened into a new window).
        if (_waitingForLayout) return;
        _waitingForLayout = true;
        _rows.LayoutUpdated += OnLayout;

        void OnLayout(object? s, object e)
        {
            var current = _items.FirstOrDefault(i => IsSelected(i.Key));
            if (current.Row is { ActualHeight: <= 0 }) return;
            _rows.LayoutUpdated -= OnLayout;
            _waitingForLayout = false;
            if (current.Row is not null) Place(current.Row, animated: false);
        }
    }

    /// Rows may sit below the caption: measure where the chosen one is.
    private void Place(FrameworkElement row, bool animated)
    {
        var y = row.TransformToVisual(_rows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + (RowHeight - 40) / 2;
        if (animated && !Motion.Reduced && _selectionY.Value != 0) { _selectionY.Spring(y, 0.45, 0.15); Start(); }
        else { _selectionY.Set(y); Canvas.SetTop(_selection, y); }
    }

    public void Refresh()
    {
        foreach (var item in _items)
        {
            var selected = IsSelected(item.Key);
            if (item.Icon.Content is FontIcon f) f.SetValue(IconElement.ForegroundProperty, UI.Brush(selected ? "OnInkBrush" : "PrimaryBrush"));
            item.Label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            if (item.Key is Page.Thoughts)
                item.Count.Text = _app.Thoughts.OpenCount > 0 ? _app.Thoughts.OpenCount.ToString(Loc.Culture) : "";
        }
        UI.Bind(_dot, Shape.FillProperty, _app.NeedsSetup ? "OrangeBrush" : _app.IsRecordingMeeting ? "RecordingBrush" : "MutedBrush");
        _status.Text = StatusText;
    }

    private string StatusText
    {
        get
        {
            if (_app.IsRecordingMeeting) return Loc.T("Spelar in ett möte", "Recording a meeting");
            return _app.Model switch
            {
                ModelState.Downloading => Loc.T($"Laddar ned {(int)(_app.EngineProgress * 100)} %", $"Downloading {(int)(_app.EngineProgress * 100)}%"),
                ModelState.Loading => Loc.T("Startar…", "Starting…"),
                _ => _app.Issue is { } issue ? _app.IssueTitle(issue) : Loc.T("Redo", "Ready"),
            };
        }
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
        var moving = _selectionY.Step(dt);
        Canvas.SetTop(_selection, _selectionY.Value);
        if (!moving) { _rendering = false; CompositionTarget.Rendering -= OnFrame; }
    }
}
