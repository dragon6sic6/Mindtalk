using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// The panel beside your work: during a meeting, when you switch to another app, a small
/// card in the corner shows the meeting — the clock, the last lines, the marks, pause and
/// end. It never takes the keyboard, and goes away when you come back to Mindtalk.
/// </summary>
internal sealed class FloatingPanel : FloatingWindow
{
    private readonly MeetingHub _hub;
    private Meeting? _done;
    private TextBlock? _clock;
    private string? _key;

    public FloatingPanel(App app, MeetingHub hub) : base(app, 320)
    {
        _hub = hub;
        hub.Frame += () =>
        {
            if (IsShown && _clock is not null && _hub.Live is { } live) _clock.Text = Meeting.Clock(live.Elapsed.TotalSeconds);
        };
    }

    protected override Corner Where => Corner.TopRight;

    public bool ShowsLive => IsShown && _done is null;

    public void ShowLive()
    {
        _done = null;
        var key = Key();
        if (key != _key) { _key = key; Refill(); }
        ShowCard();
    }

    /// The meeting is done: "The summary is ready", for a while.
    public async void ShowDone(Meeting meeting)
    {
        _done = meeting;
        _key = null;
        Refill();
        ShowCard();
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (ReferenceEquals(_done, meeting)) Hide();
    }

    public override void Hide()
    {
        _key = null;
        base.Hide();
    }

    private string Key()
    {
        if (_hub.Live is not { } live) return "none";
        var segments = live.Meeting.Segments.Count;
        return $"{live.State}|{segments}|{live.Partial}|{live.Suggestion?.Name}|{live.InRoundOfNames}|{live.Meeting.Speakers.Count(s => s.Name is null)}|{(int)(live.FinishProgress * 20)}";
    }

    protected override UIElement Content()
    {
        _clock = null;
        if (_done is { } done) return Done(done);
        if (_hub.Live is not { } live) return new Grid();
        var stack = new StackPanel { Spacing = 10 };

        // ● 12:34  Weekly meeting…                 ⤢
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(live.State == MeetingState.Paused ? UI.Icon(Icons.Pause, 10, "MutedBrush") : Parts.RecordingDot(8));
        _clock = UI.Text(Meeting.Clock(live.Elapsed.TotalSeconds), 13, FontWeights.SemiBold);
        Parts.Tabular(_clock);
        Grid.SetColumn(_clock, 1);
        head.Children.Add(_clock);
        var title = UI.Text(Format.Title(live.Meeting), 13, brush: "MutedBrush");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, 2);
        head.Children.Add(title);
        var open = UI.PlainButton(UI.Icon("", 12, "MutedBrush"), _hub.OpenLive, Loc.T("Visa i Mindtalk", "Show in Mindtalk"));
        Grid.SetColumn(open, 3);
        head.Children.Add(open);
        stack.Children.Add(head);

        if (live.State is MeetingState.Finishing or MeetingState.Summarizing)
        {
            stack.Children.Add(UI.Text(live.State == MeetingState.Summarizing ? Loc.T("Skriver sammanfattningen…", "Writing the summary…")
                                                                             : Loc.T("Gör klart mötet…", "Finishing the meeting…"), 14, FontWeights.SemiBold));
            stack.Children.Add(Parts.Progress(live.FinishProgress > 0 ? live.FinishProgress : null));
            return stack;
        }

        // The last lines, and the words being written.
        var speakers = live.Meeting.Speakers.ToList();
        var segments = live.Meeting.Segments.ToList();
        var lines = new StackPanel { Spacing = 4 };
        foreach (var segment in segments.TakeLast(live.Partial is { Length: > 0 } ? 1 : 2))
        {
            var who = speakers.FirstOrDefault(s => s.Id == segment.SpeakerId)?.DisplayName ?? segment.SpeakerId;
            var line = Parts.Rich(12.5, ($"{who}: ", true), (segment.Text, false));
            line.MaxLines = 2;
            line.TextTrimming = TextTrimming.WordEllipsis;
            lines.Children.Add(line);
        }
        if (live.Partial is { Length: > 0 } partial && live.State == MeetingState.Recording)
        {
            var text = UI.Text(partial, 12.5, brush: "FaintBrush", wrap: true);
            text.MaxLines = 2;
            text.TextTrimming = TextTrimming.WordEllipsis;
            lines.Children.Add(text);
        }
        if (lines.Children.Count == 0) lines.Children.Add(UI.Text(Loc.T("Lyssnar…", "Listening…"), 12.5, brush: "MutedBrush"));
        stack.Children.Add(lines);

