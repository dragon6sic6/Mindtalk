using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Input;
using Mindtalk.Core.Speech;

namespace Mindtalk.App.Settings;

/// <summary>
/// Settings → General: you, your shortcuts, how Mindtalk looks and starts, updates, and
/// about (credits and licences, the introduction again).
/// </summary>
public sealed partial class GeneralSettings : SettingsSectionPage
{
    private readonly ShortcutField _dictation, _thought, _meeting, _pasteLast;
    private readonly TextBlock _dictationDetail, _thoughtDetail, _meetingDetail, _pasteDetail, _languageDetail, _updateTitle, _updateDetail;
    private readonly Button _restart;
    private readonly Border _about;

    public GeneralSettings(App app, MainWindow window) : base(app, window, Loc.T("Allmänt", "General"))
    {
        var settings = app.Settings;

        // ---- You
        var name = new TextBox
        {
            Style = UI.Style("FieldStyle"),
            Width = 220,
            Text = settings.UserName ?? "",
            PlaceholderText = Loc.T("Förnamn", "First name"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, Loc.T("Ditt namn", "Your name"));
        name.TextChanged += (_, _) =>
        {
            var value = name.Text.Trim();
            settings.UserName = value.Length == 0 ? null : value;
            settings.Save();
        };
        name.LostFocus += (_, _) => app.Refresh();
        Group(Row(Loc.T("Ditt namn", "Your name"),
                  Loc.T("Hem hälsar dig med det, och i möten känner Mindtalk igen dig som du.",
                        "Home greets you with it, and in meetings Mindtalk knows you by it."), name));

        // ---- Shortcuts
        Heading(Loc.T("Genvägar", "Shortcuts"));
        _dictation = ShortcutField.Dictation(app);
        _thought = ShortcutField.Thought(app);
        _meeting = ShortcutField.AppWide(app, () => settings.MeetingShortcut, s => settings.MeetingShortcut = s, () => settings.PasteLastShortcut);
        _pasteLast = ShortcutField.AppWide(app, () => settings.PasteLastShortcut, s => settings.PasteLastShortcut = s, () => settings.MeetingShortcut);
        Group(Row(Loc.T("Diktering", "Dictation"), "", _dictation, out _dictationDetail),
              Row(Loc.T("Säg en tanke", "Say a thought"), "", _thought, out _thoughtDetail),
              Row(Loc.T("Starta ett möte", "Start a meeting"), "", _meeting, out _meetingDetail),
              Row(Loc.T("Klistra in senaste dikteringen", "Paste last dictation"), "", _pasteLast, out _pasteDetail));

        // ---- How Mindtalk looks and starts
        Heading(Loc.T("Appen", "The app"));
        var appearance = new PillPicker<Appearance>([(Appearance.Light, AppSettings.AppearanceTitle(Appearance.Light)),
                                                     (Appearance.Dark, AppSettings.AppearanceTitle(Appearance.Dark)),
                                                     (Appearance.System, AppSettings.AppearanceTitle(Appearance.System))],
                                                    settings.Appearance, app.SetAppearance);
        _restart = UI.InkButton(Loc.T("Starta om", "Restart"), () => app.Relaunch(Page.Settings));
        var language = new PillPicker<AppLanguage>(Enum.GetValues<AppLanguage>().Select(l => (l, AppSettings.LanguageTitle(l))).ToArray(),
                                                    settings.Language, l => { settings.Language = l; app.SaveSettings(); });
        Group(Row(Loc.T("Utseende", "Appearance"), Loc.T("Ljust är standard. System följer Windows.", "Light is the default. System follows Windows."), appearance),
              Row(Loc.T("Appens språk", "App language"), null, UI.HStack(10, _restart, language), out _languageDetail),
              Row(Loc.T("Starta vid inloggning", "Open at login"),
                  Loc.T("Mindtalk startar med Windows, så att tangenten alltid fungerar.", "Mindtalk starts with Windows, so the key always works."),
                  UI.Switch(Loc.T("Starta vid inloggning", "Open at login"), App.OpensAtLogin, on => App.OpensAtLogin = on)),
              SwitchRow(Loc.T("Visa i aktivitetsfältet", "Show in taskbar"),
                        Loc.T("Annars finns Mindtalk bara i meddelandefältet när fönstret är stängt.", "Otherwise Mindtalk is only in the notification area when the window is closed."),
                        settings.ShowInTaskbar, on => settings.ShowInTaskbar = on),
              SwitchRow(Loc.T("Ljud", "Sounds"),
                        Loc.T("Ett diskret ljud när Mindtalk börjar lyssna och när texten klistras in.", "A subtle sound when Mindtalk starts listening and when the text is pasted."),
                        settings.Sounds, on => settings.Sounds = on));

        // ---- Updates
        Heading(Loc.T("Uppdateringar", "Updates"));
        var check = UI.SoftButton(Loc.T("Sök nu", "Check Now"), app.Updates.CheckNow);
        Group(Row(Loc.T("Sök automatiskt", "Check automatically"),
                  Loc.T("En gång om dagen. En uppdatering väntar tills ett möte som spelas in är klart.",
                        "Once a day. An update waits until a meeting being recorded is over."),
                  UI.Switch(Loc.T("Sök automatiskt", "Check automatically"), app.Updates.Automatic, on => app.Updates.Automatic = on)),
              VersionRow(check, out _updateTitle, out _updateDetail));

        // ---- About
        Heading(Loc.T("Om Mindtalk", "About Mindtalk"));
        _about = Group(Row(Loc.T("Tack och licenser", "Credits and licenses"),
                           Loc.T("Mindtalk görs av Mindact Solutions AB i Sverige. Modellerna har egna licenser.",
                                 "Mindtalk is made by Mindact Solutions AB in Sweden. The models keep their own licenses."),
                           UI.SoftButton(Loc.T("Tack och licenser…", "Credits and Licenses…"), () => Window.ShowSheet(new CreditsSheet(Window.CloseSheet)))),
                       Row(Loc.T("Visa introduktionen igen", "Show the introduction again"),
                           Loc.T("Sju korta steg: språk, behörigheter, din tangent, tankar och möten.",
                                 "Seven short steps: language, permissions, your key, thoughts and meetings."),
                           UI.SoftButton(Loc.T("Visa introduktionen", "Show Introduction"), App.ShowIntroduction)));
    }

    private static Grid VersionRow(Button check, out TextBlock title, out TextBlock detail)
    {
        var row = Row($"Mindtalk {App.Version}", "", check, out detail);
        title = (TextBlock)((StackPanel)row.Children[0]).Children[0];
        return row;
    }

    public override void Refresh()
    {
        var settings = App.Settings;
        foreach (var field in new[] { _dictation, _thought, _meeting, _pasteLast }) field.Refresh();

        UI.SetDetail(_dictationDetail, _dictation.Recording
            ? Loc.T("Håll in en eller flera modifierartangenter tillsammans och släpp för att spara. Du kan också trycka på en vanlig tangent. Esc avbryter.",
                    "Hold one or more modifier keys together, then release to save. You can also press a regular key. Esc cancels.")
            : Loc.T($"Håll in för att prata. {settings.Hotkey.Note}", $"Hold to talk. {settings.Hotkey.Note}"));
        UI.SetDetail(_thoughtDetail, ThoughtDetail());
        UI.SetDetail(_meetingDetail, _meeting.Problem ?? Loc.T("Startar och avslutar ett möte, var du än är.", "Starts and ends a meeting, wherever you are."));
        UI.SetDetail(_pasteDetail, _pasteLast.Problem ?? Loc.T("Hamnade texten fel? Ställ markören rätt och tryck.", "Landed in the wrong place? Put the cursor right and press."));

        // The app's language: a choice other than the running one needs a restart.
        var differs = Loc.Resolve(settings.Language) != (Loc.IsSwedish ? AppLanguage.Swedish : AppLanguage.English);
        _restart.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        _restart.IsEnabled = App.Dictation.State.Phase == Core.Dictation.Phase.Idle && !App.IsRecordingMeeting;
        UI.SetDetail(_languageDetail, differs ? Loc.T("Mindtalk startar om för att byta språk.", "Mindtalk restarts to switch language.") : null);

        _updateTitle.Text = $"Mindtalk {App.Version}";
        UI.SetDetail(_updateDetail, App.Updates.Available is { } version
            ? Loc.T($"Version {version} finns.", $"Version {version} is available.")
            : Loc.T("Varje uppdatering är signerad.", "Every update is signed."));
    }

    private string ThoughtDetail()
    {
        var settings = App.Settings;
        if (_thought.Recording)
            return Loc.T("Håll in en eller flera modifierartangenter tillsammans och släpp för att spara. De kan inte delas med dikteringen. Esc avbryter.",
                         "Hold one or more modifier keys together, then release to save. They can't be shared with dictation. Esc cancels.");
        if (App.ThoughtKeyProblem is { } problem) return problem;
        if (settings.ThoughtHotkey is not { } key)
            return Loc.T("Av. Klicka i fältet för att välja tangenter.", "Off. Click the field to choose keys.");
        return Loc.T($"Håll in {key.InlineName}, säg vad du tänker och släpp. Tanken sparas under Tankar.",
                     $"Hold {key.InlineName}, say what’s on your mind and let go. It’s saved on the Thoughts page.");
    }

    /// Scrolled to "About Mindtalk" (credits and licences).
    public void ScrollToAbout() => DispatcherQueue.TryEnqueue(async () =>
    {
        await Task.Delay(450);
        var at = _about.TransformToVisual(Column).TransformPoint(default).Y;
        Scroll.ChangeView(null, Math.Max(0, at - 80), null);
    });
}

/// <summary>Credits and licences — what CC BY 4.0 and the others ask of us, said plainly.</summary>
public sealed partial class CreditsSheet : Grid
{
    private const string CcBy = "https://creativecommons.org/licenses/by/4.0/";

