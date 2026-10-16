using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Stats;
using Windows.System;

namespace Mindtalk.App.TrayPanel;

/// <summary>
/// What's in the tray panel (the Mac's StatusPanelView): the logo and what Mindtalk is doing,
/// today's words, the languages, the latest dictations, then the app's own rows. Built once
/// per appearance and brought up to date in place, so the row under the pointer keeps its
/// hover while a download ticks along.
/// </summary>
internal sealed class PanelView
{
    private readonly App _app;
    private readonly Action _close;
    private readonly StackPanel _stack = new() { Padding = new Thickness(0, 8, 0, 8) };

    private readonly Grid _logo = new() { Width = 36, Height = 36 };
    private readonly Grid _status = new() { MinHeight = 24 };
    private readonly Waveform _wave = new() { Width = 46, Height = 16, Gap = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Grid _today = new() { Height = 40, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Margin = new Thickness(Metrics.Inset, 0, Metrics.Inset, 0) };
    private readonly List<LanguageRow> _languages = [];
    private readonly StackPanel _recent = new();
    private readonly List<RecentRow> _recentRows = [];
    private readonly StackPanel _footer = new();
    private readonly Platform.TextInserter _clipboard = new();

    /// What each part shows now; a part is drawn again only when this changes.
    private string? _statusShown, _todayShown, _recentShown, _footerShown;
    private bool _logoLive, _live, _waving;
    /// A language that isn't downloaded yet, waiting for a yes before 600+ MB.
    private SpeechModel? _confirming;

    /// The card itself, for measuring.
    public Grid Card { get; } = new() { Width = Metrics.Width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// The card with its shadow.
    public FrameworkElement Root { get; }

    public PanelView(App app, Action close, double room)
    {
        _app = app;
        _close = close;

        _stack.Children.Add(Header());
        _stack.Children.Add(Parts.Separator());
        _stack.Children.Add(_today);
        _stack.Children.Add(Parts.Separator());
        _stack.Children.Add(Parts.SectionTitle(Loc.T("Språk", "Language")));
        var index = 0;
        foreach (var model in SpeechModel.All)
        {
            var row = new LanguageRow(model, ++index, () => Pick(model), () =>
            {
                _confirming = null;
                // Keeps the language you have until the new one is ready.
                _app.DownloadModel(model, thenUse: true);
                Update();
            });
            _languages.Add(row);
            _stack.Children.Add(row.Row);
        }
        _stack.Children.Add(Parts.Separator());
        _stack.Children.Add(Parts.SectionTitle(Loc.T("Senaste", "Recent")));
        _stack.Children.Add(_recent);
        _stack.Children.Add(Parts.Separator());
        _stack.Children.Add(_footer);

        // Nearly solid, so text keeps its contrast whatever is behind the panel.
        var surface = new Border { CornerRadius = new CornerRadius(Metrics.Corner), Background = Parts.Tint("PaperBrush", 0.97) };
        var edge = new Border { CornerRadius = new CornerRadius(Metrics.Corner), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        UI.Bind(edge, Border.BorderBrushProperty, "FieldStrokeBrush");
        Card.Children.Add(surface);
        Card.Children.Add(_stack);
        Card.Children.Add(edge);
        Card.Margin = new Thickness(room);
        // The window's shadow, as the Mac draws under a panel.
        Root = SoftShadow.Around(Card, Metrics.Corner, 14, 6, 0.28);
        Keys();
        Update();
    }

    /// The panel is on screen: the header's wave may move.
    public bool Live
    {
        set
        {
            _live = value;
            if (!value) HoverRow.ClearHover();
            Update();
        }
    }

    /// Closes the panel and opens the window on <paramref name="page"/>.
    private void Go(Page page)
    {
        _close();
        _app.ShowMain(page);
    }

    /// Ctrl+1 / Ctrl+2 switch language, Ctrl+, opens Settings, Ctrl+Q quits — as the rows show.
    private void Keys()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        for (var i = 0; i < SpeechModel.All.Count; i++)
        {
            var model = SpeechModel.All[i];
            Add(VirtualKey.Number1 + i, VirtualKeyModifiers.Control, () => Pick(model));
        }
        Add((VirtualKey)0xBC /* comma */, VirtualKeyModifiers.Control, () => Go(Page.Settings));
        Add(VirtualKey.Q, VirtualKeyModifiers.Control, _app.Quit);
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, _close);
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void Pick(SpeechModel model)
    {
        if (_app.IsInstalled(model))
        {
            _confirming = null;
            _app.SetEngine(model);
        }
        else if (_app.Speech.DownloadProgress(model) is null) _confirming = _confirming == model ? null : model;
        Update();
    }

    public void Update()
    {
        UpdateHeader();
        UpdateToday();
        foreach (var (row, model) in _languages.Zip(SpeechModel.All))
            row.Update(_app.Engine == model, _app.IsInstalled(model), _app.Speech.DownloadProgress(model), _confirming == model);
        UpdateRecent();
        UpdateFooter();
    }

    // ---- Header ---------------------------------------------------------------------

    private Grid Header()
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(Metrics.Inset, 8, Metrics.Inset, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = SoftShadow.Around(_logo, 36 * 0.225, 6, 2, 0.25);
        logo.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(logo);
        var name = UI.VStack(5, UI.Text("Mindtalk", 15, FontWeights.SemiBold), _status);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        _wave.Color = UI.Color("InkBrush");
        Grid.SetColumn(_wave, 2);
        grid.Children.Add(_wave);
        return grid;
    }

    private void UpdateHeader()
    {
        var state = _app.Dictation.State;
        // The logo comes alive (its wave following your voice) only while recording.
        var recording = state.Phase == Phase.Recording;
        var live = recording && _live;
        if (_logo.Children.Count == 0 || live != _logoLive)
        {
            _logoLive = live;
            _logo.Children.Clear();
            _logo.Children.Add(live ? new LogoTile(36, animated: true, levels: () => _app.Dictation.Levels()) : new LogoTile(36));
        }
        _wave.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Waving(live);

        var key = StatusKey(state);
        if (key == _statusShown) return;
        _statusShown = key;
        _status.Children.Clear();
        _status.Children.Add(Status(state));
    }

    private void Waving(bool on)
    {
        if (on == _waving) return;
        _waving = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e) => _wave.SetLevels(_app.Dictation.Levels(14));

    private string StatusKey(DictationState state) => state.Phase switch
    {
        Phase.Recording => $"recording {state.HandsFree}",
        Phase.Transcribing => "typing",
        _ => _app.Model switch
        {
            ModelState.Downloading => $"downloading {_app.Engine.Id} {(int)(_app.EngineProgress * 100)}",
            ModelState.Loading => "starting",
            _ => _app.IsReady ? $"ready {_app.Settings.Mode} {_app.Settings.Hotkey.Chip}"
               : _app.Issue is { } issue ? $"issue {_app.IssueTitle(issue)}" : "",
        },
    };

    /// "Hold [Alt →] to dictate", listening or typing, a download or start, or what's
    /// missing — which a click fixes.
    private UIElement Status(DictationState state)
    {
        static TextBlock Line(string text) => UI.Text(text, 12.5, brush: "MutedBrush");
        switch (state.Phase)
        {
            case Phase.Recording:
                return UI.HStack(5, Parts.Dot("RecordingBrush"),
                    Line(state.HandsFree ? Loc.T("Lyssnar – låst", "Listening – locked") : Loc.T("Lyssnar…", "Listening…")));
            case Phase.Transcribing:
                return Line(Loc.T("Skriver…", "Typing…"));
        }
        switch (_app.Model)
        {
            case ModelState.Downloading:
                var percent = (int)(_app.EngineProgress * 100);
                return Line(Loc.T($"Laddar ned {_app.Engine.Title} · {percent} %", $"Downloading {_app.Engine.Title} · {percent} %"));
            case ModelState.Loading:
                return Line(Loc.T("Startar…", "Starting…"));
        }
        if (_app.IsReady)
        {
            var hotkey = _app.Settings.Hotkey;
            var verb = _app.Settings.Mode == DictationMode.Toggle ? Loc.T("Tryck", "Press") : Loc.T("Håll", "Hold");
            var chip = UI.KeyChip(hotkey.Chip);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.Margin = new Thickness(1, 0, 1, 0);
            // A combination is too long to sit in the sentence: it goes under it.
            return hotkey.ModifierKeys.Count > 1
                ? UI.VStack(4, UI.HStack(5, Line(verb), Line(Loc.T("för att diktera", "to dictate"))), chip)
                : UI.HStack(5, Line(verb), chip, Line(Loc.T("för att diktera", "to dictate")));
        }
        if (_app.Issue is { } issue)
        {
            var title = UI.Text(_app.IssueTitle(issue), 12.5);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.MaxWidth = Metrics.Width - 2 * Metrics.Inset - 36 - 12 - 6 - 9 - 2 * 5;
            var chevron = UI.Icon(Icons.ChevronRight, 9, "MutedBrush");
            chevron.VerticalAlignment = VerticalAlignment.Center;
            var fix = UI.PlainButton(UI.HStack(5, Parts.Dot("OrangeBrush"), title, chevron), () =>
            {
                _close();
                _app.Fix(issue);
            }, App.IssueWhy(issue));
            fix.HorizontalAlignment = HorizontalAlignment.Left;
            return fix;
        }
        return new Grid();
    }

    // ---- Today ----------------------------------------------------------------------

    private void UpdateToday()
    {
        var stats = _app.Stats;
        var week = stats.Week;
        var key = $"{stats.Today.Words} {string.Join(",", week.Select(d => $"{d.Date:yyyyMMdd}:{d.Stat.Words}"))}";
        if (key == _todayShown) return;
        _todayShown = key;
        _today.Children.Clear();

        var weekWords = stats.WeekWords;
        FrameworkElement figures = weekWords == 0
            ? UI.Text(Loc.T("Inga ord den här veckan än", "No words this week yet"), 12.5, brush: "MutedBrush")
            : UI.HStack(14, Figure(stats.Today.Words, Loc.T("ord i dag", "words today")), Figure(weekWords, Loc.T("i veckan", "this week")));
        figures.HorizontalAlignment = HorizontalAlignment.Left;
        figures.VerticalAlignment = VerticalAlignment.Center;
        _today.Children.Add(figures);
        // The bars step aside when big numbers or a long language need the room.
        figures.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (figures.DesiredSize.Width + 8 + WeekBars.Width <= Metrics.Width - 2 * Metrics.Inset)
        {
            var bars = WeekBars.Make(week);
            bars.HorizontalAlignment = HorizontalAlignment.Right;
            _today.Children.Add(bars);
        }
        var saved = week.Sum(d => d.Stat.SavedSeconds);
        ToolTipService.SetToolTip(_today, saved >= 1
            ? Loc.T($"≈ {StatsStore.Duration(saved)} sparad tid den här veckan", $"≈ {StatsStore.Duration(saved)} saved this week")
            : null);
    }

    /// "640 words today": the number in serif, the words quiet beside it, on one baseline.
    private static TextBlock Figure(int value, string label)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        var number = new Run { Text = StatsStore.Number(value), FontFamily = UI.Serif, FontSize = 20, Foreground = UI.Brush("PrimaryBrush") };
        // Sitka's figures are old-style by default; New York's are lining (and monospaced).
        Typography.SetNumeralStyle(number, FontNumeralStyle.Lining);
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        text.Inlines.Add(number);
        text.Inlines.Add(new Run { Text = " " + label, FontSize = 12.5, Foreground = UI.Brush("MutedBrush") });
        return text;
    }

    // ---- Recent ---------------------------------------------------------------------

    private void UpdateRecent()
    {
        var entries = _app.Recent.Entries;
        var key = string.Join(",", entries.Take(3).Select(e => e.Id)) + (entries.Count > 3 ? " more" : "");
        if (key == _recentShown)
        {
            var now = DateTime.Now;
            foreach (var row in _recentRows) row.Refresh(now);
            return;
        }
        _recentShown = key;
        _recent.Children.Clear();
        _recentRows.Clear();
        if (entries.Count == 0)
        {
            var empty = UI.Text(Loc.T("Det du dikterar hamnar här.", "What you dictate shows up here."), 14, brush: "MutedBrush");
            empty.Margin = new Thickness(Metrics.Inset, 0, Metrics.Inset, 0);
            _recent.Children.Add(new Grid { Height = 30, Children = { empty } });
            return;
        }
        foreach (var entry in entries.Take(3))
        {
            var row = new RecentRow(entry, _clipboard.Copy);
            _recentRows.Add(row);
            _recent.Children.Add(row.Row);
        }
        if (entries.Count > 3) _recent.Children.Add(Parts.Row(Loc.T("Visa alla…", "Show All…"), () => Go(Page.Recent)));
    }

    // ---- The app's rows ---------------------------------------------------------------

    private void UpdateFooter()
    {
        var version = _app.Updates.Available;
        if (_footerShown == $"{version}" && _footer.Children.Count > 0) return;
        _footerShown = $"{version}";
        _footer.Children.Clear();
        void CheckForUpdates()
        {
            _close();
            _app.Updates.CheckNow();
        }
        if (version is not null)
        {
            _footer.Children.Add(Parts.Row(Loc.T($"Ny version {version} – installera…", $"New version {version} – install…"), CheckForUpdates));
            _footer.Children.Add(Parts.Separator());
        }
        _footer.Children.Add(Parts.Row(Loc.T("Öppna Mindtalk", "Open Mindtalk"), () => Go(Page.Dictation)));
        _footer.Children.Add(Parts.Row(Loc.T("Inställningar…", "Settings…"), () => Go(Page.Settings), "Ctrl+,"));
        // With a new version waiting, the row at the top already offers it.
        if (version is null) _footer.Children.Add(Parts.Row(Loc.T("Sök efter uppdateringar…", "Check for Updates…"), CheckForUpdates));
        _footer.Children.Add(Parts.Separator());
        _footer.Children.Add(Parts.Row(Loc.T("Avsluta Mindtalk", "Quit Mindtalk"), _app.Quit, "Ctrl+Q"));
    }
}
