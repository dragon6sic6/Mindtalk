using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Stats;
using Mindtalk.Core.Thoughts;

namespace Mindtalk.App.Pages;

/// <summary>
/// Home (2.0): the day and a greeting, today's words with the week and the orb, Say a Thought
/// and Start Meeting, then the latest meetings and thoughts. Anything still missing for
/// dictation (the microphone, the speech model) comes first.
/// </summary>
public sealed partial class HomePage : PageView
{
    private readonly MainWindow _window;
    private readonly TextBlock _caption = Kit.Caption("");
    private readonly TextBlock _greeting = Kit.Title("");
    private readonly SetupCard _setup;
    private readonly TextBlock _todayNumber = new() { FontSize = 82, LineHeight = 84, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    private readonly TextBlock _todayUnit = new() { FontSize = 26, TextWrapping = TextWrapping.Wrap, MaxWidth = 104, LineHeight = 30, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    private readonly TextBlock _saved = UI.Muted("", 15.5);
    private readonly TextBlock _weekCaption = Kit.Caption("", 12.5);
    private readonly HomeWeek _bars = new();
    private readonly Grid _holdLine = new();
    private readonly Grid _meetingButton = new();
    private readonly Grid _meetings = new() { ColumnSpacing = 16 };
    private readonly Grid _thoughts = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Orb _orb = new(132);
    private string? _statsKey, _holdKey, _meetingKey, _meetingsKey, _thoughtsKey, _dayKey;

    public HomePage(App app, MainWindow window) : base(app)
    {
        _window = window;
        Column.Spacing = 28;
        _todayNumber.FontFamily = UI.Serif;
        UI.Bind(_todayNumber, TextBlock.ForegroundProperty, "PrimaryBrush");
        Typography.SetNumeralStyle(_todayNumber, FontNumeralStyle.Lining);
        _todayUnit.FontFamily = UI.Serif;
        UI.Bind(_todayUnit, TextBlock.ForegroundProperty, "MutedBrush");

        Add(UI.VStack(2, _caption, _greeting), 0);
        _setup = new SetupCard(app);
        Add(_setup, 1);
        Add(StatsCard(), 1);
        Add(UI.VStack(14, Kit.SectionHeader(Loc.T("Senaste möten", "Latest meetings"), Loc.T("Alla möten", "All meetings"), () => _window.ShowPage(Page.Meetings)), _meetings), 2, 10);
        Add(UI.VStack(14, Kit.SectionHeader(Loc.T("Tankar", "Thoughts"), Loc.T("Alla tankar", "All thoughts"), () => _window.ShowPage(Page.Thoughts)), _thoughts), 3, 8);
    }

    public override void Refresh()
    {
        var now = DateTime.Now;
        var dayKey = $"{now:yyyyMMddHH}|{App.Settings.UserName}";
        if (dayKey != _dayKey)
        {
            _dayKey = dayKey;
            _caption.Text = now.ToString(Loc.T("dddd d MMMM", "dddd, MMMM d"), Loc.Culture).ToUpper(Loc.Culture);
            _greeting.Text = Greeting(now, App.Settings.UserName);
        }
        _setup.Refresh();
        RefreshStats();
        RefreshButtons();
        RefreshMeetings(now);
        RefreshThoughts(now);
    }

    /// "Good afternoon, Johan" — or just "Good afternoon" without a name.
    public static string Greeting(DateTime now, string? name)
    {
        var greeting = now.Hour switch
        {
            >= 5 and < 12 => Loc.T("God morgon", "Good morning"),
            >= 12 and < 18 => Loc.T("God eftermiddag", "Good afternoon"),
            _ => Loc.T("God kväll", "Good evening"),
        };
        return string.IsNullOrWhiteSpace(name) ? greeting : $"{greeting}, {name.Trim()}";
    }

    // ---- The stats card ----------------------------------------------------------------

    private Border StatsCard()
    {
        // Today: the big number with its unit beside it, and the time saved under it.
        var number = UI.HStack(16, _todayNumber, _todayUnit);
        _todayUnit.VerticalAlignment = VerticalAlignment.Bottom;
        _todayUnit.Margin = new Thickness(0, 0, 0, 6);
        _saved.MaxWidth = 250;
        _saved.HorizontalAlignment = HorizontalAlignment.Left;
        var today = UI.VStack(8, number, _saved);
        today.VerticalAlignment = VerticalAlignment.Center;

        var week = UI.VStack(14, _weekCaption, _bars);
        week.VerticalAlignment = VerticalAlignment.Center;
        _orb.VerticalAlignment = VerticalAlignment.Center;
        _orb.HorizontalAlignment = HorizontalAlignment.Right;

        var top = new Grid { ColumnSpacing = 24 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(today);
        Grid.SetColumn(week, 1);
        top.Children.Add(week);
        Grid.SetColumn(_orb, 3);
        top.Children.Add(_orb);
        // Narrow windows: the orb steps aside for the numbers.
        void Fit()
        {
            if (top.ActualWidth <= 0) return;
            _orb.Visibility = Kit.Show(top.ActualWidth >= 600);
        }
        top.SizeChanged += (_, _) => Fit();
        today.SizeChanged += (_, _) => Fit();
        week.SizeChanged += (_, _) => Fit();

        var line = new Rectangle { Height = 1, Margin = new Thickness(0, 26, 0, 22) };
        UI.Bind(line, Shape.FillProperty, "DividerBrush");

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_holdLine);
        var buttons = UI.HStack(12, Kit.Chip(Loc.T("Säg en tanke", "Say a Thought"), App.SayThought), _meetingButton);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);

        var card = Kit.BigCard(UI.VStack(0, top, line, bottom), new Thickness(32, 30, 32, 26));
        return card;
    }

    private void RefreshStats()
    {
        var stats = App.Stats;
        var today = stats.Today;
        var key = $"{today.Words}|{stats.WeekWords}|{stats.WeekNumber}|{string.Join(",", stats.Week.Select(d => d.Stat.Words))}";
        if (key == _statsKey) return;
        _statsKey = key;
        _todayNumber.Text = StatsStore.Number(today.Words);
        _todayUnit.Text = today.Words == 1 ? Loc.T("ord i dag", "word today") : Loc.T("ord i dag", "words today");
        _saved.Text = today.Words == 0
            ? Loc.T("Håll in tangenten och prata – orden räknas här.", "Hold the key and talk – your words are counted here.")
            : Loc.T($"Ungefär {SavedText(today.SavedSeconds)} skrivande sparat i dag.", $"About {SavedText(today.SavedSeconds)} of typing saved today.");
        _weekCaption.Text = Loc.T($"Vecka {stats.WeekNumber} · {StatsStore.Number(stats.WeekWords)} ord", $"Week {stats.WeekNumber} · {StatsStore.Number(stats.WeekWords)} words").ToUpper(Loc.Culture);
        _bars.Show(stats.Week);
    }

    /// "12 minutes" / "12 minuters" (Swedish genitive before "skrivande"), "2 hours".
    public static string SavedText(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 1) return Loc.T("en stunds", "a moment");
        if (minutes < 90) return minutes == 1 ? Loc.T("en minuts", "a minute") : Loc.T($"{minutes} minuters", $"{minutes} minutes");
        var hours = (int)Math.Round(seconds / 3600);
        return hours == 1 ? Loc.T("en timmes", "an hour") : Loc.T($"{StatsStore.Number(hours)} timmars", $"{StatsStore.Number(hours)} hours");
    }

