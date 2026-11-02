using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.AI;
using Mindtalk.Core.Input;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Settings;

/// <summary>
/// Settings → Meetings, as the Mac's: starting one and calls, how long audio is kept, voices
/// Mindtalk remembers, the speaker model, and who writes summaries and answers.
/// The meeting engine and the writers may not be there (yet): the rows then show what they
/// would do, greyed out.
/// </summary>
public sealed partial class MeetingsSettings : SettingsSectionPage
{
    private readonly ShortcutField _meeting;
    private readonly TextBlock _meetingDetail, _keepDetail, _diskDetail, _speakerDetail;
    private readonly ToggleButton _remember;
    private readonly StackPanel _voices = new() { Tag = SettingsSectionPage.OwnDividers };
    private readonly ContentControl _speakerControl = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _writers = new();
    private readonly Dictionary<WriterKind, IReadOnlyList<string>> _localModels = [];
    private string? _voicesKey, _speakerKey, _writersKey;
    private int _diskCount = -1;
    private readonly List<(WriterKind Kind, ProgressBar Bar, TextBlock Percent)> _writerProgress = [];
    private IWriterService? _writerService;
    private IMeetingService? _meetingService;

    public MeetingsSettings(App app, MainWindow window) : base(app, window, Loc.T("Möten", "Meetings"))
    {
        var settings = app.Settings;
        _writerService = SettingsServices.Writers(app);
        _meetingService = SettingsServices.Meetings(app);
#if DEBUG
        // Screenshots: --demo-voices
        if (Demo.On && Demo.Has("--demo-voices") && app.Voices.All.Count == 0)
            foreach (var name in new[] { "Anna", "Erik", "Linnéa" }) app.Voices.Remember(name, Enumerable.Range(0, 192).Select(_ => Random.Shared.NextSingle() - 0.5f).ToArray());
#endif

        // ---- Starting one, and calls
        _meeting = ShortcutField.AppWide(app, () => settings.MeetingShortcut, s => settings.MeetingShortcut = s, () => settings.PasteLastShortcut);
        var calls = new MenuPicker<CallAudio>([(CallAudio.WithHeadphones, Loc.T("Med hörlurar", "With headphones")),
                                               (CallAudio.Always, Loc.T("Alltid", "Always")),
                                               (CallAudio.Never, Loc.T("Aldrig", "Never"))],
                                              settings.CallAudio, c => { settings.CallAudio = c; app.SaveSettings(); });
        Group(Row(Loc.T("Starta ett möte", "Start a meeting"), "", _meeting, out _meetingDetail),
              SwitchRow(Loc.T("Visa panelen när du lämnar Mindtalk", "Show the panel when you leave Mindtalk"),
                        Loc.T("Under ett möte syns panelen ovanpå det du jobbar med. Den går undan när du kommer tillbaka.",
                              "During a meeting, the panel shows over whatever you are working in. It goes away when you come back."),
                        settings.ShowMeetingPanel, on => settings.ShowMeetingPanel = on),
              Row(Loc.T("Ta med samtal på datorn", "Include calls on this PC"),
                  Loc.T("Mindtalk hör de andra i Teams, Zoom, Meet och andra samtalsappar genom datorns ljud. Det behövs med hörlurar. Med högtalare hör mikrofonen dem redan.",
                        "Mindtalk hears the others in Teams, Zoom, Meet and other call apps through PC audio. It's needed with headphones. With speakers, the microphone hears them already."),
                  calls),
              SwitchRow(Loc.T("Fråga när ett samtal börjar", "Ask when a call starts"),
                        Loc.T("När Teams, Zoom, Meet eller en annan samtalsapp börjar använda mikrofonen frågar Mindtalk i en liten ruta om du vill anteckna mötet. Inget spelas in förrän du trycker.",
                              "When Teams, Zoom, Meet or another call app starts using the microphone, Mindtalk asks in a small box whether you want to take notes. Nothing is recorded until you press."),
                        settings.AskWhenCallStarts, on => settings.AskWhenCallStarts = on));

        // ---- Audio on this PC
        var keep = new MenuPicker<KeepAudio>([(KeepAudio.Always, Loc.T("Alltid", "Always")),
                                              (KeepAudio.Days90, Loc.T("90 dagar", "90 days")),
                                              (KeepAudio.Days30, Loc.T("30 dagar", "30 days")),
                                              (KeepAudio.Days7, Loc.T("7 dagar", "7 days"))],
                                             settings.KeepAudio, Keep);
        Group(Row(Loc.T("Behåll ljudet", "Keep audio"), "", keep, out _keepDetail),
              Row(Loc.T("På den här datorn", "On this PC"), "", UI.SoftButton(Loc.T("Visa i Utforskaren", "Show in Explorer"), ShowFolder), out _diskDetail));

        // ---- Voices
        _remember = UI.Switch(Loc.T("Kom ihåg röster", "Remember voices"), settings.RememberVoices, Remember);
        Group(Row(Loc.T("Kom ihåg röster", "Remember voices"),
                  Loc.T("Namnge en röst en gång, så känner Mindtalk igen den. Inget ljud sparas. Fråga de andra först.",
                        "Name a voice once and Mindtalk will know it. No audio is kept. Ask the others first."), _remember),
              SwitchRow(Loc.T("Börja med en namnrunda", "Start with a round of names"),
                        Loc.T("Var och en säger ”Hej, jag heter …”, och varje namn hamnar på rösten som sa det. Du kan välja varje gång.",
                              "Everyone says “Hi, my name is …”, and each name lands on the voice that said it. You can choose each time."),
                        settings.RoundOfNamesFirst, on => settings.RoundOfNamesFirst = on),
              _voices,
              Row(Loc.T("Talarmodellen", "The speaker model"), "", _speakerControl, out _speakerDetail));

        // ---- Summaries and answers
        Heading(Loc.T("Sammanfattningar och svar", "Summaries and answers"),
                Loc.T("Vem som skriver sammanfattningar, titlar och svar. Allt stannar på din dator.",
                      "Who writes summaries, titles and answers. Everything stays on your PC."));
        Group(_writers);

        Loaded += (_, _) =>
        {
            if (_writerService is not null) _writerService.Changed += OnServiceChanged;
            if (_meetingService is not null) _meetingService.Changed += OnServiceChanged;
            LoadLocalModels();
        };
        Unloaded += (_, _) =>
        {
            if (_writerService is not null) _writerService.Changed -= OnServiceChanged;
            if (_meetingService is not null) _meetingService.Changed -= OnServiceChanged;
        };
    }

