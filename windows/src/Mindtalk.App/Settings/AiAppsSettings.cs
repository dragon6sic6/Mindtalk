using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.AI;

namespace Mindtalk.App.Settings;

/// <summary>
/// Settings → AI apps: let Claude, ChatGPT, Cursor… read your meetings and thoughts (MCP) —
/// off by default, read-only, every read logged — how to connect each app, the command line,
/// and the log.
/// </summary>
public sealed partial class AiAppsSettings : SettingsSectionPage
{
    private readonly ToggleButton _switch;
    private readonly TextBlock _switchDetail;
    private readonly StackPanel _setups = new() { Spacing = 16 };
    private readonly StackPanel _log = new();
    private IAiApps? _apps;
    private string? _setupsKey, _logKey;

    public AiAppsSettings(App app, MainWindow window) : base(app, window, Loc.T("AI-appar", "AI apps"))
    {
        _apps = SettingsServices.AiApps(app);
        _switch = UI.Switch(Loc.T("Koppla Claude eller ChatGPT", "Connect Claude or ChatGPT"), _apps?.Enabled ?? false, on =>
        {
            SettingsServices.AiApps(App)?.SetEnabled(on);
            App.Refresh();
        });
        Group(Row(Loc.T("Koppla Claude eller ChatGPT (MCP)", "Connect Claude or ChatGPT (MCP)"), "", _switch, out _switchDetail),
              Promise(Icons.Lock, Loc.T("De kan bara läsa. Inget ändras eller tas bort.", "Read-only. Nothing is changed or deleted.")),
              Promise(Icons.Font, Loc.T("Dina dikteringar delas inte.", "Your dictations are not shared.")),
              Promise(Icons.Ear, Loc.T("Ljud och röstavtryck delas aldrig.", "Audio and voiceprints are never shared.")),
              Promise(Icons.Clock, Loc.T("Ett möte går inte att läsa medan det spelas in eller de två första minuterna efteråt.",
                                         "A meeting can't be read while it's being recorded or in the two minutes after.")),
              Promise(Icons.Book, Loc.T("Varje läsning syns i loggen här nedanför.", "Every read is logged, below.")));
        Note(Loc.T("Text lämnar din dator bara på det här sättet, och bara om du frågar din AI-assistent själv. Då får den appens leverantör texten den läste.",
                   "Text leaves your PC only this way, and only when you ask your AI assistant yourself. That app's provider then receives the text it read."));

        Heading(Loc.T("Koppla en app", "Connect an app"));
        Add(_setups, 3);

        Heading(Loc.T("Kommandoraden", "The command line"),
                Loc.T("Samma brytare och samma logg gäller.", "Under the same switch and the same log."));
        const string commands = "mindtalk meetings --json\nmindtalk search \"budget\"\nmindtalk thoughts\nmindtalk tasks --json";
        Group(Setup(null, Loc.T("Lista möten, sök i dem, läs tankar och uppgifter. Med --json för andra program.",
                                "List meetings, search them, read thoughts and tasks. With --json for other programs."), commands));

        Heading(Loc.T("Loggen", "The log"));
        Group(_log);

        Loaded += (_, _) => { if (_apps is not null) _apps.Changed += OnChanged; };
        Unloaded += (_, _) => { if (_apps is not null) _apps.Changed -= OnChanged; };
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);