    private void RefreshButtons()
    {
        var hotkey = App.Settings.Hotkey;
        var holdKey = $"{hotkey}|{App.Settings.Mode}";
        if (holdKey != _holdKey)
        {
            _holdKey = holdKey;
            _holdLine.Children.Clear();
            var verb = App.Settings.Mode == DictationMode.Toggle ? Loc.T("Tryck på", "Press") : Loc.T("Håll in", "Hold");
            var chip = UI.KeyChip(hotkey.Chip);
            var line = UI.HStack(8, UI.Text(verb, 15, brush: "MutedBrush"), chip, UI.Text(Loc.T("för att diktera", "to dictate"), 15, brush: "MutedBrush"));
            _holdLine.Children.Add(line);
        }
        // Start Meeting — or the meeting being recorded, with its time.
        var live = App.MeetingService?.Current is { State: MeetingState.Recording or MeetingState.Paused } current ? current : null;
        var meetingKey = live is null ? "start" : $"live {(int)live.Elapsed.TotalSeconds}";
        if (meetingKey == _meetingKey) return;
        _meetingKey = meetingKey;
        _meetingButton.Children.Clear();
        _meetingButton.Children.Add(live is null
            ? Kit.Ink(Loc.T("Starta möte", "Start Meeting"), App.StartMeeting, Kit.RedDot())
            : Kit.Ink(Loc.T($"Mötet pågår · {Meeting.Clock(live.Elapsed.TotalSeconds)}", $"Meeting · {Meeting.Clock(live.Elapsed.TotalSeconds)}"),
                      () => App.OpenMeeting(live.Meeting.Id, null), Kit.RedDot()));
    }

    // ---- Latest meetings --------------------------------------------------------------

