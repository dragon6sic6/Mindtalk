using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.App.Pages;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// The meeting being recorded: the clock, the orb listening, who said what as it's said (the
/// words still being written in grey), a note field, the marks, pause and end. The round of
/// names, "Is this Anna?" and "Someone new is talking" appear above the text as they come.
/// After End: finishing, then summarizing — then the meeting's own page.
/// </summary>
internal sealed partial class LivePage : PageView
{
    private readonly MainWindow _main;
    private readonly MeetingHub _hub;
    private readonly TextBlock _status = UI.Text("", 13, FontWeights.SemiBold);
    private readonly TextBlock _title = new() { FontFamily = UI.Serif, FontSize = 41, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _listening = UI.Text("", 13, brush: "MutedBrush");
    private readonly Border _control = new();
    private readonly StackPanel _banners = new() { Spacing = 12 };
    private readonly StackPanel _transcript = new() { Spacing = 4 };
    private readonly Border _partial = new() { Padding = new Thickness(0, 6, 0, 0) };
    private readonly Grid _noteBar;
    private readonly TextBox _note;
    private readonly Button _pause;
    private Orb? _orb;
    private Grid? _finish;
    private readonly HashSet<string> _dismissed = [];
    private string? _controlKey, _bannerKey, _speakersKey;
    private int _segments, _notes;
    private bool _following;

    public LivePage(App app, MainWindow main, MeetingHub hub) : base(app)
    {
        _main = main;
        _hub = hub;
        Column.Spacing = 0;
        UI.Bind(_title, TextBlock.ForegroundProperty, "PrimaryBrush");
        Parts.Tabular(_status);

        var top = new Grid { Margin = new Thickness(0, -16, 0, 0) };
        top.Children.Add(Parts.Pill(Loc.T("Möten", "Meetings"), Icons.ChevronLeft, main.Pop));
        _pause = Parts.Pill(Loc.T("Pausa", "Pause"), Icons.Pause, hub.TogglePause);
        ToolTipService.SetToolTip(_pause, Loc.T("Pausa eller fortsätt (Ctrl+Skift+P)", "Pause or resume (Ctrl+Shift+P)"));
        var more = Parts.Circle(Icons.More, () => { }, Loc.T("Mer", "More"), 32);
        more.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
            var discard = new MenuFlyoutItem { Text = Loc.T("Kasta mötet…", "Discard Meeting…"), Icon = new FontIcon { Glyph = Icons.Delete } };
            discard.Click += (_, _) => hub.Discard();
            menu.Items.Add(discard);
            menu.ShowAt(more);
        };
        var actions = UI.HStack(10, _pause, more);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(actions);
        Add(top, 0);

        var statusRow = UI.HStack(8, Parts.RecordingDot(9), _status);
        statusRow.Margin = new Thickness(0, 16, 0, 0);
        Add(statusRow, 0);
        _title.Margin = new Thickness(0, 6, 0, 0);
        Add(_title, 0);
        _listening.Margin = new Thickness(0, 8, 0, 0);
        Add(_listening, 1);
        _control.Margin = new Thickness(0, 22, 0, 0);
        Add(_control, 1);
        _banners.Margin = new Thickness(0, 14, 0, 0);
        Add(_banners, 2);
        var caption = Parts.Caption(Loc.T("Det som sägs", "What's being said"));
        caption.Margin = new Thickness(0, 28, 0, 12);
        Add(caption, 2);
        Add(_transcript, 3);
        Column.Children.Add(_partial);
        Column.Children.Add(Parts.Spacer(110));

        // Type a note and press Return: pinned to that moment.
        _noteBar = new Grid { Height = 46, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
        var face = new Border { CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), Padding = new Thickness(16, 0, 8, 0) };
        UI.Bind(face, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(face, Border.BorderBrushProperty, "FieldStrokeBrush");
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UI.Icon(Icons.Pin, 13, "MutedBrush"));
        _note = new TextBox
        {
            PlaceholderText = Loc.T("Skriv en anteckning och tryck Retur", "Type a note and press Return"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14.5,
        };
        _note.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _note.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(_note, _note.PlaceholderText);
        _note.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            var text = _note.Text.Trim();
            if (text.Length == 0 || _hub.Live is not { State: MeetingState.Recording or MeetingState.Paused } live) return;
            live.AddNote(text);
            _note.Text = "";
        };
        Grid.SetColumn(_note, 1);
        row.Children.Add(_note);
        var enter = UI.Text("↵", 14, brush: "FaintBrush");
        Grid.SetColumn(enter, 2);
        row.Children.Add(enter);
        face.Child = row;
        _noteBar.Children.Add(SoftShadow.Around(face, 23, 14, 3, UI.Dark ? 0.5 : 0.10));
        Children.Add(_noteBar);
        SizeChanged += (_, e) => _noteBar.Width = Math.Max(300, Math.Min(e.NewSize.Width > 1200 ? 860 : 720, e.NewSize.Width - 96));

        // Following the newest line once you've scrolled down to it — not while you read
        // further up, and not before (the clock and the marks stay in view).
        Scroll.ViewChanged += (_, _) => _following = Scroll.VerticalOffset > 0 && Scroll.VerticalOffset >= Scroll.ScrollableHeight - 80;

        hub.LiveChanged += Update;
        hub.Frame += OnFrame;
        hub.Marked += Flash;
        Unloaded += (_, _) => { hub.LiveChanged -= Update; hub.Frame -= OnFrame; hub.Marked -= Flash; };
        Loaded += (_, _) => Update();
    }

