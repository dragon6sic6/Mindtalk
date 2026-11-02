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
/// A meeting: its title, when and who, and Summary / Text. The summary reads like notes —
/// an overview, what you talked about, decisions, to do, what's worth remembering — with a
/// time on every point that plays that moment. The text is who said what, with names you
/// can set. Ask about the meeting at the bottom.
/// </summary>
internal sealed partial class MeetingPage : PageView
{
    private enum Tab { Summary, Text }

    private readonly MainWindow _main;
    private readonly MeetingHub _hub;
    private readonly Guid _id;
    private readonly Border _titleHost = new();
    private readonly TextBlock _meta = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _folderLink;
    private readonly TextBlock _folderText = new() { FontSize = 13 };
    private readonly StackPanel _content = new();
    private readonly Parts.AskBar _ask;
    private readonly AudioBar _audio;
    private readonly PillPicker<Tab> _tabs;
    private Tab _tab = Tab.Summary;
    private string? _key;
    private string _find = "";
    private readonly List<(double Start, double End, FrameworkElement Element)> _turnElements = [];
    private TextBox? _findBox;

    public MeetingPage(App app, MainWindow main, MeetingHub hub, Guid id) : base(app)
    {
        _main = main;
        _hub = hub;
        _id = id;
        Column.Spacing = 0;

        // ‹ Meetings · · · Share ⋯
        var top = new Grid();
        top.Children.Add(Parts.Pill(Loc.T("Möten", "Meetings"), Icons.ChevronLeft, main.Pop));
        var actions = UI.HStack(10,
            ShareButton(),
            Parts.Circle(Icons.More, () => { }, Loc.T("Mer", "More"), 32));
        ((Button)actions.Children[1]).Click += (s, _) => MoreMenu().ShowAt((FrameworkElement)s);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(actions);
        top.Margin = new Thickness(0, -16, 0, 0);
        Add(top, 0);

        _titleHost.Margin = new Thickness(0, 14, 0, 0);
        Add(_titleHost, 0);

        // Today 9:30 AM · 25 min · Anna, Erik and Sara · Add to Folder        [Summary | Text]
        var metaRow = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 16 };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UI.Bind(_meta, TextBlock.ForegroundProperty, "MutedBrush");
        UI.Bind(_folderText, TextBlock.ForegroundProperty, "MutedBrush");
        _folderLink = UI.PlainButton(_folderText, () => { });
        _folderLink.Margin = new Thickness(4, 0, 0, 0);
        _folderLink.Click += (_, _) =>
        {
            if (Meeting is not { } m) return;
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            foreach (var item in MeetingActions.FolderItems(App, XamlRoot, m)) menu.Items.Add(item);
            menu.ShowAt(_folderLink);
        };
        metaRow.Children.Add(UI.HStack(0, _meta, _folderLink));
        _tabs = new PillPicker<Tab>([(Tab.Summary, Loc.T("Sammanfattning", "Summary")), (Tab.Text, "Text")], _tab, t => { _tab = t; _key = null; Refresh(); });
        Grid.SetColumn(_tabs, 1);
        metaRow.Children.Add(_tabs);
        Add(metaRow, 1);

        _content.Margin = new Thickness(0, 26, 0, 0);
        Add(_content, 2);
        Column.Children.Add(Parts.Spacer(150));

        // The player and the ask bar, floating at the bottom.
        _audio = new AudioBar();
        _ask = new Parts.AskBar(Loc.T("Fråga om det här mötet", "Ask about this meeting"), null, q => AskSheet.Show(App, _main, _hub, q, null, Meeting));
        var bottom = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
        bottom.Children.Add(_audio);
        bottom.Children.Add(_ask);
        Children.Add(bottom);
        SizeChanged += (_, e) => bottom.Width = Math.Max(300, Math.Min(e.NewSize.Width > 1200 ? 860 : 720, e.NewSize.Width - 96));
        Unloaded += (_, _) => _audio.Close();