    public CreditsSheet(Action close)
    {
        Width = 640;
        Height = 600;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Padding = new Thickness(28, 24, 24, 12) };
        var title = new TextBlock { Text = Loc.T("Tack och licenser", "Credits and Licenses"), FontFamily = UI.Serif, FontSize = 30 };
        UI.Bind(title, TextBlock.ForegroundProperty, "PrimaryBrush");
        bar.Children.Add(title);
        var done = UI.InkButton(Loc.T("Klar", "Done"), close);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(done);
        Children.Add(bar);

        var column = new StackPanel { Spacing = 14, Padding = new Thickness(28, 4, 28, 28) };
        column.Children.Add(UI.Muted($"Mindtalk {App.Version} · © 2026 Mindact Solutions AB", 13));
        var rows = new List<UIElement>();
        foreach (var m in SpeechModel.All)
            rows.Add(Credit(m.ModelName, Loc.T($"Taligenkänning av {m.Credit}, byggd på NVIDIA Parakeet. CC BY 4.0.", $"Speech recognition by {m.Credit}, built on NVIDIA Parakeet. CC BY 4.0."),
                            (Loc.T("Modell", "Model"), m.ModelPage), ($"ONNX ({m.ConversionCredit})", m.ConversionPage), (Loc.T("Licens", "License"), CcBy)));
        rows.Add(Credit("NVIDIA Parakeet TDT 0.6B v3", Loc.T("Grundmodellen båda bygger på, av NVIDIA. CC BY 4.0.", "The base model both build on, by NVIDIA. CC BY 4.0."),
                        (Loc.T("Modell", "Model"), "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"), (Loc.T("Licens", "License"), CcBy)));
        rows.Add(Credit("Gemma 4", Loc.T("Sammanfattningar och svar, om du väljer den. Av Google DeepMind. Apache 2.0.", "Summaries and answers, if you choose it. By Google DeepMind. Apache 2.0."),
                        (Loc.T("Modell", "Model"), "https://huggingface.co/google/gemma-4-E4B-it")));
        rows.Add(Credit("ONNX Runtime", Loc.T("Kör modellerna på din dator. Av Microsoft, MIT-licens.", "Runs the models on your PC. By Microsoft, MIT license."),
                        (Loc.T("Källkod", "Source"), "https://github.com/microsoft/onnxruntime")));
        rows.Add(Credit("NAudio", Loc.T("Spelar in från mikrofonen. Av Mark Heath, MIT-licens.", "Records from the microphone. By Mark Heath, MIT license."),
                        (Loc.T("Källkod", "Source"), "https://github.com/naudio/NAudio")));
        column.Children.Add(SettingsSectionPage.Card(rows.ToArray()));

        var licenses = UI.Text(Loc.T("Licenstexter", "License texts"), 15, FontWeights.SemiBold);
        licenses.Margin = new Thickness(0, 8, 0, 0);
        column.Children.Add(licenses);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Acknowledgements.txt");
        var body = new TextBlock
        {
            Text = File.Exists(path) ? File.ReadAllText(path) : "",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        UI.Bind(body, TextBlock.ForegroundProperty, "MutedBrush");
        column.Children.Add(body);
        var scroll = new ScrollViewer { Content = column };
        SetRow(scroll, 1);
        Children.Add(scroll);
    }

    private static Grid Credit(string title, string detail, params (string Title, string Url)[] links)
    {
        var text = UI.VStack(4, UI.Text(title, 14, FontWeights.SemiBold), UI.Muted(detail, 12.5),
                             UI.HStack(12, links.Select(l => (UIElement)UI.Link(l.Title, l.Url)).ToArray()));
        return new Grid { Padding = new Thickness(22, 14, 22, 14), Children = { text } };
    }
}