    private void RefreshMeetings(DateTime now)
    {
        var latest = App.Meetings.All.OrderByDescending(m => m.Started).Take(3).ToList();
        var key = string.Join(",", latest.Select(m => $"{m.Id}:{m.Modified.Ticks}")) + now.Date.Ticks;
        if (key == _meetingsKey) return;
        _meetingsKey = key;
        _meetings.Children.Clear();
        _meetings.ColumnDefinitions.Clear();
        if (latest.Count == 0)
        {
            var empty = UI.Muted(Loc.T("Dina möten hamnar här. Starta ett ovan, eller släpp en inspelning på Möten.",
                                       "Your meetings show up here. Start one above, or drop a recording on Meetings."), 15);
            _meetings.Children.Add(Kit.Card(empty, new Thickness(24, 22, 24, 22), 18));
            return;
        }
        for (var i = 0; i < 3; i++)
        {
            _meetings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i >= latest.Count) continue;
            var card = MeetingCard(latest[i], now);
            Grid.SetColumn(card, i);
            _meetings.Children.Add(card);
        }
    }

    /// "Today 9:30 AM · 25 min", "Yesterday 2:00 PM", "Oct 19 10:00 AM".
    public static string MeetingWhen(DateTime started, DateTime now)
    {
        var time = Kit.Time(started);
        if (started.Date == now.Date) return Loc.T($"I dag {time}", $"Today {time}");
        if (started.Date == now.Date.AddDays(-1)) return Loc.T($"I går {time}", $"Yesterday {time}");
        var day = started.Year == now.Year ? started.ToString(Loc.T("d MMM", "MMM d"), Loc.Culture) : started.ToString(Loc.T("d MMM yyyy", "MMM d, yyyy"), Loc.Culture);
        return $"{day} {time}";
    }

    private Button MeetingCard(Meeting meeting, DateTime now)
    {
        var meta = UI.HStack(8, UI.Icon(Icons.People, 15, "MutedBrush"),
            UI.Text($"{MeetingWhen(meeting.Started, now)} · {Meeting.Length(meeting.Duration)}", 13.5, brush: "MutedBrush"));
        var title = UI.Text(string.IsNullOrWhiteSpace(meeting.Title) ? Loc.T("Möte", "Meeting") : meeting.Title, 17, FontWeights.SemiBold, wrap: true);
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var summaryText = meeting.Summary?.Overview is { Length: > 0 } overview ? overview
            : meeting.State is MeetingState.Recording or MeetingState.Paused ? Loc.T("Spelas in nu.", "Recording now.")
            : meeting.State is MeetingState.Finishing or MeetingState.Summarizing ? Loc.T("Sammanfattningen skrivs…", "Writing the summary…")
            : string.Join(" ", meeting.Segments.Take(3).Select(s => s.Text));
        var summary = UI.Text(summaryText, 14.5, brush: "MutedBrush", wrap: true);
        summary.MaxLines = 3;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;

        var talkers = meeting.Talkers.Count();
        var foot = UI.HStack(8, UI.Icon(Icons.People, 15, "MutedBrush"),
            UI.Text(talkers == 1 ? Loc.T("1 talare", "1 speaker") : Loc.T($"{talkers} talare", $"{talkers} speakers"), 14, brush: "MutedBrush"));
        if (meeting.Folder is { } folder)
        {
            var folderIcon = UI.Icon(Icons.Folder, 15, "MutedBrush");
            folderIcon.Margin = new Thickness(14, 0, 0, 0);
            foot.Children.Add(folderIcon);
            foot.Children.Add(UI.Text(folder, 14, brush: "MutedBrush"));
        }
        if (talkers == 0 && meeting.Folder is null) foot.Visibility = Visibility.Collapsed;

        var body = new Grid { RowSpacing = 0, MinHeight = 172, Height = 172 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = UI.VStack(8, meta, title, summary);
        body.Children.Add(top);
        foot.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(foot, 2);
        body.Children.Add(foot);

        var card = Kit.Card(body, new Thickness(22, 20, 22, 20), 18);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        var button = UI.PlainButton(card, () => App.OpenMeeting(meeting.Id, null));
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.CornerRadius = new CornerRadius(18);
        AutomationProperties.SetName(button, meeting.Title);
        Lift(button, card);
        return button;
    }

    /// The card darkens a touch under the pointer.
    private static void Lift(Button button, Border card)
    {
        button.PointerEntered += (_, _) => card.Background = Kit.Tint("ChipBrush", 0.8);
        button.PointerExited += (_, _) => UI.Bind(card, Border.BackgroundProperty, "CardBrush");
    }

    // ---- Thoughts ---------------------------------------------------------------------

    private void RefreshThoughts(DateTime now)
    {
        var latest = App.Thoughts.All.Where(t => !t.Done).OrderByDescending(t => t.Pinned).ThenByDescending(t => t.Created).Take(4).ToList();
        var key = string.Join(",", latest.Select(t => $"{t.Id}:{t.Text.GetHashCode()}:{t.Pinned}")) + now.Date.Ticks;
        if (key == _thoughtsKey) return;
        _thoughtsKey = key;
        _thoughts.Children.Clear();
        _thoughts.ColumnDefinitions.Clear();
        _thoughts.RowDefinitions.Clear();
        if (latest.Count == 0)
        {
            var hint = App.Settings.ThoughtHotkey is { } key2 && Core.Input.ThoughtKeys.Resolve(App.Settings.Hotkey, key2).Hold is { } hold
                ? Loc.T($"Håll in {hold.InlineName} och säg det du tänker på – det sparas här i stället för att skrivas.",
                        $"Hold {hold.InlineName} and say what's on your mind – it's kept here instead of typed.")
                : Loc.T("Det du säger som en tanke sparas här i stället för att skrivas.", "What you say as a thought is kept here instead of typed.");
            _thoughts.Children.Add(Kit.Card(UI.Muted(hint, 15), new Thickness(24, 22, 24, 22), 18));
            return;
        }
        _thoughts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _thoughts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < latest.Count; i++)
        {
            if (i % 2 == 0) _thoughts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = ThoughtCard(latest[i], now);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            _thoughts.Children.Add(card);
        }
    }

    private Button ThoughtCard(Thought thought, DateTime now)
    {
        var text = new TextBlock { Text = thought.Text, FontFamily = UI.Serif, FontSize = 18, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        UI.Bind(text, TextBlock.ForegroundProperty, "PrimaryBrush");
        var when = thought.Created.Date == now.Date ? Kit.Time(thought.Created) : MeetingWhen(thought.Created, now);
        var meta = UI.HStack(6, thought.Pinned ? UI.Icon(Icons.Pin, 12, "MutedBrush") : null, UI.Text(when, 13.5, brush: "MutedBrush"));
        var card = Kit.Card(UI.VStack(10, text, meta), new Thickness(22, 18, 22, 18), 18);
        card.Height = 108;
        var button = UI.PlainButton(card, () => _window.ShowPage(Page.Thoughts));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.CornerRadius = new CornerRadius(18);
        AutomationProperties.SetName(button, thought.Text);
        Lift(button, card);
        return button;
    }
}

