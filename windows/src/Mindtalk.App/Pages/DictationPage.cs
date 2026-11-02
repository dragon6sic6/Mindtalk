using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Stats;

namespace Mindtalk.App.Pages;

/// <summary>
/// Dictation (2.0): your key — changed right here, and a box to try it — your numbers, and
/// what you've dictated: the last 50, by day, searchable, each one to paste, copy or delete.
/// </summary>
public sealed partial class DictationPage : PageView
{
    private readonly MainWindow? _window;
    private readonly TextBlock _caption = Kit.Caption("");
    private readonly SetupCard _setup;
    private readonly Grid _keyCard = new();
    private readonly Grid _stats = new() { ColumnSpacing = 20 };
    private readonly StackPanel _history = new() { Spacing = 14 };
    private readonly TextBlock _note;
    private readonly TextBox _search;
    private readonly Grid _searchHost;
    private TextBox? _tryBox;
    private bool _trying;
    private string? _keyKey, _statsKey, _historyKey;

    public DictationPage(App app, MainWindow? window = null) : base(app)
    {
        _window = window;
        Column.Spacing = 26;
        var menu = new MenuFlyout();
        menu.Items.Add(Kit.Item(Loc.T("Rensa historiken…", "Clear History…"), ConfirmClear, Icons.Delete));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Kit.Item(Loc.T("Inställningar för diktering…", "Dictation Settings…"), OpenSettings, Icons.Gear));
        Add(Kit.Header(_caption, Kit.Title(Loc.T("Diktering", "Dictation")), menu), 0);
        _setup = new SetupCard(app);
        Add(_setup, 1);
        Add(_keyCard, 1);
        Add(Kit.Card(_stats, new Thickness(28, 26, 28, 24)), 2);

        _search = Kit.Field(Loc.T("Sök", "Search"), Icons.Search, out _searchHost);
        _search.FontSize = 14;
        _search.Padding = new Thickness(38, 7, 10, 7);
        _searchHost.Width = 220;
        ((FrameworkElement)_searchHost.Children[1]).Margin = new Thickness(13, 0, 0, 0);
        _search.TextChanged += (_, _) => { _historyKey = null; RefreshHistory(); };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = UI.Text(Loc.T("Det du dikterat", "What you’ve dictated"), 18, FontWeights.SemiBold);
        heading.Children.Add(title);
        Grid.SetColumn(_searchHost, 1);
        heading.Children.Add(_searchHost);
        Add(UI.VStack(16, heading, _history), 3, 6);