    // Services may say so from any thread.
    private void OnServiceChanged() => DispatcherQueue.TryEnqueue(Refresh);

    public override void Refresh()
    {
        var settings = App.Settings;
        _writerService = SettingsServices.Writers(App);
        _meetingService = SettingsServices.Meetings(App);
        _meeting.Refresh();
        UI.SetDetail(_meetingDetail, _meeting.Problem ?? Loc.T("Startar och avslutar ett möte, var du än är.", "Starts and ends a meeting, wherever you are."));
        UI.SetDetail(_keepDetail, settings.KeepAudio switch
        {
            KeepAudio.Always => Loc.T("Ljudet sparas så länge mötet finns. Texten sparas alltid.", "The audio is kept as long as the meeting is. The text is always kept."),
            var keep => Loc.T($"Ljudet tas bort efter {Days(keep)} dagar. Texten sparas alltid.", $"The audio is deleted after {Days(keep)} days. The text is always kept."),
        });
        RefreshDisk();
        RefreshVoices();
        RefreshSpeakerModel();
        RefreshWriters();
    }

    private static int Days(KeepAudio keep) => keep switch { KeepAudio.Days90 => 90, KeepAudio.Days30 => 30, _ => 7 };

    // ---- Audio ----------------------------------------------------------------------

    private void Keep(KeepAudio keep)
    {
        App.Settings.KeepAudio = keep;
        App.SaveSettings();
        // Older audio goes now, as it would at the next start.
        var meetings = App.Meetings;
        Task.Run(() => meetings.ApplyRetention(keep, DateTime.Now));
    }

    private void RefreshDisk()
    {
        var count = App.Meetings.Count;
        if (count == _diskCount) return;
        _diskCount = count;
        var bytes = App.Meetings.SizeOnDisk();
        var size = ShortcutRules.Size(bytes);
        UI.SetDetail(_diskDetail, count == 0
            ? Loc.T("Inga möten än.", "No meetings yet.")
            : Loc.T($"{count} {(count == 1 ? "möte" : "möten")} som tar {size}.", $"{count} {(count == 1 ? "meeting" : "meetings")}, taking up {size}."));
    }