    public void FocusNote() => _note.Focus(FocusState.Keyboard);

    private void OnFrame()
    {
        if (_hub.Live is not { } live) return;
        _status.Text = MeetingsPage.LiveStatus(live);
        if (_orb is not null) _orb.Level = live.State == MeetingState.Recording ? live.Level : 0;
        if (_finish is not null && live.State is MeetingState.Finishing or MeetingState.Summarizing)
        {
            var bar = Parts.Progress(live.FinishProgress > 0 ? live.FinishProgress : null);
            _finish.Children.Clear();
            _finish.Children.Add(bar);
        }
    }

    private void Update()
    {
        if (_hub.Live is not { } live) return;
        var meeting = live.Meeting;
        _status.Text = MeetingsPage.LiveStatus(live);
        _title.Text = Format.Title(meeting);
        var listening = Loc.T($"Lyssnar på {Format.Language(string.IsNullOrEmpty(meeting.Language) ? App.Engine.Id : meeting.Language)}",
                              $"Listening in {Format.Language(string.IsNullOrEmpty(meeting.Language) ? App.Engine.Id : meeting.Language)}");
        if (live.HearsCall) listening += Loc.T(" · hör samtalet", " · hears the call");
        else if (meeting.Source == MeetingSource.Call) listening += Loc.T(" · hör bara mikrofonen", " · hears the microphone only");
        _listening.Text = listening;
        var recording = live.State is MeetingState.Recording or MeetingState.Paused;
        _pause.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        var pauseLabel = (StackPanel)((Border)_pause.Content).Child;
        ((FontIcon)pauseLabel.Children[0]).Glyph = live.State == MeetingState.Paused ? Icons.Play : Icons.Pause;
        ((TextBlock)pauseLabel.Children[1]).Text = live.State == MeetingState.Paused ? Loc.T("Fortsätt", "Resume") : Loc.T("Pausa", "Pause");
        _noteBar.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;

        var controlKey = $"{live.State is MeetingState.Finishing or MeetingState.Summarizing}|{live.State == MeetingState.Summarizing}";
        if (controlKey != _controlKey)
        {
            _controlKey = controlKey;
            _control.Child = live.State is MeetingState.Finishing or MeetingState.Summarizing ? Finishing(live) : Controls(live);
        }
        UpdateBanners(live);
        UpdateTranscript(live);
    }

    // ---- The control card ------------------------------------------------------------------

    private readonly Dictionary<MarkKind, Border> _markFaces = [];

