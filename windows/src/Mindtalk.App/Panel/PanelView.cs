using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Stats;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Mindtalk.App.TrayPanel;

/// <summary>
/// What's in the tray panel (2.x, the Mac's menu bar panel): the logo and what Mindtalk is
/// doing, today's and this week's words, Start Meeting, Say a Thought and Paste Last
/// Dictation, the language you speak, then the app's own rows. Built once per appearance and
/// brought up to date in place, so the row under the pointer keeps its hover.
/// </summary>
internal sealed class PanelView
{
    private readonly App _app;
    private readonly Action _close;
    private readonly StackPanel _stack = new() { Padding = new Thickness(0, 8, 0, 8) };

    private readonly Grid _logo = new() { Width = 40, Height = 40 };
    private readonly Grid _status = new() { MinHeight = 22 };
    private readonly Waveform _wave = new() { Width = 46, Height = 16, Gap = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Grid _today = new() { Height = 44, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Margin = new Thickness(Metrics.Inset, 0, Metrics.Inset, 0) };
    private readonly StackPanel _actions = new();
    private readonly StackPanel _language = new() { Margin = new Thickness(Metrics.Inset, 4, Metrics.Inset, 4), Spacing = 8 };
    private readonly StackPanel _footer = new();

    /// What each part shows now; a part is drawn again only when this changes.
    private string? _statusShown, _todayShown, _actionsShown, _languageShown, _footerShown;
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
        _stack.Children.Add(_actions);
        _stack.Children.Add(Parts.Separator());
        _stack.Children.Add(_language);
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
        UpdateActions();
        UpdateLanguage();
        UpdateFooter();
    }

    // ---- Header ---------------------------------------------------------------------

