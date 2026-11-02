using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.App.Pages;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Speech;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mindtalk.App.Meetings;

/// <summary>
/// Meetings: start one (with a round of names or right away), or drop a recording; search,
/// folders, the meetings by day; and a bar at the bottom to ask about all of them.
/// While a meeting is recorded, the start card shows it instead.
/// </summary>
internal sealed partial class MeetingsPage : PageView
{
    /// The Mac's taglines; the first one first.
    private static readonly (string Sv, string En)[] Taglines =
    [
        ("Den känner igen era röster.", "It knows your voices."),
        ("Ingen bot i era möten.", "No bot in your meetings."),
        ("Vem som sa vad, medan det sägs.", "Who said what, as it's said."),
        ("Allt stannar på din dator.", "Everything stays on your PC."),
    ];
    private static int _shown;

    private readonly MainWindow _main;
    private readonly MeetingHub _hub;
    private readonly Border _startHost = new();
    private readonly Grid _dropZone = new();
    private readonly Rectangle _dashes;
    private readonly StackPanel _dropContent = new() { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new() { Spacing = 26 };
    private readonly Parts.AskBar _ask;
    private string? _folder;
    private string? _listKey, _startKey, _dropKey;
    private Orb? _liveOrb;
    private TextBlock? _liveClock;
    private TextBlock? _languageText, _writerText;
    private ToggleButton? _remember;

    public MeetingsPage(App app, MainWindow main, MeetingHub hub) : base(app)
    {
        _main = main;
        _hub = hub;
        Column.Spacing = 0;
        Add(UI.PageHeader(Loc.T("Möten", "Meetings")), 0);

        // The start card and the drop zone, side by side.
        var top = new Grid { ColumnSpacing = 20, Margin = new Thickness(0, 22, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(_startHost);
        _dashes = new Rectangle { RadiusX = 20, RadiusY = 20, StrokeThickness = 1.4, StrokeDashArray = [5, 4] };
        UI.Bind(_dashes, Shape.StrokeProperty, "FieldStrokeBrush");
        _dropZone.Children.Add(_dashes);
        _dropZone.Children.Add(_dropContent);
        _dropContent.Margin = new Thickness(24);
        Grid.SetColumn(_dropZone, 1);
        top.Children.Add(_dropZone);
        Add(top, 1);

        // Search and folders.
        var bar = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 30, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _search = new TextBox
        {
            PlaceholderText = Loc.T("Sök i mötena", "Search meetings"),
            Style = UI.Style("FieldStyle"),
            Padding = new Thickness(36, 8, 12, 8),
            CornerRadius = new CornerRadius(11),
            FontSize = 14,
        };
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        _search.TextChanged += (_, _) => { _listKey = null; Refresh(); };
        var searchIcon = UI.Icon(Icons.Search, 13, "MutedBrush");
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.Margin = new Thickness(13, 0, 0, 0);
        searchIcon.IsHitTestVisible = false;
        var searchBox = new Grid();
        searchBox.Children.Add(_search);
        searchBox.Children.Add(searchIcon);
        bar.Children.Add(searchBox);
        Grid.SetColumn(_chips, 1);
        bar.Children.Add(_chips);
        Add(bar, 2);
        Add(_list, 3, 26);
        Column.Children.Add(Parts.Spacer(96));

        // Ask about all meetings, floating at the bottom.
        _ask = new Parts.AskBar(Loc.T("Fråga om alla möten", "Ask about all meetings"), "Ctrl K", q => AskSheet.Show(App, _main, _hub, q, _folder, null))
        {
            Margin = new Thickness(0, 0, 0, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Children.Add(_ask);
        SizeChanged += (_, e) => _ask.Width = Math.Max(300, Math.Min(e.NewSize.Width > 1200 ? 860 : 720, e.NewSize.Width - 96) - 90);

        // Drop a recording anywhere on the page.
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("Gör till ett möte", "Make it a meeting");
            Highlight(true);
        };
        DragLeave += (_, _) => Highlight(false);
        Drop += async (_, e) =>
        {
            Highlight(false);
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0) await _hub.Import(paths);
        };

        _hub.Frame += OnFrame;
        _hub.LiveChanged += OnLive;
        Loaded += (_, _) => Shown = this;
        Unloaded += (_, _) =>
        {
            _hub.Frame -= OnFrame;
            _hub.LiveChanged -= OnLive;
            if (ReferenceEquals(Shown, this)) Shown = null;
        };
        _shown++;

        // While the page is shown: Ctrl+F searches, Return starts a meeting right away.
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) => { e.Handled = true; FocusSearch(); };
        KeyboardAccelerators.Add(find);
        var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter };
        enter.Invoked += (_, e) =>
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
            if (focused is TextBox or ButtonBase or Selector or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton || _hub.IsLive || !ReferenceEquals(_main.Pushed, null)) return;
            e.Handled = true;
            StartRightAway();
        };
        KeyboardAccelerators.Add(enter);
    }