        // Is this Anna? / Someone new / round of names: answered here, or named in Mindtalk.
        if (live.Suggestion is { } suggestion)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Parts.Rich(13, (Loc.T("Är det här ", "Is this "), false), (suggestion.Name, true), ("?", false)));
            var answers = UI.HStack(6, Small(Loc.T("Nej", "No"), () => live.AcceptSuggestion(false), false), Small(Loc.T("Ja", "Yes"), () => live.AcceptSuggestion(true), true));
            Grid.SetColumn(answers, 1);
            row.Children.Add(answers);
            stack.Children.Add(row);
        }
        else if (live.InRoundOfNames)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var named = string.Join(", ", speakers.Where(s => s.Name is not null).Select(s => s.Name));
            row.Children.Add(UI.Text(named.Length == 0 ? Loc.T("Namnrunda: ”Hej, jag heter …”", "Round of names: “Hi, my name is …”") : named, 13, FontWeights.SemiBold, wrap: true));
            var finished = Small(Loc.T("Klar", "Done"), live.EndRoundOfNames, true);
            Grid.SetColumn(finished, 1);
            row.Children.Add(finished);
            stack.Children.Add(row);
        }
        else if (speakers.Any(s => s.Name is null && !s.IsBackground && segments.Any(x => x.SpeakerId == s.Id)))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(UI.Text(Loc.T("Någon ny pratar.", "Someone new is talking."), 13, brush: "MutedBrush"));
            var name = Small(Loc.T("Sätt namn…", "Name…"), _hub.OpenLive, false);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            stack.Children.Add(row);
        }

        // ⚑ ☑ !   ⏸        End
        var controls = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = UI.HStack(6);
        foreach (var kind in new[] { MarkKind.Decision, MarkKind.Todo, MarkKind.Important })
            buttons.Children.Add(Parts.Circle(Parts.MarkGlyph(kind), () => _hub.Mark(kind), $"{Parts.MarkTitle(kind)} ({Parts.MarkShortcut(kind)})", 30));
        buttons.Children.Add(Parts.Circle(live.State == MeetingState.Paused ? Icons.Play : Icons.Pause, _hub.TogglePause,
            live.State == MeetingState.Paused ? Loc.T("Fortsätt (Ctrl+Skift+P)", "Resume (Ctrl+Shift+P)") : Loc.T("Pausa (Ctrl+Skift+P)", "Pause (Ctrl+Shift+P)"), 30));
        controls.Children.Add(buttons);
        var end = Parts.SoftPill(null, Loc.T("Avsluta", "End"), () => _ = _hub.End(), Loc.T("Avsluta mötet", "End the meeting"), ink: true, height: 30);
        Grid.SetColumn(end, 2);
        controls.Children.Add(end);
        stack.Children.Add(controls);
        return stack;
    }

    private UIElement Done(Meeting meeting)
    {
        var stack = new StackPanel { Spacing = 10 };
        var head = new Grid();
        head.Children.Add(UI.HStack(8, UI.Icon(Icons.CheckCircleFill, 13, "GoodBrush"), UI.Text(Loc.T("Mötet är klart", "The meeting is done"), 13, FontWeights.SemiBold)));
        var close = UI.PlainButton(UI.Icon(Icons.XMark, 10, "MutedBrush"), Hide, Loc.T("Stäng", "Close"));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(close);
        stack.Children.Add(head);
        var title = Parts.Serif(Format.Title(meeting), 17);
        stack.Children.Add(title);
        var open = Small(Loc.T("Visa sammanfattningen", "Show the Summary"), () => { Hide(); _hub.OpenMeeting(meeting.Id); }, true);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(open);
        return stack;
    }

    private static Button Small(string text, Action action, bool ink) => Parts.SoftPill(null, text, action, ink: ink, height: 28);
}