        _note = UI.Muted("", 13.5);
        Column.Children.Add(_note);
    }

    private void OpenSettings()
    {
        if (_window is not null) _window.ShowSection(SettingsSection.Dictation);
        else App.ShowMain(Page.Settings);
    }

    public override void Refresh()
    {
        var stats = App.Stats;
        _caption.Text = Loc.T($"{StatsStore.Number(stats.Today.Words)} ord i dag", $"{StatsStore.Number(stats.Today.Words)} words today").ToUpper(Loc.Culture);
        _setup.Refresh();
        RefreshKey();
        RefreshStats();
        RefreshHistory();
    }

    // ---- The key card --------------------------------------------------------------

    private bool PickingDictationKey => App.PickingKey && !App.PickingThoughtKey;

    private void RefreshKey()
    {
        var hotkey = App.Settings.Hotkey;
        var key = $"{hotkey}|{App.Settings.Mode}|{PickingDictationKey}|{_trying}|{App.NeedsSetup}";
        if (key == _keyKey) return;
        _keyKey = key;
        _keyCard.Children.Clear();

        var picking = PickingDictationKey;
        var cap = Kit.KeyCap(picking ? "…" : hotkey.Chip, 150);
        cap.Margin = new Thickness(0, 0, 8, 0);

        var title = UI.Text(picking ? Loc.T("Tryck på tangenten du vill använda", "Press the key you want to use") : HoldTitle(hotkey, App.Settings.Mode),
                            19.5, FontWeights.SemiBold, wrap: true);
        var detail = UI.Muted(picking
            ? Loc.T("Håll in de modifierartangenter du vill ha tillsammans och släpp, eller tryck på en vanlig tangent. Esc behåller den du har.",
                    "Hold the modifier keys you want together and let go, or press a single key. Esc keeps the one you have.")
            : Explanation(hotkey, App.Settings.Mode), 15);
        var change = Kit.Chip(picking ? Loc.T("Avbryt", "Cancel") : Loc.T("Ändra", "Change"), () =>
        {
            if (PickingDictationKey) App.StopPickingKey(); else App.PickKey();
        });
        change.HorizontalAlignment = HorizontalAlignment.Left;
        change.Margin = new Thickness(0, 8, 0, 0);
        var text = UI.VStack(6, title, detail, change);
        text.VerticalAlignment = VerticalAlignment.Center;

        var tryIt = Kit.Chip(_trying ? Loc.T("Klar", "Done") : Loc.T("Prova", "Try It"), ToggleTry);
        tryIt.Visibility = Kit.Show(!App.NeedsSetup && !picking);

        var row = new Grid { ColumnSpacing = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(cap);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(tryIt, 2);
        row.Children.Add(tryIt);

        var stack = UI.VStack(22, row);
        if (_trying && !App.NeedsSetup) stack.Children.Add(TryBox(hotkey));
        _keyCard.Children.Add(Kit.BigCard(stack, new Thickness(34, 36, 34, 34)));
    }

    /// "Hold right Alt and talk".
    public static string HoldTitle(Hotkey hotkey, DictationMode mode) => mode == DictationMode.Toggle
        ? Loc.T($"Tryck på {hotkey.InlineName} och prata", $"Press {hotkey.InlineName} and talk")
        : Loc.T($"Håll in {hotkey.InlineName} och prata", $"Hold {hotkey.InlineName} and talk");

    /// Where the key is, and how holding, locking and cancelling work in your mode.
    public static string Explanation(Hotkey hotkey, DictationMode mode)
    {
        var where = hotkey.ModifierKeys.Count > 1 ? hotkey.Note : hotkey.Key switch
        {
            VirtualKey.RightAlt => Loc.T("Höger Alt (AltGr) är tangenten till höger om mellanslag. ", "Right Alt (AltGr) is the key to the right of the space bar. "),
            VirtualKey.LeftAlt => Loc.T("Vänster Alt är tangenten till vänster om mellanslag. ", "Left Alt is the key to the left of the space bar. "),
            VirtualKey.RightCtrl => Loc.T("Höger Ctrl sitter längst ned till höger. ", "Right Ctrl is at the bottom right. "),
            _ => "",
        };
        if (hotkey.ModifierKeys.Count > 1) where += " ";
        var how = mode switch
        {
            DictationMode.Hold => Loc.T("Släpp när du är klar, så hamnar texten där markören står. Esc avbryter.",
                                        "Let go when you’re done and the text lands at your cursor. Esc cancels."),
            DictationMode.Toggle => Loc.T("Tryck igen när du är klar, så hamnar texten där markören står. Esc avbryter.",
                                          "Press again when you’re done and the text lands at your cursor. Esc cancels."),
            _ => Loc.T("Släpp när du är klar, så hamnar texten där markören står. Dubbeltryck eller tryck mellanslag så slipper du hålla in. Esc avbryter.",
                       "Let go when you’re done and the text lands at your cursor. Double-tap or press space so you don’t have to hold. Esc cancels."),
        };
        return where + how;
    }

    private void ToggleTry()
    {
        _trying = !_trying;
        _keyKey = null;
        RefreshKey();
        if (_trying) DispatcherQueue.TryEnqueue(() => _tryBox?.Focus(FocusState.Programmatic));
    }

    /// Try it: a box to dictate into, outlined in ink once there's something in it.
    private Grid TryBox(Hotkey hotkey)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17,
            MinHeight = 120,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Text = _tryBox?.Text ?? "",
        };
        AutomationProperties.SetName(box, Loc.T("Prova diktering här", "Try dictation here"));
        AutomationProperties.SetAutomationId(box, "TryBox");
        UI.Bind(box, Control.BackgroundProperty, "FieldBrush");
        UI.Bind(box, Control.BorderBrushProperty, "FieldStrokeBrush");
        var placeholder = UI.Muted(Loc.T($"Klicka här, håll in {hotkey.InlineName} och säg något.", $"Click here, hold {hotkey.InlineName} and say something."), 17);
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Top;
        placeholder.Margin = new Thickness(17, 12, 17, 0);
        void Paint()
        {
            var empty = box.Text.Length == 0;
            placeholder.Visibility = Kit.Show(empty);
            box.BorderThickness = new Thickness(empty ? 1 : 1.5);
            UI.Bind(box, Control.BorderBrushProperty, empty ? "FieldStrokeBrush" : "AccentBrush");
        }
        box.TextChanged += (_, _) => Paint();
        Paint();
        _tryBox = box;
        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(placeholder);
        return field;
    }

    // ---- The numbers --------------------------------------------------------------------

    private void RefreshStats()
    {
        var stats = App.Stats;
        var days = stats.Days;
        var key = $"{stats.TotalWords}|{stats.Streak}|{days.Count}";
        if (key == _statsKey) return;
        _statsKey = key;
        _stats.Children.Clear();
        _stats.ColumnDefinitions.Clear();

        var seconds = days.Values.Sum(d => d.Seconds);
        var perMinute = seconds > 0 ? (int)Math.Round(stats.TotalWords / (seconds / 60)) : 0;
        var best = days.OrderByDescending(d => d.Value.Words).FirstOrDefault();
        DateTime? bestDate = best.Key is { } k && DateTime.TryParse(k, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
        var streak = stats.Streak;

        (string Number, string Label, string? Detail)[] tiles =
        [
            (StatsStore.Number(stats.TotalWords), Loc.T("ord totalt", "words in total"),
             stats.TotalWords > 0 ? Loc.T($"Ungefär {HomePage.SavedText(stats.TotalSaved)} skrivande sparat.", $"About {HomePage.SavedText(stats.TotalSaved)} of typing saved.") : null),
            (perMinute > 0 ? StatsStore.Number(perMinute) : "–", Loc.T("ord i minuten", "words a minute"),
             Loc.T($"Du skriver runt {(int)StatsStore.TypingWordsPerMinute}", $"You type about {(int)StatsStore.TypingWordsPerMinute}")),
            (StatsStore.Number(best.Value?.Words ?? 0), Loc.T("bästa dagen", "best day"),
             bestDate?.ToString(Loc.T("ddd d MMM", "ddd, MMM d"), Loc.Culture)),
            (StatsStore.Number(streak), streak == 1 ? Loc.T("dag i rad", "day in a row") : Loc.T("dagar i rad", "days in a row"), null),
        ];
        for (var i = 0; i < tiles.Length; i++)
        {
            _stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (number, label, detail) = tiles[i];
            var figure = new TextBlock { Text = number, FontFamily = UI.Serif, FontSize = 40, LineHeight = 44, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            Typography.SetNumeralStyle(figure, FontNumeralStyle.Lining);
            UI.Bind(figure, TextBlock.ForegroundProperty, "PrimaryBrush");
            var tile = UI.VStack(3, figure, UI.Text(label, 15.5, brush: "MutedBrush", wrap: true), detail is null ? null : UI.Text(detail, 14.5, brush: "FaintBrush", wrap: true));
            ((FrameworkElement)tile.Children[1]).Margin = new Thickness(0, 4, 0, 0);
            Grid.SetColumn(tile, i);
            _stats.Children.Add(tile);
        }
    }

    // ---- What you've dictated -------------------------------------------------------

    private void RefreshHistory()
    {
        var entries = App.Recent.Entries;
        var query = _search.Text.Trim();
        var now = DateTime.Now;
        var key = string.Join(",", entries.Select(e => e.Id)) + "|" + query + "|" + now.Date.Ticks;
        if (key == _historyKey) return;
        _historyKey = key;
        _history.Children.Clear();
        _searchHost.Visibility = Kit.Show(entries.Count > 0);

        var paste = App.Settings.PasteLastShortcut?.ToString() ?? Shortcut.PasteLast.ToString();
        _note.Text = Loc.T($"Sparas bara på din dator, de senaste {RecentStore.Limit}. Hamnade texten fel? Ställ markören rätt och tryck {paste} för att klistra in den senaste igen.",
                           $"Kept only on your PC, the last {RecentStore.Limit}. Landed in the wrong place? Put the cursor right and press {paste} to paste the latest again.");
        _note.Visibility = Kit.Show(entries.Count > 0);

        if (entries.Count == 0)
        {
            var empty = UI.VStack(10, new WaveIcon(28, "FaintBrush") { HorizontalAlignment = HorizontalAlignment.Center },
                Center(UI.Text(Loc.T("Inget dikterat än", "Nothing dictated yet"), 15, FontWeights.SemiBold)),
                Center(UI.Muted(Loc.T("Det du dikterar dyker upp här, så att du kan klistra in det igen om det hamnade fel.",
                                      "What you dictate shows up here, so you can paste it again if it landed in the wrong place."), 14)));
            _history.Children.Add(Kit.Card(empty, new Thickness(40), 20));
            return;
        }
        var shown = query.Length == 0 ? entries : entries.Where(e => Core.Meetings.MeetingStore.Contains(e.Text, query)).ToList();
        if (shown.Count == 0)
        {
            _history.Children.Add(UI.Muted(Loc.T($"Inget dikterat innehåller ”{query}”.", $"Nothing you’ve dictated contains “{query}”."), 15));
            return;
        }
        foreach (var day in shown.GroupBy(e => e.Date.Date))
        {
            var rows = day.Select(e => (UIElement)new HistoryRow(App, e)).ToArray();
            var list = new StackPanel();
            for (var i = 0; i < rows.Length; i++)
            {
                if (i > 0) list.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = UI.Style("CardDividerStyle"), Margin = new Thickness(28, 0, 28, 0) });
                list.Children.Add(rows[i]);
            }
            _history.Children.Add(UI.VStack(10, Kit.Caption(Kit.Day(day.Key, now), 13), Kit.Card(list, new Thickness(0, 6, 0, 6), 20)));
        }
    }

    private static TextBlock Center(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    private async void ConfirmClear()
    {
        if (App.Recent.Entries.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = UI.Theme,
            Title = Loc.T("Rensa historiken?", "Clear the history?"),
            Content = Loc.T("Det du dikterat tas bort från din dator och går inte att få tillbaka. Statistiken finns kvar.",
                            "What you’ve dictated is removed from your PC and can’t be brought back. Your statistics stay."),
            PrimaryButtonText = Loc.T("Rensa", "Clear"),
            CloseButtonText = Loc.T("Avbryt", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Recent.Clear();
    }

    /// One dictation: when, the text, and — on hover — paste, copy and delete.
    private sealed partial class HistoryRow : Grid
    {
        public HistoryRow(App app, RecentEntry entry)
        {
            Padding = new Thickness(28, 15, 20, 15);
            ColumnSpacing = 18;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = UI.Text(Kit.Time(entry.Date), 15, brush: "MutedBrush");
            time.VerticalAlignment = VerticalAlignment.Top;
            time.Margin = new Thickness(0, 1, 0, 0);
            Children.Add(time);
            var text = UI.Text(entry.Text, 16.5, wrap: true);
            text.MaxLines = 6;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.IsTextSelectionEnabled = true;
            Grid.SetColumn(text, 1);
            Children.Add(text);

            var copyIcon = UI.Icon(Icons.Copy, 16, "MutedBrush");
            var copy = UI.PlainButton(new Grid { Width = 30, Height = 28, Children = { copyIcon } }, () => { }, Loc.T("Kopiera", "Copy"));
            copy.Click += async (_, _) =>
            {
                app.Copy(entry.Text);
                copyIcon.Glyph = Icons.Check;
                await Task.Delay(1500);
                copyIcon.Glyph = Icons.Copy;
            };
            var paste = Kit.IconButton(Icons.Clipboard, Loc.T("Klistra in i appen du var i", "Paste into the app you were in"), () => app.PasteIntoPreviousApp(entry.Text), 16);
            var delete = Kit.IconButton(Icons.Delete, Loc.T("Ta bort", "Delete"), () => app.Recent.Remove(entry.Id), 16);
            var hover = UI.HStack(0, paste, delete);
            hover.Opacity = 0;
            var actions = UI.HStack(0, hover, copy);
            actions.VerticalAlignment = VerticalAlignment.Top;
            actions.Margin = new Thickness(0, -3, 0, -3);
            Grid.SetColumn(actions, 2);
            Children.Add(actions);
            PointerEntered += (_, _) => hover.Opacity = 1;
            PointerExited += (_, _) => hover.Opacity = 0;
            // The keyboard reaches them too.
            hover.GotFocus += (_, _) => hover.Opacity = 1;

            var menu = new MenuFlyout();
            menu.Items.Add(Kit.Item(Loc.T("Klistra in i appen du var i", "Paste into the App You Were In"), () => app.PasteIntoPreviousApp(entry.Text)));
            menu.Items.Add(Kit.Item(Loc.T("Kopiera", "Copy"), () => app.Copy(entry.Text)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Kit.Item(Loc.T("Ta bort", "Delete"), () => app.Recent.Remove(entry.Id)));
            ContextFlyout = menu;
        }
    }
}

/// <summary>
/// What's still missing for dictation — the microphone, the speech model — each with one
/// button. Hidden once everything's in place. (Home and Dictation.)
/// </summary>
public sealed partial class SetupCard : Grid
{
    private readonly App _app;
    private string? _key;
    private ModelControl? _modelControl;
    private TextBlock? _modelDetail;

    public SetupCard(App app)
    {
        _app = app;
        Visibility = Visibility.Collapsed;
    }

    public void Refresh()
    {
        var key = _app.NeedsSetup ? $"{_app.MicGranted}|{_app.Model}|{_app.Engine.Id}" : "";
        if (key != _key)
        {
            _key = key;
            Children.Clear();
            Visibility = Kit.Show(_app.NeedsSetup);
            if (_app.NeedsSetup) Children.Add(Build());
        }
        _modelControl?.Refresh();
        if (_modelDetail is not null) UI.SetDetail(_modelDetail, ModelDetail(_app));
    }

    private StackPanel Build()
    {
        _modelControl = new ModelControl(_app);
        var mic = _app.MicGranted
            ? (UIElement)UI.GrantedLabel(Loc.T("Tillåten", "Allowed"))
            : UI.InkButton(Loc.T("Tillåt", "Allow"), _app.RequestMicrophone);
        var engine = _app.Engine.Title.ToLower(Loc.Culture);
        var model = UI.Row(Loc.T($"Språkmodell – {engine}", $"Speech model – {engine}"), ModelDetail(_app), _modelControl, out var detail);
        _modelDetail = detail;
        return UI.VStack(10,
            UI.SectionTitle(Loc.T("Kom igång", "Get Started")),
            UI.Rows(
                UI.Row(Loc.T("Mikrofon", "Microphone"),
                       Loc.T("Hör vad du säger medan du dikterar – aldrig annars.", "Hears what you say while you dictate – never otherwise."), mic),
                model));
    }

    public static string ModelDetail(App app)
    {
        var engine = app.Engine;
        return app.Model switch
        {
            ModelState.Missing => Loc.T($"{engine.ModelName}, {engine.SizeText}. Laddas ned en gång och fungerar sedan offline.",
                                        $"{engine.ModelName}, {engine.SizeText}. Downloads once, then works offline."),
            ModelState.Downloading when SpeechModel.IsPreparing(app.EngineProgress) =>
                Loc.T("Förbereder modellen för din dator…", "Preparing the model for your PC…"),
            ModelState.Downloading => Loc.T($"Laddar ned {engine.ModelName} – bara den här gången.", $"Downloading {engine.ModelName} – just this once."),
            ModelState.Loading => Loc.T("Startar – första gången kan det ta upp till en minut.", "Starting – the first time can take up to a minute."),
            ModelState.Failed => app.ModelError ?? "",
            _ => Loc.T("Ljudet raderas direkt efteråt. Ingen AI skriver om dina ord.", "The audio is deleted right after. No AI rewrites your words."),
        };
    }
}

/// <summary>The language model's state in a row: name when ready, Download, progress, or Starting.</summary>
public sealed partial class ModelControl : Grid
{
    private readonly App _app;
    private ModelState? _shown;
    private ProgressBar? _bar;
    private TextBlock? _percent;

    public ModelControl(App app)
    {
        _app = app;
        HorizontalAlignment = HorizontalAlignment.Right;
        Refresh();
    }

    public void Refresh()
    {
        if (_shown == _app.Model)
        {
            if (_bar is not null) _bar.Value = _app.EngineProgress * 100;
            if (_percent is not null) _percent.Text = $"{(int)Math.Round(_app.EngineProgress * 100)} %";
            return;
        }
        _shown = _app.Model;
        _bar = null;
        _percent = null;
        Children.Clear();
        switch (_app.Model)
        {
            case ModelState.Ready:
                var name = UI.Text(_app.Engine.ModelName, 14, FontWeights.SemiBold);
                name.HorizontalAlignment = HorizontalAlignment.Right;
                var where = UI.HStack(4, UI.Icon(Icons.Lock, 10, "MutedBrush"), UI.Text(Loc.T("På din dator", "On your PC"), 12, brush: "MutedBrush"));
                where.HorizontalAlignment = HorizontalAlignment.Right;
                Children.Add(UI.VStack(3, name, where));
                break;
            case ModelState.Missing or ModelState.Failed:
                Children.Add(UI.InkButton(_app.Model == ModelState.Missing ? Loc.T("Ladda ned", "Download") : Loc.T("Försök igen", "Try Again"),
                                          () => _app.DownloadModel()));
                break;
            case ModelState.Downloading:
                _bar = new ProgressBar { Width = 110, Maximum = 100, Value = _app.EngineProgress * 100, VerticalAlignment = VerticalAlignment.Center };
                _percent = UI.Text($"{(int)Math.Round(_app.EngineProgress * 100)} %", 13, brush: "MutedBrush");
                Children.Add(UI.HStack(10, _bar, _percent, UI.SoftButton(Loc.T("Avbryt", "Cancel"), () => _app.CancelDownload())));
                break;
            default:
                Children.Add(UI.HStack(6, new ProgressRing { Width = 16, Height = 16, IsActive = true }, UI.Text(Loc.T("Startar…", "Starting…"), 13, brush: "MutedBrush")));
                break;
        }
    }
}