/// <summary>
/// Home's week: seven bars, Monday to Sunday — today in ink, the other days grey — over a
/// line, with the day names under it, today's in bold.
/// </summary>
public sealed partial class HomeWeek : StackPanel
{
    private const double BarWidth = 23, Gap = 9, Tallest = 70;
    private readonly Grid _bars = new() { Height = Tallest, ColumnSpacing = Gap };
    private readonly Grid _labels = new() { ColumnSpacing = Gap };

    public HomeWeek()
    {
        Spacing = 0;
        for (var i = 0; i < 7; i++)
        {
            _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth) });
            _labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth) });
        }
        Children.Add(_bars);
        var line = new Rectangle { Height = 1, Margin = new Thickness(-2, 4, -2, 6) };
        UI.Bind(line, Shape.FillProperty, "DividerBrush");
        Children.Add(line);
        Children.Add(_labels);
    }

    public void Show(IReadOnlyList<WeekDay> week)
    {
        _bars.Children.Clear();
        _labels.Children.Clear();
        var peak = Math.Max(1, week.Max(d => d.Stat.Words));
        for (var i = 0; i < week.Count; i++)
        {
            var day = week[i];
            var words = day.Stat.Words;
            var bar = new Border
            {
                Height = words == 0 ? 3 : Math.Max(8, words / (double)peak * Tallest),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = day.IsToday ? UI.Brush("InkBrush") : Kit.Tint("InkBrush", day.IsFuture || words == 0 ? 0.12 : UI.Dark ? 0.32 : 0.36),
            };
            var full = day.Date.ToString(Loc.T("dddd d MMMM", "dddd, MMMM d"), Loc.Culture);
            if (!day.IsFuture) ToolTipService.SetToolTip(bar, Loc.T($"{full}: {StatsStore.Number(words)} ord", $"{full}: {StatsStore.Number(words)} words"));
            Grid.SetColumn(bar, i);
            _bars.Children.Add(bar);

            var name = Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(day.Date.DayOfWeek).TrimEnd('.').ToLower(Loc.Culture);
            var label = UI.Text(name, 13, day.IsToday ? FontWeights.Bold : FontWeights.Normal, day.IsToday ? "PrimaryBrush" : "MutedBrush");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(-8, 0, -8, 0);   // "tors", a bold "mon": wider than the bar
            Grid.SetColumn(label, i);
            _labels.Children.Add(label);
        }
    }
}