    private Grid Header()
    {
        var grid = new Grid { ColumnSpacing = 14, Margin = new Thickness(Metrics.Inset, 8, Metrics.Inset, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = SoftShadow.Around(_logo, 40 * 0.225, 6, 2, 0.25);
        logo.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(logo);
        var name = UI.VStack(3, UI.Text("Mindtalk", 16, FontWeights.SemiBold), _status);
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
            _logo.Children.Add(live ? new LogoTile(40, animated: true, levels: () => _app.Dictation.Levels()) : new LogoTile(40));
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
        Phase.Recording => $"recording {state.HandsFree} {state.Thought}",
        Phase.Transcribing => $"typing {state.Thought}",
        _ => _app.Model switch
        {
            ModelState.Downloading => $"downloading {_app.Engine.Id} {(int)(_app.EngineProgress * 100)}",
            ModelState.Loading => "starting",
            _ => _app.IsReady ? $"ready {_app.Settings.Mode} {_app.Settings.Hotkey}"
               : _app.Issue is { } issue ? $"issue {_app.IssueTitle(issue)}" : "",
        },
    };

    /// "Hold right Alt to dictate", listening or typing, a download or start, or what's
    /// missing — which a click fixes.
    private UIElement Status(DictationState state)
    {
        static TextBlock Line(string text) => UI.Text(text, 13.5, brush: "MutedBrush");
        switch (state.Phase)
        {
            case Phase.Recording:
                var listening = state.Thought ? Loc.T("Lyssnar på en tanke…", "Listening to a thought…")
                              : state.HandsFree ? Loc.T("Lyssnar – låst", "Listening – locked") : Loc.T("Lyssnar…", "Listening…");
                return UI.HStack(5, Parts.Dot("RecordingBrush"), Line(listening));
            case Phase.Transcribing:
                return Line(state.Thought ? Loc.T("Skriver ned tanken…", "Writing down the thought…") : Loc.T("Skriver…", "Typing…"));
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
            var line = Line(_app.Settings.Mode == DictationMode.Toggle
                ? Loc.T($"Tryck {hotkey.InlineName} för att diktera", $"Press {hotkey.InlineName} to dictate")
                : Loc.T($"Håll {hotkey.InlineName} för att diktera", $"Hold {hotkey.InlineName} to dictate"));
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            return line;
        }
        if (_app.Issue is { } issue)
        {
            var title = UI.Text(_app.IssueTitle(issue), 13);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.MaxWidth = Metrics.Width - 2 * Metrics.Inset - 40 - 14 - 6 - 9 - 2 * 5;
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
            ? UI.Text(Loc.T("Inga ord den här veckan än", "No words this week yet"), 13.5, brush: "MutedBrush")
            : UI.HStack(16, Figure(stats.Today.Words, Loc.T("ord i dag", "words today")), Figure(weekWords, Loc.T("i veckan", "this week")));
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
        var number = new Run { Text = StatsStore.Number(value), FontFamily = UI.Serif, FontSize = 22, Foreground = UI.Brush("PrimaryBrush") };
        // Sitka's figures are old-style by default; New York's are lining (and monospaced).
        Typography.SetNumeralStyle(number, FontNumeralStyle.Lining);
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        text.Inlines.Add(number);
        text.Inlines.Add(new Run { Text = " " + label, FontSize = 13.5, Foreground = UI.Brush("MutedBrush") });
        return text;
    }

    // ---- Meeting, thought, paste ----------------------------------------------------------

    private void UpdateActions()
    {
        var live = _app.MeetingService?.Current is { State: MeetingState.Recording or MeetingState.Paused } current ? current : null;
        var state = _app.Dictation.State;
        var thinking = state.Thought && state.Phase == Phase.Recording;
        var thoughtHint = ThoughtKeys.Hint(_app.Settings.Hotkey, _app.Settings.ThoughtHotkey);
        var key = $"{(live is null ? "" : (int)live.Elapsed.TotalSeconds)}|{thinking}|{thoughtHint}|{_app.Settings.MeetingShortcut}|{_app.Settings.PasteLastShortcut}|{_app.Recent.Latest is null}";
        if (key == _actionsShown) return;
        _actionsShown = key;
        _actions.Children.Clear();

        _actions.Children.Add(live is null
            ? Parts.IconRow(Parts.Dot("RecordingBrush", 9), Loc.T("Starta möte", "Start Meeting"), _app.Settings.MeetingShortcut?.ToString(), () =>
            {
                _close();
                _app.StartMeeting();
            })
            : Parts.IconRow(UI.Icon(Icons.Stop, 12, "RecordingBrush"), Loc.T("Avsluta mötet", "End Meeting"), Meeting.Clock(live.Elapsed.TotalSeconds), _app.EndMeeting));
        _actions.Children.Add(Parts.IconRow(UI.Icon(Icons.Lightbulb, 15, "PrimaryBrush"),
            thinking ? Loc.T("Spara tanken", "Save Thought") : Loc.T("Säg en tanke", "Say a Thought"), thoughtHint, () =>
            {
                _close();
                _app.SayThought();
            }));
        var paste = Parts.IconRow(UI.Icon(Icons.Clipboard, 15, "PrimaryBrush"), Loc.T("Klistra in senaste dikteringen", "Paste Last Dictation"),
            (_app.Settings.PasteLastShortcut ?? Shortcut.PasteLast).ToString(), () =>
            {
                _close();
                // Once the panel is gone, the app you were in has the focus again.
                _app.UI.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
                {
                    await Task.Delay(150);
                    _app.Dictation.PasteLast();
                });
            });
        paste.IsEnabled = _app.Recent.Latest is not null;
        paste.Opacity = paste.IsEnabled ? 1 : 0.45;
        _actions.Children.Add(paste);
    }

    // ---- I speak ------------------------------------------------------------------------

    private static string Speaks(SpeechModel model) => model == SpeechModel.Swedish ? Loc.T("Svenska", "Swedish") : Loc.T("Flera språk", "Several languages");

    private void UpdateLanguage()
    {
        var download = _app.Speech.DownloadProgress(SpeechModel.Multilingual) ?? _app.Speech.DownloadProgress(SpeechModel.Swedish);
        var key = $"{_app.Engine.Id}|{_confirming?.Id}|{string.Join(",", SpeechModel.All.Select(m => $"{_app.IsInstalled(m)}:{(int)((_app.Speech.DownloadProgress(m) ?? -1) * 100)}"))}";
        if (key == _languageShown) return;
        _languageShown = key;
        _language.Children.Clear();

        var shown = _confirming ?? _app.Engine;
        var picker = new PillPicker<SpeechModel>(SpeechModel.All.Select(m => (m, Speaks(m))).ToArray(), shown, Pick);
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UI.Text(Loc.T("Jag pratar", "I speak"), 14));
        Grid.SetColumn(picker, 1);
        row.Children.Add(picker);
        _language.Children.Add(row);

        // A language not downloaded yet: say what it takes, then download on a yes.
        if (_confirming is { } model)
        {
            var title = Loc.T($"Ladda ned {model.SizeText}", $"Download {model.SizeText}");
            var capsule = new Border { Height = 24, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 0, 10, 0), Child = UI.Text(title, 12, FontWeights.SemiBold, "OnInkBrush") };
            UI.Bind(capsule, Border.BackgroundProperty, "InkBrush");
            var button = UI.PlainButton(capsule, () =>
            {
                _confirming = null;
                // Keeps the language you have until the new one is ready.
                _app.DownloadModel(model, thenUse: true);
                Update();
            }, title);
            button.CornerRadius = new CornerRadius(12);
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(UI.Text(Loc.T($"{Speaks(model)} laddas ned en gång.", $"{Speaks(model)} downloads once."), 12.5, brush: "MutedBrush", wrap: true));
            Grid.SetColumn(button, 1);
            line.Children.Add(button);
            _language.Children.Add(line);
        }
        else if (SpeechModel.All.FirstOrDefault(m => _app.Speech.DownloadProgress(m) is not null) is { } loading && download is { } progress)
        {
            _language.Children.Add(UI.HStack(6, new DownloadRing(progress),
                UI.Text(Loc.T($"Laddar ned {Speaks(loading).ToLower(Loc.Culture)} · {(int)(progress * 100)} %", $"Downloading {Speaks(loading).ToLower(Loc.Culture)} · {(int)(progress * 100)} %"), 12.5, brush: "MutedBrush")));
        }
        else _language.Children.Add(UI.Text(Loc.T("Gäller diktering, tankar och möten.", "Applies to dictation, thoughts and meetings."), 12.5, brush: "MutedBrush"));
    }

    // ---- The app's rows ---------------------------------------------------------------

    private void UpdateFooter()
    {
        var version = _app.Updates.Available;
        if (_footerShown == $"{version}" && _footer.Children.Count > 0) return;
        _footerShown = $"{version}";
        _footer.Children.Clear();
        if (version is not null)
        {
            _footer.Children.Add(Parts.Row(Loc.T($"Ny version {version} – installera…", $"New version {version} – install…"), () =>
            {
                _close();
                _app.Updates.CheckNow();
            }));
        }
        _footer.Children.Add(Parts.Row(Loc.T("Öppna Mindtalk", "Open Mindtalk"), () => Go(Page.Home)));
        _footer.Children.Add(Parts.Row(Loc.T("Inställningar…", "Settings…"), () => Go(Page.Settings), "Ctrl+,"));
        _footer.Children.Add(Parts.Row(Loc.T("Avsluta Mindtalk", "Quit Mindtalk"), _app.Quit, "Ctrl+Q"));
    }
}