    /// One of the promises: an icon and a sentence, no control.
    private static Grid Promise(string glyph, string text)
    {
        var row = new Grid { Padding = new Thickness(24, 12, 24, 12), ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = UI.Icon(glyph, 14, "MutedBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(icon);
        var words = UI.Text(text, 13.5, wrap: true);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return row;
    }

    public override void Refresh()
    {
        _apps = SettingsServices.AiApps(App);
        var enabled = _apps?.Enabled ?? false;
        _switch.IsChecked = enabled;
        Disabled(_switch, _apps is null);
        UI.SetDetail(_switchDetail, _apps is null
            ? Loc.T("Kommer i en senare version av Mindtalk för Windows.", "Comes in a later version of Mindtalk for Windows.")
            : Loc.T("Claude, Claude Code, ChatGPT (Codex), Cursor och andra MCP-appar på datorn kan söka i och läsa dina möten och tankar. Avstängt från början.",
                    "Claude, Claude Code, ChatGPT (Codex), Cursor and other MCP apps on this PC can search and read your meetings and thoughts. Off by default."));
        RefreshSetups(enabled);
        RefreshLog();
    }

    private void RefreshSetups(bool enabled)
    {
        var setups = _apps?.Setups ?? [];
        var key = string.Join("|", setups.Select(s => s.Name + s.Config)) + enabled;
        if (key == _setupsKey) return;
        _setupsKey = key;
        _setups.Children.Clear();
        if (setups.Count == 0)
        {
            _setups.Children.Add(Card(Row(Loc.T("Inga appar att koppla än", "No apps to connect yet"),
                Loc.T("När MCP finns här visas hur du kopplar Claude, ChatGPT och Cursor.", "When MCP is here, this shows how to connect Claude, ChatGPT and Cursor."), null)));
            return;
        }
        foreach (var setup in setups)
        {
            var card = Card(Setup(setup.Name, setup.Instructions, setup.Config));
            // Until it's turned on, connecting would only find the door shut.
            card.Opacity = enabled ? 1 : 0.55;
            _setups.Children.Add(card);
        }
    }

    /// An app's name, what to do, and what to paste — with Copy.
    private Grid Setup(string? name, string instructions, string config)
    {
        var column = new StackPanel { Spacing = 8 };
        if (name is not null) column.Children.Add(UI.Text(name, 15, FontWeights.SemiBold));
        column.Children.Add(UI.Muted(instructions, 13.5));
        var code = new TextBlock
        {
            Text = config,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        UI.Bind(code, TextBlock.ForegroundProperty, "PrimaryBrush");
        var copy = UI.SoftButton(Loc.T("Kopiera", "Copy"), () => { });
        copy.Click += async (_, _) =>
        {
            App.Copy(config);
            copy.Content = Loc.T("Kopierat", "Copied");
            await Task.Delay(1500);
            copy.Content = Loc.T("Kopiera", "Copy");
        };
        copy.VerticalAlignment = VerticalAlignment.Top;
        var block = new Grid { ColumnSpacing = 12 };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        block.Children.Add(code);
        Grid.SetColumn(copy, 1);
        block.Children.Add(copy);
        var field = new Border { Padding = new Thickness(14, 10, 10, 10), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = block, Margin = new Thickness(0, 4, 0, 0) };
        UI.Bind(field, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(field, Border.BorderBrushProperty, "FieldStrokeBrush");
        column.Children.Add(field);
        return new Grid { Padding = new Thickness(24, 18, 24, 20), Children = { column } };
    }

    private void RefreshLog()
    {
        var log = _apps?.Log ?? [];
        var key = string.Join("|", log.Take(50).Select(e => $"{e.When.Ticks}{e.Client}{e.Action}{e.What}")) + (_apps is null);
        if (key == _logKey) return;
        _logKey = key;
        _log.Children.Clear();
        if (log.Count == 0)
        {
            _log.Children.Add(Row(Loc.T("Inget har lästs", "Nothing has been read"),
                Loc.T("När en AI-app eller kommandoraden läser något syns det här: vem, vad och när.",
                      "When an AI app or the command line reads something, it shows here: who, what and when."), null));
            return;
        }
        foreach (var entry in log.Take(50))
        {
            if (_log.Children.Count > 0) _log.Children.Add(Divider());
            var title = $"{entry.Client} · {entry.Action}";
            var when = UI.Text(When(entry.When), 12.5, brush: "MutedBrush");
            var row = Row(title, entry.What, when);
            row.Padding = new Thickness(24, 12, 24, 12);
            row.MinHeight = 0;
            _log.Children.Add(row);
        }
        _log.Children.Add(Divider());
        _log.Children.Add(Row(Loc.T($"{log.Count} {(log.Count == 1 ? "läsning" : "läsningar")}", $"{log.Count} {(log.Count == 1 ? "read" : "reads")}"), null,
                              UI.SoftButton(Loc.T("Rensa loggen", "Clear Log"), () => _apps?.ClearLog())));
    }

    private static string When(DateTime when)
    {
        var time = when.ToString("t", Loc.Culture);
        if (when.Date == DateTime.Today) return time;
        if (when.Date == DateTime.Today.AddDays(-1)) return Loc.T($"I går {time}", $"Yesterday {time}");
        return when.ToString(Loc.IsSwedish ? "d MMM HH:mm" : "MMM d, h:mm tt", Loc.Culture);
    }
}