    private Border Controls(ILiveMeeting live)
    {
        _finish = null;
        var grid = new Grid { ColumnSpacing = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _orb = new Orb(92) { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(_orb);

        var right = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(UI.Text(Loc.T("Markera det som sägs nu – eller tryck Ctrl+Skift+1, 2, 3 i vilken app som helst",
                                         "Mark what's being said – or press Ctrl+Shift+1, 2, 3 in any app"), 13, brush: "MutedBrush", wrap: true));
        var marks = new FlowPanel { Spacing = 8 };
        _markFaces.Clear();
        foreach (var kind in new[] { MarkKind.Decision, MarkKind.Todo, MarkKind.Important })
        {
            var button = Parts.SoftPill(Parts.MarkGlyph(kind), Parts.MarkTitle(kind), () => _hub.Mark(kind),
                $"{Parts.MarkTitle(kind)} ({Parts.MarkShortcut(kind)})");
            var face = (Border)button.Content;
            UI.Bind(face, Border.BackgroundProperty, UI.Dark ? "ChipBrush" : "ControlBrush");
            ((StackPanel)face.Child).Children.Add(Parts.KeyChip(string.Join(" ", Parts.MarkShortcut(kind).Parts.Skip(2))));
            _markFaces[kind] = face;
            marks.Children.Add(button);
        }
        right.Children.Add(marks);
        var end = Parts.SoftPill(Icons.Stop, Loc.T("Avsluta mötet", "End Meeting"), () => _ = _hub.End(), ink: true, height: 36);
        end.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(end);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var card = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(24, 20, 24, 20), Child = grid };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    /// A mark set (here or with Ctrl+Shift+1/2/3): its button flashes.
    private async void Flash(MarkKind kind)
    {
        if (!_markFaces.TryGetValue(kind, out var face)) return;
        UI.Bind(face, Border.BackgroundProperty, "AccentTintBrush");
        await Task.Delay(500);
        UI.Bind(face, Border.BackgroundProperty, UI.Dark ? "ChipBrush" : "ControlBrush");
    }

    private Border Finishing(ILiveMeeting live)
    {
        _orb = null;
        var summarizing = live.State == MeetingState.Summarizing;
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Parts.Serif(summarizing ? Loc.T("Skriver sammanfattningen…", "Writing the summary…")
                                                   : Loc.T("Gör klart mötet…", "Finishing the meeting…"), 22));
        _finish = new Grid { MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        _finish.Children.Add(Parts.Progress(live.FinishProgress > 0 ? live.FinishProgress : null));
        stack.Children.Add(_finish);
        stack.Children.Add(UI.Muted(summarizing
            ? Loc.T($"{_hub.WriterName} skriver vad ni pratade om, beslut, att göra och vad som är värt att komma ihåg.",
                    $"{_hub.WriterName} writes what you talked about, decisions, to do and what's worth remembering.")
            : Loc.T("Hela inspelningen lyssnas igenom igen med mer sammanhang, och namnen följer med.",
                    "The whole recording is listened through again with more context, and the names carry over."), 13.5));
        var card = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(24, 20, 24, 22), Child = stack };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    // ---- Round of names, "Is this Anna?", someone new ----------------------------------------

    private void UpdateBanners(ILiveMeeting live)
    {
        var speakers = Snapshot(live.Meeting.Speakers);
        var segments = Snapshot(live.Meeting.Segments);
        var recording = live.State is MeetingState.Recording or MeetingState.Paused;
        var named = speakers.Where(s => s.Name is not null && !s.IsBackground).Select(s => s.Name!).ToList();
        var newcomer = recording && !live.InRoundOfNames && live.Suggestion is null
            ? speakers.FirstOrDefault(s => s.Name is null && !s.IsBackground && !_dismissed.Contains(s.Id) && segments.Any(x => x.SpeakerId == s.Id))
            : null;
        var key = $"{recording}|{live.InRoundOfNames}|{string.Join(",", named)}|{live.Suggestion?.SpeakerId}:{live.Suggestion?.Name}|{newcomer?.Id}";
        if (key == _bannerKey) return;
        _bannerKey = key;
        _banners.Children.Clear();
        if (!recording) return;
        if (live.InRoundOfNames) _banners.Children.Add(RoundOfNames(live, named));
        if (live.Suggestion is { } suggestion) _banners.Children.Add(Suggestion(live, suggestion));
        if (newcomer is not null) _banners.Children.Add(Newcomer(live, newcomer));
    }

    private Border RoundOfNames(ILiveMeeting live, List<string> named)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Parts.Caption(Loc.T("Namnrunda", "Round of names")));
        stack.Children.Add(Parts.Serif(Loc.T("Alla säger: ”Hej, jag heter …”", "Everyone says: “Hi, my name is …”"), 25));
        stack.Children.Add(UI.Muted(Loc.T("Gå laget runt. Varje namn hamnar på rösten som sa det.", "Go around the room. Each name lands on the voice that said it."), 13.5));
        var heard = new FlowPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (named.Count == 0) heard.Children.Add(UI.HStack(8, new ProgressRing { Width = 14, Height = 14, IsActive = true }, UI.Text(Loc.T("Lyssnar efter namn…", "Listening for names…"), 13, brush: "MutedBrush")));
        foreach (var name in named)
        {
            var chip = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(12, 5, 13, 6) };
            UI.Bind(chip, Border.BackgroundProperty, UI.Dark ? "ChipBrush" : "ControlBrush");
            chip.Child = UI.HStack(6, UI.Icon(Icons.Check, 11, "GoodBrush"), UI.Text(name, 14, FontWeights.SemiBold));
            Motion.Stagger(chip, 0);
            heard.Children.Add(chip);
        }
        stack.Children.Add(heard);
        var done = UI.InkButton(Loc.T("Klar", "Done"), live.EndRoundOfNames);
        done.HorizontalAlignment = HorizontalAlignment.Left;
        done.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(done);
        var card = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(24, 20, 24, 22), Child = stack, BorderThickness = new Thickness(1.5) };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        UI.Bind(card, Border.BorderBrushProperty, "InkBrush");
        return card;
    }

    private Border Suggestion(ILiveMeeting live, VoiceSuggestion suggestion)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Avatar(Icons.Ear));
        var text = UI.VStack(2,
            Parts.Rich(15, (Loc.T("Är det här ", "Is this "), false), (suggestion.Name, true), ("?", false)),
            UI.Text(Loc.T("Rösten liknar en du har sparat.", "The voice sounds like one you've remembered."), 12.5, brush: "MutedBrush"));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = UI.HStack(8, UI.SoftButton(Loc.T("Nej", "No"), () => live.AcceptSuggestion(false)), UI.InkButton(Loc.T("Ja", "Yes"), () => live.AcceptSuggestion(true)));
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return Banner(grid);
    }

    /// "Someone new is talking. Who is it?" — with the name field, never in the way.
    private Border Newcomer(ILiveMeeting live, Speaker speaker)
    {
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Avatar(Icons.People));
        var lastWords = Snapshot(live.Meeting.Segments).LastOrDefault(s => s.SpeakerId == speaker.Id)?.Text;
        var text = UI.VStack(2,
            UI.Text(Loc.T("Någon ny pratar. Vem är det?", "Someone new is talking. Who is it?"), 15, FontWeights.SemiBold),
            UI.Text(lastWords is null ? speaker.DisplayName : $"{speaker.DisplayName}: ”{lastWords}”", 12.5, brush: "MutedBrush"));
        ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var later = UI.SoftButton(Loc.T("Inte nu", "Not Now"), () => { _dismissed.Add(speaker.Id); _bannerKey = null; Update(); });
        Grid.SetColumn(later, 2);
        grid.Children.Add(later);
        var field = new NameField(NameField.Known(App), null, App.Settings.RememberVoices, 260, focus: false) { HorizontalAlignment = HorizontalAlignment.Left };
        field.Saved += (name, remember) =>
        {
            if (name is null) return;
            live.NameSpeaker(speaker.Id, name, remember);
            _dismissed.Add(speaker.Id);
        };
        field.Cancelled += () => { _dismissed.Add(speaker.Id); _bannerKey = null; Update(); };
        Grid.SetRow(field, 1);
        Grid.SetColumn(field, 1);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(field);
        return Banner(grid);
    }

    private static Grid Avatar(string glyph)
    {
        var avatar = new Grid { Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Top };
        var disc = new Microsoft.UI.Xaml.Shapes.Ellipse();
        UI.Bind(disc, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "ChipBrush");
        avatar.Children.Add(disc);
        avatar.Children.Add(UI.Icon(glyph, 15, "MutedBrush"));
        return avatar;
    }

    private static Border Banner(UIElement content)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 14, 18, 14), Child = content, BorderThickness = new Thickness(1) };
        UI.Bind(card, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(card, Border.BorderBrushProperty, "FieldStrokeBrush");
        Motion.Stagger(card, 0);
        return card;
    }

    // ---- The transcript ----------------------------------------------------------------------

    private void UpdateTranscript(ILiveMeeting live)
    {
        var meeting = live.Meeting;
        var speakers = Snapshot(meeting.Speakers);
        var segments = Snapshot(meeting.Segments);
        var notes = Snapshot(meeting.Notes).Select(n => (n.Time, Glyph: Icons.Pin, n.Text))
            .Concat(Snapshot(meeting.Marks).Select(m => (m.Time, Glyph: Parts.MarkGlyph(m.Kind), Text: Parts.MarkTitle(m.Kind))))
            .OrderBy(x => x.Time).ToList();
        var speakersKey = string.Join(",", speakers.Select(s => $"{s.Id}={s.Name}/{s.IsBackground}"));
        // Names changed, or the text was rewritten: everything again. Otherwise only what's new.
        if (speakersKey != _speakersKey || segments.Count < _segments || notes.Count < _notes)
        {
            _speakersKey = speakersKey;
            _transcript.Children.Clear();
            _segments = 0;
            _notes = 0;
        }
        if (segments.Count != _segments || notes.Count != _notes)
        {
            // Rebuilt from the last turn, so a turn that grew shows its new words.
            var turns = Format.Turns(segments);
            _transcript.Children.Clear();
            var next = 0;
            foreach (var turn in turns)
            {
                while (next < notes.Count && notes[next].Time <= turn.Start) _transcript.Children.Add(NoteLine(notes[next++]));
                _transcript.Children.Add(Turn(live, speakers, turn));
            }
            while (next < notes.Count) _transcript.Children.Add(NoteLine(notes[next++]));
            _segments = segments.Count;
            _notes = notes.Count;
            if (segments.Count == 0)
                _transcript.Children.Add(UI.Muted(live.State is MeetingState.Recording or MeetingState.Paused
                    ? Loc.T("Texten dyker upp här, ungefär en sekund efter rösten.", "The text appears here, about a second behind the voice.")
                    : Loc.T("Ingenting sades.", "Nothing was said."), 14));
        }

        // The words being written right now, in grey.
        if (live.Partial is { Length: > 0 } partial && live.State == MeetingState.Recording)
        {
            var who = live.PartialSpeakerId is { } id ? speakers.FirstOrDefault(s => s.Id == id) : null;
            var text = new TextBlock { Text = partial, FontSize = 14.5, LineHeight = 23, TextWrapping = TextWrapping.Wrap, FontStyle = Windows.UI.Text.FontStyle.Normal };
            UI.Bind(text, TextBlock.ForegroundProperty, "FaintBrush");
            _partial.Child = who is null ? text : UI.VStack(6, SpeakerName(live, who, faint: true), text);
        }
        else _partial.Child = null;
        if (_following) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Scroll.ChangeView(null, Scroll.ScrollableHeight, null));
    }

    private UIElement Turn(ILiveMeeting live, List<Speaker> speakers, Format.Turn turn)
    {
        var speaker = speakers.FirstOrDefault(s => s.Id == turn.SpeakerId) ?? new Speaker { Id = turn.SpeakerId };
        var head = UI.HStack(10, SpeakerName(live, speaker, faint: false), UI.Text(Meeting.Clock(turn.Start), 12.5, FontWeights.Medium, "MutedBrush"));
        var body = new TextBlock { Text = turn.Text, FontSize = 14.5, LineHeight = 23, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        UI.Bind(body, TextBlock.ForegroundProperty, speaker.IsBackground ? "FaintBrush" : "PrimaryBrush");
        var stack = UI.VStack(6, head, body);
        stack.Margin = new Thickness(0, 4, 0, 10);
        return stack;
    }

    private Button SpeakerName(ILiveMeeting live, Speaker speaker, bool faint)
    {
        var label = UI.Text(speaker.DisplayName + (speaker.IsBackground ? Loc.T(" (bakgrund)", " (background)") : ""), 13, FontWeights.SemiBold,
                            faint || speaker.Name is null || speaker.IsBackground ? "MutedBrush" : "PrimaryBrush");
        var face = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(10, 3, 10, 4), Child = UI.HStack(5, label, UI.Icon(Icons.ChevronDown, 8, "FaintBrush")) };
        UI.Bind(face, Border.BackgroundProperty, "ChipBrush");
        var button = UI.PlainButton(face, () => { }, Loc.T($"Sätt namn på {speaker.DisplayName}", $"Name {speaker.DisplayName}"));
        button.Click += (s, _) => NameField.ShowSpeakerFlyout(App, (FrameworkElement)s, speaker, live: true,
            (name, remember) => live.NameSpeaker(speaker.Id, name, remember),
            background => live.SetBackground(speaker.Id, background));
        return button;
    }

    private static UIElement NoteLine((double Time, string Glyph, string Text) note)
    {
        var row = UI.HStack(8, UI.Icon(note.Glyph, 11, "MutedBrush"), UI.Text(note.Text, 13, FontWeights.SemiBold, "MutedBrush"),
                            UI.Text(Meeting.Clock(note.Time), 12.5, brush: "FaintBrush"));
        row.Margin = new Thickness(0, 2, 0, 12);
        return row;
    }

    /// A copy of a list the engine may be adding to on another thread.
    private static List<T> Snapshot<T>(List<T> list)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return [.. list]; }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
        return [];
    }
}