        // Ctrl+F: search the text.
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) =>
        {
            e.Handled = true;
            ShowTab(Tab.Text);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _findBox?.Focus(FocusState.Keyboard));
        };
        KeyboardAccelerators.Add(find);
    }

    private void ShowTab(Tab tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        _key = null;
        Refresh();
    }

    /// Opens on the Text tab (the demo's --demo-tab text).
    public void ShowText() => ShowTab(Tab.Text);

    private Meeting? Meeting => App.Meetings.Get(_id);

    public override void Refresh()
    {
        if (Meeting is not { } meeting)
        {
            if (IsLoaded && ReferenceEquals(_main.Pushed, this)) _main.Pop();
            return;
        }
        var working = App.MeetingService?.Working is { } w && w.TryGetValue(_id, out var p) ? p : (double?)null;
        var key = $"{meeting.Modified.Ticks}|{meeting.State}|{(working is null ? -1 : (int)(working * 50))}|{_tab}|{App.Settings.RememberVoices}";
        if (key == _key) return;
        var firstTime = _key is null && _titleHost.Child is null;
        _key = key;
        if (!_renaming) _titleHost.Child = Title(meeting);
        var meta = $"{Format.When(meeting.Started, DateTime.Now)} · {Meeting.Length(meeting.Duration)}";
        if (meeting.Talkers.Any()) meta += $" · {meeting.PeopleLine}";
        _meta.Text = meta + " ·";
        _folderText.Text = meeting.Folder ?? Loc.T("Lägg i mapp", "Add to Folder");
        _tabs.Selection = _tab;
        _audio.Load(App.Meetings.AudioPath(meeting), meeting.Duration);

        _content.Children.Clear();
        if (_tab == Tab.Summary) BuildSummary(meeting, working);
        else BuildText(meeting);
        if (firstTime && _tab == Tab.Text) _findBox?.Focus(FocusState.Programmatic);
    }

    // ---- The title -------------------------------------------------------------------

    private bool _renaming;

    private UIElement Title(Meeting meeting)
    {
        var text = new TextBlock { Text = Format.Title(meeting), FontFamily = UI.Serif, FontSize = 41, TextWrapping = TextWrapping.Wrap, Foreground = UI.Brush("PrimaryBrush") };
        var button = UI.PlainButton(text, () => StartRename(meeting), Loc.T("Klicka för att byta namn", "Click to rename"));
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private void StartRename(Meeting meeting)
    {
        _renaming = true;
        var box = new TextBox { Text = Format.Title(meeting), FontFamily = UI.Serif, FontSize = 36, Padding = new Thickness(6, 0, 6, 2), BorderThickness = new Thickness(0, 0, 0, 1) };
        box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            _renaming = false;
            var name = box.Text.Trim();
            if (save && name.Length > 0 && name != meeting.Title) { meeting.Title = name; meeting.TitleIsAuto = false; App.Meetings.Save(meeting); }
            _titleHost.Child = Title(Meeting ?? meeting);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        _titleHost.Child = box;
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
    }

    // ---- Share and more ------------------------------------------------------------------

    private Button ShareButton()
    {
        var button = Parts.Pill(Loc.T("Dela", "Share"), Icons.Share, () => { });
        button.Click += (s, _) => ShareMenu().ShowAt((FrameworkElement)s);
        return button;
    }

    private MenuFlyout ShareMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        void Item(string text, string glyph, Func<Meeting, Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += async (_, _) => { if (Meeting is { } m) await action(m); };
            menu.Items.Add(item);
        }
        var hasAudio = Meeting is { } meeting && App.Meetings.AudioPath(meeting) is not null;
        Item("PDF…", Icons.Document, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Pdf));
        Item(Loc.T("Word (.docx)…", "Word (.docx)…"), Icons.Document, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Word));
        Item("Markdown…", Icons.Document, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Markdown));
        Item(Loc.T("Ren text…", "Plain Text…"), Icons.Document, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Text));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(Loc.T("Ljud (.m4a)…", "Audio (.m4a)…"), Icons.Volume, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Audio), hasAudio);
        Item(Loc.T("Undertexter (.srt)…", "Subtitles (.srt)…"), Icons.Font, m => MeetingExport.SaveAsync(App, _main, m, MeetingExport.Kind.Subtitles));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(Loc.T("Kopiera sammanfattningen", "Copy Summary"), Icons.Copy, m => { App.Copy(MeetingExport.Markdown(m, withTranscript: false)); return Task.CompletedTask; });
        return menu;
    }

    private MenuFlyout MoreMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        if (Meeting is not { } meeting) return menu;
        var rename = new MenuFlyoutItem { Text = Loc.T("Byt namn…", "Rename…"), Icon = new FontIcon { Glyph = Icons.Edit } };
        rename.Click += (_, _) => StartRename(meeting);
        menu.Items.Add(rename);
        menu.Items.Add(MeetingActions.MoveMenu(App, XamlRoot, meeting));
        var again = new MenuFlyoutItem
        {
            Text = Loc.T("Skriv sammanfattningen igen", "Write the Summary Again"),
            Icon = new FontIcon { Glyph = Icons.Refresh },
            IsEnabled = App.MeetingService is not null && meeting.State is MeetingState.Done or MeetingState.Failed && meeting.Segments.Count > 0,
        };
        again.Click += (_, _) => Resummarize(meeting);
        menu.Items.Add(again);
        menu.Items.Add(new MenuFlyoutSeparator());
        var audio = new MenuFlyoutItem { Text = Loc.T("Ta bort ljudet…", "Delete Audio…"), Icon = new FontIcon { Glyph = Icons.Volume }, IsEnabled = App.Meetings.AudioPath(meeting) is not null };
        audio.Click += async (_, _) =>
        {
            var sure = await MeetingActions.Confirm(XamlRoot, Loc.T("Ta bort ljudet?", "Delete the audio?"),
                Loc.T("Texten, anteckningarna och sammanfattningen finns kvar. Tiderna går inte längre att lyssna på.",
                      "The text, your notes and the summary stay. The times can no longer be listened to."), Loc.T("Ta bort", "Delete"));
            if (!sure) return;
            _audio.Close();
            App.Meetings.DeleteAudio(meeting);
        };
        menu.Items.Add(audio);
        var explorer = new MenuFlyoutItem { Text = Loc.T("Visa i Utforskaren", "Show in Explorer"), Icon = new FontIcon { Glyph = Icons.Folder } };
        explorer.Click += (_, _) => MeetingActions.ShowInExplorer(App, meeting);
        menu.Items.Add(explorer);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = Loc.T("Ta bort mötet…", "Delete Meeting…"), Icon = new FontIcon { Glyph = Icons.Delete } };
        delete.Click += async (_, _) => { _audio.Close(); await MeetingActions.Delete(App, XamlRoot, meeting, _main.Pop); };
        menu.Items.Add(delete);
        return menu;
    }

    private async void Resummarize(Meeting meeting)
    {
        if (App.MeetingService is not { } service) return;
        try { await service.ResummarizeAsync(meeting.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            await MeetingActions.Confirm(XamlRoot, Loc.T("Sammanfattningen kunde inte skrivas", "The summary couldn't be written"), ex.Message, "OK");
        }
    }

    // ---- Playing a moment -------------------------------------------------------------

    /// Plays from a moment — or, without audio, shows it in the text.
    private void Play(double seconds)
    {
        if (_audio.HasAudio) _audio.PlayFrom(seconds);
        else ShowMoment(seconds);
    }

    /// The Text tab, scrolled to the moment, which lights up for a moment.
    public void ShowMoment(double seconds)
    {
        if (_tab != Tab.Text) { _tab = Tab.Text; _key = null; Refresh(); }
        if (_audio.HasAudio) _audio.Seek(seconds);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(250);
            var target = _turnElements.LastOrDefault(t => t.Start <= seconds + 0.5).Element ?? _turnElements.FirstOrDefault().Element;
            if (target is null) return;
            var y = target.TransformToVisual(Scroll.Content as UIElement).TransformPoint(default).Y;
            Scroll.ChangeView(null, Math.Max(0, y - 140), null);
            if (target is Border border)
            {
                UI.Bind(border, Border.BackgroundProperty, "AccentTintBrush");
                await Task.Delay(1600);
                border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        });
    }

    // ---- Summary -----------------------------------------------------------------------

    private void BuildSummary(Meeting meeting, double? working)
    {
        var busy = working is not null || meeting.State is MeetingState.Finishing or MeetingState.Summarizing;
        if (meeting.Summary is not { } summary || busy && meeting.Summary is null)
        {
            _content.Children.Add(NoSummary(meeting, busy, working));
            AddNotes(meeting);
            return;
        }
        if (busy) _content.Children.Add(Busy(meeting, working));

        var overview = Parts.Serif(summary.Overview, 19.5);
        overview.LineHeight = 31;
        overview.MaxWidth = 640;
        overview.HorizontalAlignment = HorizontalAlignment.Left;
        overview.IsTextSelectionEnabled = true;
        _content.Children.Add(overview);

        if (summary.Topics.Count > 0)
        {
            AddCaption(Loc.T("Det här pratade ni om", "What you talked about"), 30);
            foreach (var topic in summary.Topics)
            {
                var heading = Parts.Serif(topic.Title, 17.5);
                heading.Margin = new Thickness(0, 18, 0, 12);
                _content.Children.Add(heading);
                _content.Children.Add(Bullets(topic.Items.Select(i => (i.Text, i.Time))));
            }
        }
        if (summary.Decisions.Count > 0)
        {
            AddCaption(Loc.T("Beslut", "Decisions"), 34);
            _content.Children.Add(Bullets(summary.Decisions.Select(i => (i.Text, i.Time)), Icons.Flag));
        }
        if (summary.Todos.Count > 0)
        {
            AddCaption(Loc.T("Att göra", "To do"), 34);
            _content.Children.Add(Todos(meeting, summary));
        }
        if (summary.Remember.Count > 0)
        {
            AddCaption(Loc.T("Värt att komma ihåg", "Worth remembering"), 34);
            _content.Children.Add(Bullets(summary.Remember.Select(i => (i.Text, i.Time)), Icons.Important));
        }
        AddNotes(meeting);
        AddQuestions(meeting);

        var by = UI.Text(Loc.T($"Sammanfattad av {summary.Writer}", $"Summarized by {summary.Writer}"), 12.5, brush: "FaintBrush");
        by.Margin = new Thickness(0, 30, 0, 0);
        _content.Children.Add(by);
    }

    private void AddCaption(string text, double before)
    {
        var caption = Parts.Caption(text);
        caption.Margin = new Thickness(0, before, 0, before > 30 ? 12 : 0);
        _content.Children.Add(caption);
    }

    private UIElement NoSummary(Meeting meeting, bool busy, double? working)
    {
        if (busy) return Busy(meeting, working);
        var stack = new StackPanel { Spacing = 10 };
        if (meeting.State == MeetingState.Failed)
        {
            stack.Children.Add(Parts.Serif(Loc.T("Något gick fel", "Something went wrong"), 22));
            stack.Children.Add(UI.Muted(meeting.Error ?? Loc.T("Sammanfattningen kunde inte skrivas.", "The summary couldn't be written."), 14));
        }
        else
        {
            stack.Children.Add(Parts.Serif(Loc.T("Ingen sammanfattning än", "No summary yet"), 22));
            stack.Children.Add(UI.Muted(meeting.Segments.Count == 0
                ? Loc.T("Ingenting sades i det här mötet.", "Nothing was said in this meeting.")
                : meeting.Error is { } error ? $"{error} {Loc.T("Texten finns under Text.", "The text is under Text.")}"
                : Loc.T("Texten finns under Text.", "The text is under Text."), 14));
        }
        if (meeting.Segments.Count > 0)
        {
            var write = UI.InkButton(Loc.T("Skriv sammanfattningen", "Write the Summary"), () => Resummarize(meeting));
            write.IsEnabled = App.MeetingService is not null;
            write.HorizontalAlignment = HorizontalAlignment.Left;
            write.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(write);
        }
        return stack;
    }

    private static UIElement Busy(Meeting meeting, double? working)
    {
        var finishing = meeting.State == MeetingState.Finishing || meeting.Source == MeetingSource.Import && meeting.Segments.Count == 0;
        var title = Parts.Serif(finishing
            ? Loc.T("Lyssnar igenom hela mötet…", "Listening through the whole meeting…")
            : Loc.T("Skriver sammanfattningen…", "Writing the summary…"), 22);
        var detail = UI.Muted(finishing
            ? Loc.T("Med mer sammanhang blir texten bättre. Sedan skrivs sammanfattningen.", "With more context, the text gets better. Then the summary is written.")
            : Loc.T("Texten är klar under Text medan du väntar.", "The text is ready under Text while you wait."), 14);
        var bar = Parts.Progress(working is > 0 ? working : null, 320);
        bar.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 26), Children = { title, bar, detail } };
    }

    /// A grey card of points, each with its time on the right.
    private Border Bullets(IEnumerable<(string Text, double? Time)> items, string? glyph = null)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var (text, time) in items)
        {
            if (!first) stack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Margin = new Thickness(22, 0, 22, 0), Fill = UI.Brush("DividerBrush") });
            first = false;
            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(22, 14, 24, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bullet = glyph is null
                ? (FrameworkElement)new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = UI.Brush("FaintBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) }
                : UI.Icon(glyph, 10, "FaintBrush");
            if (glyph is not null) { bullet.VerticalAlignment = VerticalAlignment.Top; bullet.Margin = new Thickness(0, 4, 0, 0); }
            row.Children.Add(bullet);
            var body = UI.Text(text, 14.5, wrap: true);
            body.IsTextSelectionEnabled = true;
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            if (time is { } t)
            {
                var at = Parts.Time(t, Play);
                at.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(at, 2);
                row.Children.Add(at);
            }
            stack.Children.Add(row);
        }
        var card = new Border { CornerRadius = new CornerRadius(16), Child = stack };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    /// To do: tick them off (kept with the meeting), who promised it, when.
    private Border Todos(Meeting meeting, MeetingSummary summary)
    {
        var stack = new StackPanel();
        for (var i = 0; i < summary.Todos.Count; i++)
        {
            var index = i;
            var task = summary.Todos[i];
            if (i > 0) stack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Margin = new Thickness(22, 0, 22, 0), Fill = UI.Brush("DividerBrush") });
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 10, 24, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = task.Done, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, 0, 0) };
            AutomationProperties.SetName(check, task.Text);
            check.Click += (_, _) =>
            {
                if (Meeting?.Summary is not { } s || index >= s.Todos.Count) return;
                s.Todos[index] = s.Todos[index] with { Done = check.IsChecked == true };
                App.Meetings.Save(Meeting!);
            };
            row.Children.Add(check);
            var body = UI.Text(task.Text, 14.5, brush: task.Done ? "MutedBrush" : "PrimaryBrush", wrap: true);
            if (task.Done) body.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            body.Margin = new Thickness(0, 4, 0, 0);
            body.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            if (task.Owner is { } owner)
            {
                var chip = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(9, 2, 10, 3), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
                UI.Bind(chip, Border.BackgroundProperty, "ChipBrush");
                chip.Child = UI.Text(owner, 12, FontWeights.SemiBold);
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }
            if (task.Time is { } t)
            {
                var at = Parts.Time(t, Play);
                at.VerticalAlignment = VerticalAlignment.Top;
                at.Margin = new Thickness(0, 4, 0, 0);
                Grid.SetColumn(at, 3);
                row.Children.Add(at);
            }
            stack.Children.Add(row);
        }
        var card = new Border { CornerRadius = new CornerRadius(16), Child = stack };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    /// Your notes and marks, in time order.
    private void AddNotes(Meeting meeting)
    {
        if (meeting.Notes.Count == 0 && meeting.Marks.Count == 0) return;
        AddCaption(Loc.T("Dina anteckningar och markeringar", "Your notes and marks"), 34);
        var items = meeting.Notes.Select(n => (n.Time, Glyph: Icons.Pin, Text: n.Text))
            .Concat(meeting.Marks.Select(m => (m.Time, Glyph: Parts.MarkGlyph(m.Kind), Text: Parts.MarkTitle(m.Kind) + MarkContext(meeting, m.Time))))
            .OrderBy(x => x.Time);
        var stack = new StackPanel();
        var first = true;
        foreach (var (time, glyph, text) in items)
        {
            if (!first) stack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Margin = new Thickness(22, 0, 22, 0), Fill = UI.Brush("DividerBrush") });
            first = false;
            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(20, 13, 24, 13) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = UI.Icon(glyph, 12, "MutedBrush");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 3, 0, 0);
            row.Children.Add(icon);
            var body = UI.Text(text, 14.5, wrap: true);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            var at = Parts.Time(time, Play);
            Grid.SetColumn(at, 2);
            row.Children.Add(at);
            stack.Children.Add(row);
        }
        var card = new Border { CornerRadius = new CornerRadius(16), Child = stack };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        _content.Children.Add(card);
    }

    /// What was being said when a mark was set: ": Then we build the meetings feature next."
    private static string MarkContext(Meeting meeting, double time)
    {
        var said = meeting.Segments.LastOrDefault(s => s.Start <= time + 1);
        return said is null ? "" : $": {said.Text}";
    }

    private void AddQuestions(Meeting meeting)
    {
        if (meeting.Questions.Count == 0) return;
        AddCaption(Loc.T("Du frågade", "You asked"), 34);
        foreach (var exchange in meeting.Questions.TakeLast(5))
        {
            var q = UI.Text(exchange.Question, 14.5, FontWeights.SemiBold, wrap: true);
            var a = UI.Text(exchange.Answer, 14, brush: "MutedBrush", wrap: true);
            a.IsTextSelectionEnabled = true;
            var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 14, 22, 16), Margin = new Thickness(0, 0, 0, 10), Child = UI.VStack(6, q, a) };
            UI.Bind(card, Border.BackgroundProperty, "CardBrush");
            _content.Children.Add(card);
        }
    }

    // ---- Text ----------------------------------------------------------------------------

    private void BuildText(Meeting meeting)
    {
        // Find in the text: one field for the page's life, so typing in it never loses focus.
        if (_findRow is null)
        {
            _findRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            _findBox = new TextBox
            {
                Text = _find,
                PlaceholderText = Loc.T("Sök i texten", "Search the text"),
                Style = UI.Style("FieldStyle"),
                Padding = new Thickness(36, 8, 12, 8),
                CornerRadius = new CornerRadius(11),
                FontSize = 14,
                Width = 280,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(_findBox, _findBox.PlaceholderText);
            _findBox.TextChanged += (_, _) =>
            {
                _find = _findBox.Text.Trim();
                if (Meeting is { } m) BuildTranscript(m);
            };
            var icon = UI.Icon(Icons.Search, 13, "MutedBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Margin = new Thickness(13, 0, 0, 0);
            icon.IsHitTestVisible = false;
            _findRow.Children.Add(_findBox);
            _findRow.Children.Add(icon);
        }
        _content.Children.Add(_findRow);

        // Everyone in the meeting: click a name to set it.
        var people = new FlowPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
        foreach (var speaker in meeting.Speakers.Where(s => meeting.Segments.Any(x => x.SpeakerId == s.Id)))
            people.Children.Add(SpeakerChip(meeting, speaker, large: true));
        _content.Children.Add(people);
        _content.Children.Add(_transcript);
        BuildTranscript(meeting);
    }

    private Grid? _findRow;
    private readonly StackPanel _transcript = new();

    /// Who said what, turn by turn, with your notes and marks where they were set.
    private void BuildTranscript(Meeting meeting)
    {
        _turnElements.Clear();
        _transcript.Children.Clear();
        if (meeting.Segments.Count == 0)
        {
            _transcript.Children.Add(UI.Muted(Loc.T("Ingen text i det här mötet.", "No text in this meeting."), 14));
            return;
        }
        var marks = meeting.Notes.Select(n => (n.Time, Glyph: Icons.Pin, Text: n.Text))
            .Concat(meeting.Marks.Select(m => (m.Time, Glyph: Parts.MarkGlyph(m.Kind), Text: Parts.MarkTitle(m.Kind))))
            .OrderBy(x => x.Time).ToList();
        var next = 0;
        var matches = 0;
        foreach (var turn in Format.Turns(meeting.Segments))
        {
            while (next < marks.Count && marks[next].Time <= turn.Start)
            {
                var mark = marks[next++];
                if (_find.Length == 0) _transcript.Children.Add(NoteLine(mark));
            }
            var speaker = meeting.SpeakerById(turn.SpeakerId) ?? new Speaker { Id = turn.SpeakerId };
            if (_find.Length > 0 && !MeetingStore.Contains(turn.Text, _find)) continue;
            matches++;
            var head = UI.HStack(10, SpeakerChip(meeting, speaker, large: false), Parts.Time(turn.Start, Play));
            var body = TurnText(turn.Text, speaker.IsBackground);
            var block = new Border
            {
                Child = UI.VStack(6, head, body),
                Padding = new Thickness(12, 10, 12, 12),
                Margin = new Thickness(-12, 0, -12, 4),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            _turnElements.Add((turn.Start, turn.End, block));
            _transcript.Children.Add(block);
        }
        if (_find.Length == 0) while (next < marks.Count) _transcript.Children.Add(NoteLine(marks[next++]));
        if (_find.Length > 0 && matches == 0)
            _transcript.Children.Add(UI.Muted(Loc.T($"Inget hittades för ”{_find}”.", $"Nothing found for “{_find}”."), 14));
    }

    private TextBlock TurnText(string text, bool background)
    {
        var block = new TextBlock { FontSize = 14.5, LineHeight = 23, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        UI.Bind(block, TextBlock.ForegroundProperty, background ? "FaintBrush" : "PrimaryBrush");
        if (_find.Length == 0) { block.Text = text; return block; }
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        var options = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var from = 0;
        while (from < text.Length)
        {
            var found = compare.IndexOf(text.AsSpan(from), _find, options, out var length);
            if (found < 0 || length == 0) break;
            var at = from + found;
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[from..at] });
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(at, length), FontWeight = FontWeights.Bold });
            from = at + length;
        }
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[from..] });
        return block;
    }

    private UIElement NoteLine((double Time, string Glyph, string Text) note)
    {
        var row = UI.HStack(8, UI.Icon(note.Glyph, 11, "MutedBrush"), UI.Text(note.Text, 13, FontWeights.SemiBold, "MutedBrush"), Parts.Time(note.Time, Play));
        row.Margin = new Thickness(0, 4, 0, 14);
        return row;
    }

    /// A speaker's name as a chip: click to name them (2.3) or call them background sound.
    private Button SpeakerChip(Meeting meeting, Speaker speaker, bool large)
    {
        var label = UI.Text(speaker.DisplayName + (speaker.IsBackground ? Loc.T(" (bakgrund)", " (background)") : ""), large ? 13 : 13, FontWeights.SemiBold,
                            speaker.Name is null || speaker.IsBackground ? "MutedBrush" : "PrimaryBrush");
        var face = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(10, 3, 10, 4), Child = UI.HStack(5, label, UI.Icon(Icons.ChevronDown, 8, "FaintBrush")) };
        UI.Bind(face, Border.BackgroundProperty, "ChipBrush");
        var button = UI.PlainButton(face, () => { }, Loc.T($"Sätt namn på {speaker.DisplayName}", $"Name {speaker.DisplayName}"));
        button.Click += (s, _) => NameField.ShowSpeakerFlyout(App, (FrameworkElement)s, speaker, live: false,
            (name, _) =>
            {
                if (Meeting is not { } m || m.SpeakerById(speaker.Id) is not { } target) return;
                target.Name = name;
                target.IsBackground = false;
                App.Meetings.Save(m);
            },
            background =>
            {
                if (Meeting is not { } m || m.SpeakerById(speaker.Id) is not { } target) return;
                target.IsBackground = background;
                if (background) target.Name = null;
                App.Meetings.Save(m);
            });
        return button;
    }
}