    private void ShowFolder()
    {
        var root = App.Meetings.Root;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    // ---- Voices ---------------------------------------------------------------------

    /// Turning it on asks for the others' consent first.
    private async void Remember(bool on)
    {
        if (on)
        {
            var yes = await Window.Ask(Loc.T("Fråga de andra först", "Ask the others first"),
                Loc.T("Mindtalk sparar ett röstavtryck – inte ljud – av varje person du namnger, på den här datorn, så att de känns igen i kommande möten. Se till att alla går med på det. Du kan glömma en person eller alla när som helst.",
                      "Mindtalk keeps a voiceprint – not audio – of each person you name, on this PC, so they're recognized in later meetings. Make sure everyone agrees. You can forget one person or everyone at any time."),
                Loc.T("Slå på", "Turn On"), Loc.T("Avbryt", "Cancel"));
            if (!yes) { _remember.IsChecked = false; return; }
        }
        App.Settings.RememberVoices = on;
        App.SaveSettings();
    }

    private void RefreshVoices()
    {
        var voices = App.Voices.All.OrderBy(v => v.Name, StringComparer.Create(Loc.Culture, true)).ToList();
        var key = string.Join("|", voices.Select(v => $"{v.Id}:{v.Name}")) + App.Settings.RememberVoices;
        if (key == _voicesKey) return;
        _voicesKey = key;
        _voices.Children.Clear();
        if (voices.Count == 0)
        {
            if (App.Settings.RememberVoices)
            {
                _voices.Children.Add(Divider());
                _voices.Children.Add(Row(Loc.T("Inga röster än", "No voices yet"),
                    Loc.T("Namnge en talare i ett möte, så sparas rösten här.", "Name a speaker in a meeting, and the voice is kept here."), null));
            }
            return;
        }
        foreach (var voice in voices)
        {
            _voices.Children.Add(Divider());
            var forget = UI.SoftButton(Loc.T("Glöm", "Forget"), () => App.Voices.Forget(voice.Id));
            var since = voice.Created.ToString(Loc.IsSwedish ? "d MMMM yyyy" : "MMMM d, yyyy", Loc.Culture);
            _voices.Children.Add(VoiceRow(voice.Name, Loc.T($"Ihågkommen sedan {since}", $"Remembered since {since}"), forget));
        }
        _voices.Children.Add(Divider());
        _voices.Children.Add(Row(Loc.T("Glöm alla", "Forget everyone"),
            Loc.T("Alla röstavtryck tas bort. Namnen i gamla möten står kvar.", "Every voiceprint is deleted. The names in past meetings stay."),
            UI.SoftButton(Loc.T("Glöm alla…", "Forget Everyone…"), ForgetEveryone)));
    }

    /// A remembered voice: a small circle with the initial, the name, since when.
    private static Grid VoiceRow(string name, string detail, UIElement control)
    {
        var initial = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, "ChipBrush");
        initial.Children.Add(disc);
        var letter = UI.Text(name.Length > 0 ? name[..1].ToUpper(Loc.Culture) : "?", 13, FontWeights.SemiBold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        initial.Children.Add(letter);
        var row = Row(name, detail, control);
        row.Padding = new Thickness(24, 12, 24, 12);
        row.MinHeight = 0;
        row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
        foreach (var child in row.Children.OfType<FrameworkElement>()) Grid.SetColumn(child, Grid.GetColumn(child) + 1);
        row.Children.Add(initial);
        return row;
    }

    private async void ForgetEveryone()
    {
        if (await Window.Ask(Loc.T("Glömma alla röster?", "Forget every voice?"),
                             Loc.T("Mindtalk känner inte igen någon i kommande möten förrän du namnger dem igen.", "Mindtalk won't recognize anyone in later meetings until you name them again."),
                             Loc.T("Glöm alla", "Forget Everyone"), Loc.T("Avbryt", "Cancel")))
            App.Voices.ForgetAll();
    }

    // ---- The speaker model ----------------------------------------------------------

    private void RefreshSpeakerModel()
    {
        var service = _meetingService;
        var progress = service?.SpeakerModelProgress;
        var key = service is null ? "none" : $"{service.SpeakerModelReady}:{progress is not null}:{service.SpeakerModelError}";
        UI.SetDetail(_speakerDetail, service switch
        {
            null => Loc.T("Håller isär upp till 8 talare. Laddas ned en gång, när möten finns på den här datorn.",
                          "Keeps up to 8 speakers apart. Downloaded once, when meetings are available on this PC."),
            { SpeakerModelError: { } error } => error,
            { SpeakerModelReady: true } => Loc.T("Håller isär upp till 8 talare. Den finns på din dator.", "Keeps up to 8 speakers apart. It's on your PC."),
            _ => Loc.T("Håller isär upp till 8 talare. Laddas ned en gång och fungerar sedan offline.", "Keeps up to 8 speakers apart. Downloaded once, then it works offline."),
        });
        if (key == _speakerKey)
        {
            if (progress is { } p && _speakerControl.Content is StackPanel { Children: [ProgressBar bar, TextBlock percent] })
            {
                bar.Value = p * 100;
                percent.Text = $"{(int)Math.Round(p * 100)} %";
            }
            return;
        }
        _speakerKey = key;
        _speakerControl.Content = service switch
        {
            null => Disabled(UI.InkButton(Loc.T("Ladda ned", "Download"), () => { })),
            { SpeakerModelReady: true } => Check(Loc.T("På datorn", "On this PC")),
            _ when progress is { } p => UI.HStack(10, new ProgressBar { Width = 90, Maximum = 100, Value = p * 100, VerticalAlignment = VerticalAlignment.Center },
                                                   UI.Text($"{(int)Math.Round(p * 100)} %", 12, brush: "MutedBrush")),
            _ => UI.InkButton(service.SpeakerModelError is null ? Loc.T("Ladda ned", "Download") : Loc.T("Försök igen", "Try Again"),
                              () => _ = service.DownloadSpeakerModelAsync()),
        };
    }

    // ---- Writers --------------------------------------------------------------------

    private async void LoadLocalModels()
    {
        if (_writerService is not { } writers) return;
        foreach (var kind in new[] { WriterKind.Ollama, WriterKind.LMStudio })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _localModels[kind] = await writers.LocalModelsAsync(kind, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
            {
                _localModels[kind] = [];
            }
        }
        _writersKey = null;
        Refresh();
    }

    /// Without the writers: every kind, as it would be, greyed out.
    private static IReadOnlyList<WriterStatus> Placeholder(string why) =>
    [
        new(WriterKind.GemmaSmall, "Gemma 4 E4B", Loc.T("6,9 GB, för 16 GB minne", "6.9 GB, for 16 GB of memory"), false, false),
        new(WriterKind.GemmaLarge, "Gemma 4 26B", Loc.T("15,6 GB, för 32 GB minne", "15.6 GB, for 32 GB of memory"), false, false),
        new(WriterKind.Ollama, "Ollama", why, false, false),
        new(WriterKind.LMStudio, "LM Studio", why, false, false),
        new(WriterKind.Windows, "Windows AI", Loc.T("Kräver en Copilot+ PC", "Needs a Copilot+ PC"), false, false),
    ];

    private void RefreshWriters()
    {
        var service = _writerService;
        var all = service?.All ?? Placeholder(Loc.T("Kommer med möten", "Comes with meetings"));
        var current = service?.Current;
        var key = string.Join("|", all.Select(s => $"{s.Kind}:{s.Available}:{s.Ready}:{s.Progress is not null}:{s.Error}:{s.Model}:{s.Detail}")) +
                  $"#{current}#{service is null}#{string.Join(",", _localModels.Select(m => m.Key + "=" + m.Value.Count))}";
        if (key == _writersKey)
        {
            foreach (var (kind, bar, percent) in _writerProgress)
                if (all.FirstOrDefault(s => s.Kind == kind)?.Progress is { } p) { bar.Value = p * 100; percent.Text = $"{(int)Math.Round(p * 100)} %"; }
            return;
        }
        _writersKey = key;
        _writers.Children.Clear();
        _writerProgress.Clear();
        foreach (var status in all)
        {
            if (_writers.Children.Count > 0) _writers.Children.Add(Divider());
            _writers.Children.Add(WriterRow(status, service, status.Kind == current));
        }
    }

    private Grid WriterRow(WriterStatus status, IWriterService? service, bool current)
    {
        var usable = service is not null && status.Available;
        var detail = status.Error ?? status.Detail;
        if (status.Kind is WriterKind.Ollama or WriterKind.LMStudio)
            detail = (status.Available ? (status.Model is { Length: > 0 } m ? m + ". " : "") : detail + ". ")
                     + Loc.T("Nås bara på 127.0.0.1.", "Reached only on 127.0.0.1.");
        if (MemoryNote(status.Kind) is { } note) detail += " " + note;

        var row = Row(status.Title, detail, WriterControl(status, service, current));
        var mark = UI.Icon(current ? Icons.CheckCircleFill : Icons.Circle, 18, current ? "AccentBrush" : "FieldStrokeBrush");
        mark.VerticalAlignment = VerticalAlignment.Center;
        row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
        foreach (var child in row.Children.OfType<FrameworkElement>()) Grid.SetColumn(child, Grid.GetColumn(child) + 1);
        row.ColumnSpacing = 16;
        row.Children.Add(mark);
        // Windows AI where Windows won't let Mindtalk use it: greyed, with its reason.
        if (!usable && status.Kind == WriterKind.Windows || service is null) row.Opacity = 0.5;
        return row;
    }

    private FrameworkElement? WriterControl(WriterStatus status, IWriterService? service, bool current)
    {
        if (service is null || !status.Available) return null;
        var kind = status.Kind;
        if (status.Progress is { } p)
        {
            var bar = new ProgressBar { Width = 90, Maximum = 100, Value = p * 100, VerticalAlignment = VerticalAlignment.Center };
            var percent = UI.Text($"{(int)Math.Round(p * 100)} %", 12, brush: "MutedBrush");
            _writerProgress.Add((kind, bar, percent));
            return UI.HStack(10, bar, percent, UI.SoftButton(Loc.T("Avbryt", "Cancel"), () => service.CancelDownload(kind)));
        }
        if (kind is WriterKind.Ollama or WriterKind.LMStudio)
        {
            var models = _localModels.GetValueOrDefault(kind) ?? [];
            if (models.Count == 0)
                return UI.SoftButton(Loc.T("Leta igen", "Look Again"), LoadLocalModels);
            var chosen = status.Model is { } m && models.Contains(m) ? m : models[0];
            var picker = new MenuPicker<string>(models.Select(x => (x, x)).ToArray(), chosen, x => service.Use(kind, x));
            return current ? UI.HStack(12, picker, Check(Loc.T("Används", "In use")))
                           : UI.HStack(8, picker, UI.SoftButton(Loc.T("Använd", "Use"), () => service.Use(kind, chosen)));
        }
        if (kind is WriterKind.GemmaSmall or WriterKind.GemmaLarge && !status.Ready)
        {
            var size = status.SizeBytes is { } bytes ? " " + ShortcutRules.Size(bytes) : "";
            return UI.InkButton(status.Error is null ? Loc.T($"Ladda ned{size}", $"Download{size}") : Loc.T("Försök igen", "Try Again"),
                                () => _ = service.DownloadAsync(kind));
        }
        if (current) return Check(Loc.T("Används", "In use"));
        var use = UI.SoftButton(Loc.T("Använd", "Use"), () => service.Use(kind));
        if (kind is not (WriterKind.GemmaSmall or WriterKind.GemmaLarge)) return Disabled(use, !status.Ready);
        var trash = UI.PlainButton(UI.Icon(Icons.Trash, 13, "MutedBrush"), () => RemoveWriter(service, status),
                                   Loc.T($"Ta bort {status.Title} från din dator", $"Remove {status.Title} from your PC"));
        return UI.HStack(8, trash, use);
    }

    private async void RemoveWriter(IWriterService service, WriterStatus status)
    {
        var size = status.SizeBytes is { } bytes ? $" ({ShortcutRules.Size(bytes)})" : "";
        if (await Window.Ask(Loc.T($"Ta bort {status.Title}{size}?", $"Remove {status.Title}{size}?"),
                             Loc.T("Den kan laddas ned igen senare.", "You can download it again later."),
                             Loc.T("Ta bort", "Remove"), Loc.T("Avbryt", "Cancel")))
            service.Remove(status.Kind);
    }

    /// Gemma wants memory: say so if this PC has less than it's made for.
    private static string? MemoryNote(WriterKind kind)
    {
        var needs = kind switch { WriterKind.GemmaSmall => 16, WriterKind.GemmaLarge => 32, _ => 0 };
        if (needs == 0) return null;
        var gb = (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_073_741_824.0);
        return gb + 1 < needs ? Loc.T($"Den här datorn har {gb} GB.", $"This PC has {gb} GB.") : null;
    }
}