    /// The Meetings page on screen now, if it is.
    public static MeetingsPage? Shown { get; private set; }

    private void OnLive() => Refresh();

    private void OnFrame()
    {
        if (_hub.Live is not { } live) return;
        if (_liveOrb is not null) _liveOrb.Level = live.State == MeetingState.Recording ? live.Level : 0;
        if (_liveClock is not null) _liveClock.Text = LiveStatus(live);
    }

    private void Highlight(bool on)
    {
        UI.Bind(_dashes, Shape.StrokeProperty, on ? "InkBrush" : "FieldStrokeBrush");
        _dashes.StrokeThickness = on ? 2 : 1.4;
        _dashes.Fill = on ? UI.Brush("HoverBrush") : null;
    }

    public override void Refresh()
    {
        RefreshStart();
        RefreshDrop();
        RefreshList();
    }

    // ---- Keys ------------------------------------------------------------------------

    public void FocusAsk() => _ask.FocusField();
    public void FocusSearch() => _search.Focus(FocusState.Keyboard);
    public void Search(string words) => _search.Text = words;
    public void StartRightAway() => _hub.Start(roundOfNames: false);

    public async void ChooseFile()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary };
        foreach (var type in MeetingHub.ImportTypes) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_main));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await _hub.Import(files.Select(f => f.Path).ToList());
    }

    // ---- The start card ----------------------------------------------------------------

    private void RefreshStart()
    {
        var live = _hub.IsLive ? _hub.Live : null;
        var key = live is null ? "start" : $"live|{live.Meeting.Id}|{live.State}";
        if (key != _startKey)
        {
            _startKey = key;
            _liveOrb = null;
            _liveClock = null;
            _startHost.Child = live is null ? StartCard() : LiveCard(live);
        }
        if (_languageText is not null) _languageText.Text = Loc.T($"Lyssnar på {Format.Language(App.Engine.Id)}", $"Listens in {Format.Language(App.Engine.Id)}");
        if (_writerText is not null) _writerText.Text = Loc.T($"Sammanfattas av {_hub.WriterName}", $"Summarized by {_hub.WriterName}");
        if (_remember is not null) _remember.IsChecked = App.Settings.RememberVoices;
    }

    private Border StartCard()
    {
        var tagline = Taglines[Demo.On ? 0 : (_shown - 1) % Taglines.Length];
        var stack = new StackPanel { Spacing = 0 };
        var title = Parts.Serif(Loc.T(tagline.Sv, tagline.En), 25);
        title.FontFamily = UI.Serif;
        stack.Children.Add(title);
        var orb = new Orb(165) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 14) };
        stack.Children.Add(orb);
        var names = Parts.StartButton(Loc.T("Starta med namnrunda", "Start with a round of names"), ink: false, () => _hub.Start(roundOfNames: true));
        var now = Parts.StartButton(Loc.T("Starta direkt", "Start right away"), ink: true, () => _hub.Start(roundOfNames: false), returnKey: true);
        now.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(names);
        stack.Children.Add(now);

        var language = Parts.TextWithAction(Icons.Globe, "", Loc.T("Byt", "Change"), ShowLanguages);
        _languageText = (TextBlock)language.Children[1];
        language.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(language);

        // Remember voices, with what it means a click away.
        var rememberRow = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        rememberRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rememberRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = UI.PlainButton(UI.Icon(Icons.Info, 13, "MutedBrush"), () => { }, Loc.T("Vad betyder det?", "What does this mean?"));
        var why = Loc.T("Säg ditt namn en gång i en namnrunda, så känner Mindtalk igen dig i nästa möte. Bara ett röstavtryck sparas – siffror, aldrig ljud av rösten – och bara på din dator. Fråga de andra först.",
                        "Say your name once in a round of names, and Mindtalk knows you in the next meeting. Only a voiceprint is kept – numbers, never audio of the voice – and only on your PC. Ask the others first.");
        ToolTipService.SetToolTip(info, why);
        info.Click += (_, _) =>
        {
            var flyout = new Flyout { Content = new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, FontSize = 13 }, FlyoutPresenterStyle = NameField.PresenterStyle() };
            flyout.ShowAt(info);
        };
        rememberRow.Children.Add(UI.HStack(8, UI.Text(Loc.T("Kom ihåg rösterna", "Remember voices"), 13, brush: "MutedBrush"), info));
        _remember = UI.Switch(Loc.T("Kom ihåg rösterna", "Remember voices"), App.Settings.RememberVoices, on => _ = SetRememberVoices(on));
        Grid.SetColumn(_remember, 1);
        rememberRow.Children.Add(_remember);
        stack.Children.Add(rememberRow);

        var writer = Parts.TextWithAction(null, "", Loc.T("Byt", "Change"), ShowWriters);
        _writerText = (TextBlock)writer.Children[0];
        var chip = Parts.KeyChip(App.Settings.MeetingShortcut);
        ToolTipService.SetToolTip(chip, Loc.T("Starta eller avsluta ett möte, från vilken app som helst", "Start or end a meeting, from any app"));
        var foot = new FlowPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(writer);
        foot.Children.Add(chip);
        stack.Children.Add(foot);

        var card = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(25, 24, 25, 22), Child = stack, BorderThickness = new Thickness(1) };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        UI.Bind(card, Border.BorderBrushProperty, "DividerBrush");
        return card;
    }

    /// While a meeting is recorded: the meeting, its clock and the orb listening.
    private Border LiveCard(ILiveMeeting live)
    {
        var stack = new StackPanel { Spacing = 0 };
        _liveClock = UI.Text(LiveStatus(live), 13, FontWeights.SemiBold);
        Parts.Tabular(_liveClock);
        stack.Children.Add(UI.HStack(8, Parts.RecordingDot(9), _liveClock));
        var title = Parts.Serif(Format.Title(live.Meeting), 25);
        title.FontFamily = UI.Serif;
        title.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(title);
        _liveOrb = new Orb(165) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 14) };
        stack.Children.Add(_liveOrb);
        var finishing = live.State is MeetingState.Finishing or MeetingState.Summarizing;
        var open = Parts.StartButton(Loc.T("Visa mötet", "Show the meeting"), ink: false, _hub.OpenLive);
        stack.Children.Add(open);
        if (!finishing)
        {
            var end = Parts.StartButton(Loc.T("Avsluta mötet", "End meeting"), ink: true, () => _ = _hub.End());
            end.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(end);
        }
        else
        {
            var progress = Parts.Progress(live.FinishProgress > 0 ? live.FinishProgress : null);
            progress.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(progress);
        }
        var listening = UI.Text(Loc.T($"Lyssnar på {Format.Language(App.Engine.Id)}", $"Listening in {Format.Language(App.Engine.Id)}")
                                + (live.HearsCall ? Loc.T(" · hör samtalet", " · hears the call") : ""), 13, brush: "MutedBrush");
        listening.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(listening);
        var card = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(25, 24, 25, 22), Child = stack, BorderThickness = new Thickness(1) };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        UI.Bind(card, Border.BorderBrushProperty, "DividerBrush");
        return card;
    }

    public static string LiveStatus(ILiveMeeting live) => live.State switch
    {
        MeetingState.Paused => Loc.T($"Pausat · {Meeting.Clock(live.Elapsed.TotalSeconds)}", $"Paused · {Meeting.Clock(live.Elapsed.TotalSeconds)}"),
        MeetingState.Finishing => Loc.T("Lyssnar igenom hela mötet…", "Listening through the whole meeting…"),
        MeetingState.Summarizing => Loc.T("Skriver sammanfattningen…", "Writing the summary…"),
        _ => Loc.T($"Spelar in · {Meeting.Clock(live.Elapsed.TotalSeconds)}", $"Recording · {Meeting.Clock(live.Elapsed.TotalSeconds)}"),
    };

    private async Task SetRememberVoices(bool on)
    {
        if (on && !App.Settings.RememberVoices)
        {
            var yes = await MeetingActions.Confirm(XamlRoot, Loc.T("Kom ihåg rösterna?", "Remember voices?"),
                Loc.T("Mindtalk sparar ett röstavtryck – siffror, aldrig ljud – för varje person du namnger, så att de känns igen i senare möten. Det stannar på din dator, och du kan glömma en person eller alla när du vill.\n\nFråga de andra om det är okej först.",
                      "Mindtalk keeps a voiceprint – numbers, never audio – of each person you name, so they're recognized in later meetings. It stays on your PC, and you can forget one person or everyone at any time.\n\nAsk the others if it's all right first."),
                Loc.T("Slå på", "Turn On"));
            if (!yes) { if (_remember is not null) _remember.IsChecked = false; return; }
        }
        App.Settings.RememberVoices = on;
        App.SaveSettings();
        App.Refresh();
    }

    private void ShowLanguages(FrameworkElement anchor)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var model in SpeechModel.All)
        {
            var title = model.Id == "swedish" ? Loc.T("Svenska", "Swedish") : Loc.T("Flera språk", "Several languages");
            if (!App.IsInstalled(model)) title += Loc.T($" – laddas ned ({model.SizeText})", $" – downloads ({model.SizeText})");
            var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = model == App.Engine };
            item.Click += (_, _) =>
            {
                if (App.IsInstalled(model)) App.SetEngine(model);
                else App.DownloadModel(model, thenUse: true);
                App.Refresh();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void ShowWriters(FrameworkElement anchor)
    {
        if (App.Writers is not { } writers) { _main.ShowSection(SettingsSection.Meetings); return; }
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var status in writers.All.Where(s => s.Available))
        {
            var text = status.Ready ? status.Title : $"{status.Title} – {status.Detail}";
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = status.Kind == writers.Current };
            item.Click += (_, _) =>
            {
                if (status.Ready) { writers.Use(status.Kind, status.Model); App.Settings.Writer = status.Kind; App.SaveSettings(); App.Refresh(); }
                else _main.ShowSection(SettingsSection.Meetings);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = Loc.T("Inställningar för möten…", "Meetings Settings…") };
        settings.Click += (_, _) => _main.ShowSection(SettingsSection.Meetings);
        menu.Items.Add(settings);
        menu.ShowAt(anchor);
    }

    // ---- The drop zone ---------------------------------------------------------------

    private void RefreshDrop()
    {
        var key = $"{_hub.Importing}|{_hub.ImportError}";
        if (key == _dropKey) return;
        _dropKey = key;
        _dropContent.Children.Clear();
        var tile = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center };
        UI.Bind(tile, Border.BackgroundProperty, "ChipBrush");
        tile.Child = WaveformPlus();
        _dropContent.Children.Add(tile);

        if (_hub.Importing is { } file)
        {
            var title = UI.Text(Loc.T("Gör ett möte av inspelningen…", "Making a meeting of the recording…"), 17, FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 22, 0, 0);
            _dropContent.Children.Add(title);
            var name = UI.Text(file, 13, brush: "MutedBrush");
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 8, 0, 16);
            _dropContent.Children.Add(name);
            _dropContent.Children.Add(Parts.Progress(null, 200));
            return;
        }
        var heading = UI.Text(Loc.T("Har du redan en inspelning?", "Already have a recording?"), 17.5, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.TextAlignment = TextAlignment.Center;
        heading.TextWrapping = TextWrapping.Wrap;
        heading.Margin = new Thickness(0, 22, 0, 0);
        _dropContent.Children.Add(heading);
        var text = UI.Text(Loc.T("Släpp en ljud- eller videofil här, så blir den ett möte med text, talare och sammanfattning.",
                                 "Drop an audio or video file here, and it becomes a meeting with text, speakers and a summary."), 13, brush: "MutedBrush", wrap: true);
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 290;
        text.Margin = new Thickness(0, 10, 0, 18);
        _dropContent.Children.Add(text);
        var choose = new Button { Content = Loc.T("Välj fil…", "Choose File…"), Style = UI.Style("SoftButtonStyle"), Padding = new Thickness(15, 8, 15, 8) };
        UI.Bind(choose, Control.BackgroundProperty, "ChipBrush");
        choose.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        choose.Click += (_, _) => ChooseFile();
        var row = UI.HStack(10, choose, Parts.KeyChip("Ctrl O"));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        _dropContent.Children.Add(row);
        if (_hub.ImportError is { } error)
        {
            var problem = UI.Text(error, 12.5, brush: "OrangeBrush", wrap: true);
            problem.TextAlignment = TextAlignment.Center;
            problem.MaxWidth = 290;
            problem.Margin = new Thickness(0, 14, 0, 0);
            _dropContent.Children.Add(problem);
        }
    }

    /// The Mac's "waveform with a plus": a few bars, a filled plus at the lower right.
    private static Grid WaveformPlus()
    {
        var grid = new Grid { Width = 26, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var height in new[] { 7.0, 13, 20, 11, 16, 8 })
        {
            var bar = new Rectangle { Width = 1.8, Height = height, RadiusX = 0.9, RadiusY = 0.9, VerticalAlignment = VerticalAlignment.Center };
            UI.Bind(bar, Shape.FillProperty, "MutedBrush");
            bars.Children.Add(bar);
        }
        grid.Children.Add(bars);
        var plus = new Grid { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -1) };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, "PrimaryBrush");
        var ring = new Ellipse { StrokeThickness = 1.5 };
        UI.Bind(ring, Shape.StrokeProperty, "ChipBrush");
        plus.Children.Add(disc);
        plus.Children.Add(ring);
        plus.Children.Add(UI.Icon(Icons.Plus, 6.5, "OnInkBrush"));
        grid.Children.Add(plus);
        return grid;
    }

    // ---- Folders and the list ------------------------------------------------------------

    private void RefreshList()
    {
        var all = App.Meetings.All.Where(m => !(_hub.IsLive && _hub.Live!.Meeting.Id == m.Id)).ToList();
        var working = App.MeetingService?.Working ?? new Dictionary<Guid, double>();
        var query = _search.Text.Trim();
        var key = string.Join(",", all.Select(m => $"{m.Id}:{m.Modified.Ticks}:{m.State}:{(working.TryGetValue(m.Id, out var p) ? (int)(p * 50) : -1)}"))
                  + $"|{_folder}|{query}|{string.Join(",", App.Meetings.Folders)}|{DateTime.Now.Date}";
        if (key == _listKey) return;
        _listKey = key;
        if (_folder is not null && !App.Meetings.Folders.Contains(_folder)) _folder = null;
        BuildChips(all);
        _ask.Field.PlaceholderText = _folder is null ? Loc.T("Fråga om alla möten", "Ask about all meetings") : Loc.T($"Fråga om {_folder}", $"Ask about {_folder}");

        _list.Children.Clear();
        if (all.Count == 0)
        {
            _list.Children.Add(Empty());
            return;
        }
        if (query.Length > 0)
        {
            ShowHits(App.Meetings.Search(query, _folder));
            return;
        }
        var shown = _folder is null ? all : all.Where(m => string.Equals(m.Folder, _folder, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (shown.Count == 0)
        {
            _list.Children.Add(UI.Muted(Loc.T("Inga möten i den här mappen än. Högerklicka på ett möte för att flytta hit det.", "No meetings in this folder yet. Right-click a meeting to move it here."), 13));
            return;
        }
        var now = DateTime.Now;
        foreach (var day in shown.GroupBy(m => m.Started.Date))
        {
            var rows = new StackPanel();
            var first = true;
            foreach (var meeting in day)
            {
                if (!first) rows.Children.Add(UI.Divider());
                first = false;
                rows.Children.Add(Row(meeting, working.TryGetValue(meeting.Id, out var progress) ? progress : null));
            }
            var card = new Border { CornerRadius = new CornerRadius(16), Child = rows };
            UI.Bind(card, Border.BackgroundProperty, "CardBrush");
            _list.Children.Add(UI.VStack(10, Parts.Caption(Format.Day(day.Key, now)), card));
        }
    }

    private UIElement Empty()
    {
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
        var title = Parts.Serif(Loc.T("Inga möten än", "No meetings yet"), 22);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(title);
        var text = UI.Muted(Loc.T("Starta ett ovan, tryck Ctrl+Alt+M i vilken app som helst, eller släpp en inspelning här.",
                                  "Start one above, press Ctrl+Alt+M in any app, or drop a recording here."), 13);
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 380;
        stack.Children.Add(text);
        return stack;
    }

    private void BuildChips(List<Meeting> all)
    {
        _chips.Children.Clear();
        _chips.Children.Add(Chip(Loc.T("Alla", "All"), all.Count, null, _folder is null, () => SelectFolder(null)));
        var folders = App.Meetings.Folders;
        // The chosen folder is always visible; two in all, the rest under More.
        var visible = folders.Take(2).ToList();
        if (_folder is not null && !visible.Contains(_folder)) visible[^1] = _folder;
        foreach (var folder in visible)
        {
            var count = all.Count(m => string.Equals(m.Folder, folder, StringComparison.CurrentCultureIgnoreCase));
            var chip = Chip(folder, count, Icons.Folder, folder == _folder, () => SelectFolder(folder));
            chip.ContextFlyout = FolderMenu(folder);
            _chips.Children.Add(chip);
        }
        var more = Chip(folders.Count > visible.Count ? Loc.T("Fler", "More") : Loc.T("Mappar", "Folders"), null, null, false, () => { });
        ((StackPanel)((Border)more.Content).Child).Children.Add(UI.Icon(Icons.ChevronDown, 9, "MutedBrush"));
        more.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            foreach (var folder in folders.Except(visible))
            {
                var item = new MenuFlyoutItem { Text = folder, Icon = new FontIcon { Glyph = Icons.Folder } };
                item.Click += (_, _) => SelectFolder(folder);
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = Loc.T("Ny mapp…", "New Folder…"), Icon = new FontIcon { Glyph = Icons.NewFolder } };
            create.Click += async (_, _) => await MeetingActions.NewFolder(App, XamlRoot);
            menu.Items.Add(create);
            if (_folder is not null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var rename = new MenuFlyoutItem { Text = Loc.T($"Byt namn på ”{_folder}”…", $"Rename “{_folder}”…") };
                var current = _folder;
                rename.Click += async (_, _) => await RenameFolder(current);
                menu.Items.Add(rename);
                var remove = new MenuFlyoutItem { Text = Loc.T($"Ta bort ”{_folder}”…", $"Delete “{_folder}”…") };
                remove.Click += async (_, _) => await RemoveFolder(current);
                menu.Items.Add(remove);
            }
            menu.ShowAt(more);
        };
        _chips.Children.Add(more);
    }

    private MenuFlyout FolderMenu(string folder)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Loc.T("Byt namn…", "Rename…") };
        rename.Click += async (_, _) => await RenameFolder(folder);
        menu.Items.Add(rename);
        var remove = new MenuFlyoutItem { Text = Loc.T("Ta bort mappen…", "Delete Folder…") };
        remove.Click += async (_, _) => await RemoveFolder(folder);
        menu.Items.Add(remove);
        return menu;
    }

    private async Task RenameFolder(string folder)
    {
        var name = await MeetingActions.Prompt(XamlRoot, Loc.T("Byt namn på mappen", "Rename the Folder"), folder, Loc.T("Byt namn", "Rename"));
        if (name is null || name == folder) return;
        App.Meetings.RenameFolder(folder, name);
        if (_folder == folder) _folder = name;
    }

    private async Task RemoveFolder(string folder)
    {
        var sure = await MeetingActions.Confirm(XamlRoot, Loc.T($"Ta bort mappen ”{folder}”?", $"Delete the folder “{folder}”?"),
            Loc.T("Mötena i den finns kvar, utan mapp.", "The meetings in it stay, out of any folder."), Loc.T("Ta bort", "Delete"));
        if (!sure) return;
        if (_folder == folder) _folder = null;
        App.Meetings.RemoveFolder(folder);
    }

    private void SelectFolder(string? folder)
    {
        _folder = folder;
        _listKey = null;
        Refresh();
    }

    /// "All 9", "📁 Customers 2": the chosen one raised in white.
    private static Button Chip(string title, int? count, string? glyph, bool selected, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null) content.Children.Add(UI.Icon(glyph, 12, "MutedBrush"));
        content.Children.Add(UI.Text(title, 13, FontWeights.SemiBold));
        if (count is not null) content.Children.Add(UI.Text(count.Value.ToString(Loc.Culture), 13, brush: "MutedBrush"));
        var face = new Border
        {
            CornerRadius = new CornerRadius(100),
            Height = 31,
            Padding = new Thickness(13, 0, 13, 0),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        UI.Bind(face, Border.BackgroundProperty, selected ? (UI.Dark ? "PillBrush" : "ControlBrush") : "CardBrush");
        UI.Bind(face, Border.BorderBrushProperty, selected ? "FieldStrokeBrush" : "CardBrush");
        var button = UI.PlainButton(face, action);
        AutomationProperties.SetName(button, count is null ? title : $"{title} {count}");
        return button;
    }

    /// One meeting in the list: when, how long, who, the first lines of the summary.
    private Border Row(Meeting meeting, double? working)
    {
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(18, 14, 14, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, UI.Dark ? "ChipBrush" : "ControlBrush");
        icon.Children.Add(disc);
        icon.Children.Add(UI.Icon(meeting.Source switch { MeetingSource.Call => Icons.Phone, MeetingSource.Import => Icons.Import, _ => Icons.People }, 14, "MutedBrush"));
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 3 };
        var title = UI.Text(Format.Title(meeting), 14.5, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        var meta = $"{Format.Clock(meeting.Started)} · {Meeting.Length(meeting.Duration)}";
        if (meeting.Talkers.Any()) meta += $" · {meeting.PeopleLine}";
        var metaText = UI.Text(meta, 12.5, brush: "MutedBrush");
        metaText.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(metaText);
        if (working is { } progress || meeting.State is MeetingState.Finishing or MeetingState.Summarizing)
        {
            var status = UI.Text(meeting.State == MeetingState.Summarizing ? Loc.T("Skriver sammanfattningen…", "Writing the summary…")
                                 : meeting.Source == MeetingSource.Import ? Loc.T("Lyssnar igenom inspelningen…", "Listening through the recording…")
                                 : Loc.T("Gör klart…", "Finishing…"), 12.5, brush: "MutedBrush");
            var bar = Parts.Progress(working is > 0 ? working : null, 160);
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(UI.HStack(10, status, bar));
        }
        else if (Format.Gist(meeting) is { Length: > 0 } gist)
        {
            var gistText = UI.Text(gist, 13, brush: meeting.State == MeetingState.Failed ? "OrangeBrush" : "MutedBrush", wrap: true);
            gistText.MaxLines = 2;
            gistText.TextTrimming = TextTrimming.WordEllipsis;
            gistText.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(gistText);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        if (meeting.Folder is { } folder)
        {
            var chip = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(9, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            UI.Bind(chip, Border.BackgroundProperty, "ChipBrush");
            chip.Child = UI.HStack(5, UI.Icon(Icons.Folder, 10, "MutedBrush"), UI.Text(folder, 11.5, FontWeights.Medium, "MutedBrush"));
            right.Children.Add(chip);
        }
        var more = UI.PlainButton(UI.Icon(Icons.More, 14, "MutedBrush"), () => { }, Loc.T("Mer", "More"));
        more.Width = 28;
        more.Height = 28;
        more.HorizontalContentAlignment = HorizontalAlignment.Center;
        more.Click += (_, _) => MeetingActions.RowMenu(App, _hub, XamlRoot, meeting).ShowAt(more);
        right.Children.Add(more);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = new Border { Child = grid, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(16) };
        Parts.Hover(row, "Transparent");
        Parts.OnClick(row, () => _hub.OpenMeeting(meeting.Id));
        row.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var menu = MeetingActions.RowMenu(App, _hub, XamlRoot, meeting);
            if (e.TryGetPosition(row, out var at)) menu.ShowAt(row, new FlyoutShowOptions { Position = at });
            else menu.ShowAt(row);
        };
        AutomationProperties.SetName(row, $"{Format.Title(meeting)}, {meta}");
        return row;
    }

    /// Search: each meeting with its hits, a time on each you can click to go there.
    private void ShowHits(IReadOnlyList<MeetingHit> hits)
    {
        if (hits.Count == 0)
        {
            _list.Children.Add(UI.Muted(Loc.T($"Inget hittades för ”{_search.Text.Trim()}”.", $"Nothing found for “{_search.Text.Trim()}”."), 13));
            return;
        }
        var now = DateTime.Now;
        var query = _search.Text.Trim();
        foreach (var group in hits.GroupBy(h => h.Meeting.Id))
        {
            var meeting = group.First().Meeting;
            var rows = new StackPanel();
            var header = new Grid { Padding = new Thickness(18, 14, 18, 8) };
            header.Children.Add(UI.VStack(2,
                UI.Text(Format.Title(meeting), 14.5, FontWeights.SemiBold),
                UI.Text($"{Format.When(meeting.Started, now)} · {Meeting.Length(meeting.Duration)}", 12.5, brush: "MutedBrush")));
            var headerRow = new Border { Child = header, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Parts.OnClick(headerRow, () => _hub.OpenMeeting(meeting.Id));
            rows.Children.Add(headerRow);
            foreach (var hit in group.Take(6))
            {
                var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 7, 18, 7) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(UI.Icon(hit.Kind switch { HitKind.Note => Icons.Pin, HitKind.Summary => Icons.Document, HitKind.Title => Icons.Edit, _ => Icons.Mic }, 11, "FaintBrush"));
                var snippet = Highlighted(hit.Snippet, query);
                Grid.SetColumn(snippet, 1);
                line.Children.Add(snippet);
                if (hit.Time is { } time)
                {
                    var at = Parts.Time(time, t => _hub.OpenMeeting(meeting.Id, t));
                    Grid.SetColumn(at, 2);
                    line.Children.Add(at);
                }
                var lineRow = new Border { Child = line, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(10) };
                Parts.Hover(lineRow, "Transparent");
                var moment = hit.Time;
                Parts.OnClick(lineRow, () => _hub.OpenMeeting(meeting.Id, moment));
                rows.Children.Add(lineRow);
            }
            if (group.Count() > 6)
            {
                var more = UI.Text(Loc.T($"och {group.Count() - 6} till", $"and {group.Count() - 6} more"), 12.5, brush: "FaintBrush");
                more.Margin = new Thickness(42, 2, 18, 0);
                rows.Children.Add(more);
            }
            rows.Children.Add(Parts.Spacer(10));
            var card = new Border { CornerRadius = new CornerRadius(16), Child = rows };
            UI.Bind(card, Border.BackgroundProperty, "CardBrush");
            _list.Children.Add(card);
        }
    }

    /// The snippet with what you searched for in semibold.
    private static TextBlock Highlighted(string text, string query)
    {
        var block = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = UI.Brush("PrimaryBrush") };
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        var options = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var at = compare.IndexOf(text, query, options, out var length);
        if (at < 0) { block.Text = text; return block; }
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[..at] });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(at, length), FontWeight = FontWeights.Bold });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[(at + length)..] });
        return block;
    }
}
