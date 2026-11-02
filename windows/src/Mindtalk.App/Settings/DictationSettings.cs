using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.App.Pages;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App.Settings;

/// <summary>
/// Settings → Dictation: how you dictate, what happens to the text, the microphone, music
/// and sound while you talk, and your vocabulary.
/// </summary>
public sealed partial class DictationSettings : SettingsSectionPage
{
    private readonly TextBlock _modeDetail, _mediaDetail, _polishDetail, _micDetail, _vocabularyDetail;
    private readonly ContentControl _polishControl = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly ContentControl _micControl = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
    private PolishStatus? _polishShown;
    private bool? _micShown;

    public DictationSettings(App app, MainWindow window) : base(app, window, Loc.T("Diktering", "Dictation"))
    {
        var settings = app.Settings;

        var mode = new MenuPicker<DictationMode>(Enum.GetValues<DictationMode>().Select(m => (m, AppSettings.ModeTitle(m))).ToArray(),
                                                 settings.Mode, app.SetMode);
        Group(Row(Loc.T("Sätt att diktera", "How to dictate"), "", mode, out _modeDetail));

        Heading(Loc.T("Text", "Text"));
        Group(SwitchRow(Loc.T("Ta bort tvekljud", "Remove filler sounds"), Loc.T("Eh, öh och um försvinner ur texten.", "Um, uh and er are left out of the text."),
                        settings.RemoveFillers, on => settings.RemoveFillers = on),
              SwitchRow(Loc.T("Röstkommandon", "Voice commands"),
                        Loc.T("Säg ”ny rad” eller ”nytt stycke” – på engelska ”new line” och ”new paragraph”.",
                              "Say “new line” or “new paragraph” – in Swedish “ny rad” and “nytt stycke”."),
                        settings.VoiceCommands, on => settings.VoiceCommands = on),
              SwitchRow(Loc.T("Behåll texten i urklipp", "Keep text on the clipboard"),
                        Loc.T("Texten skrivs in som vanligt och ligger sedan kvar i urklipp, redo för Ctrl+V. Utan markör i ett textfält hamnar den alltid där.",
                              "Text is pasted as usual and then stays on the clipboard, ready for Ctrl+V. With no cursor in a text field, it always goes there."),
                        settings.KeepInClipboard, on => settings.KeepInClipboard = on),
              Row(Loc.T("Putsa med AI", "Polish with AI"), "", _polishControl, out _polishDetail));

        Heading(Loc.T("Ljud", "Sound"));
        var media = new MenuPicker<MediaMode>(Enum.GetValues<MediaMode>().Select(m => (m, AppSettings.MediaTitle(m))).ToArray(),
                                              settings.MediaMode, m => { settings.MediaMode = m; app.SaveSettings(); });
        Group(Row(Loc.T("Mikrofon", "Microphone"), "", _micControl, out _micDetail),
              Row(Loc.T("Musik och ljud medan du pratar", "Music and sound while you talk"), "", media, out _mediaDetail));

        Heading(Loc.T("Ordlista", "Vocabulary"));
        Group(Row(Loc.T("Ord och namn", "Words and names"), "",
                  UI.SoftButton(Loc.T("Öppna ordlistan", "Open Vocabulary"), () => Window.ShowPage(Page.Vocabulary)), out _vocabularyDetail));
    }

    public override void Refresh()
    {
        var settings = App.Settings;
        UI.SetDetail(_modeDetail, AppSettings.ModeExplanation(settings.Mode) + " " +
                                  Loc.T("Esc avbryter utan att skriva något.", "Esc cancels without typing anything."));
        UI.SetDetail(_mediaDetail, AppSettings.MediaExplanation(settings.MediaMode) +
                                   (settings.MediaMode == MediaMode.Off ? "" : Loc.T(" Ett samtal sänks men tystas aldrig.", " A call is turned down but never muted.")));
        var words = App.Vocabulary.Entries.Count;
        UI.SetDetail(_vocabularyDetail, Loc.T(
            $"Lär Mindtalk namn och termer, i diktering, tankar, möten och sammanfattningar. {words} ord nu.",
            $"Teach Mindtalk names and terms, in dictation, thoughts, meetings and summaries. {words} {(words == 1 ? "word" : "words")} now."));
        RefreshPolish();
        RefreshMicrophone();
    }

    private void RefreshPolish()
    {
        var status = App.Polisher.Status;
        if (status == _polishShown) return;
        _polishShown = status;
        UI.SetDetail(_polishDetail, status switch
        {
            PolishStatus.Available => Loc.T("Rättar skiljetecken, upprepningar och självrättelser som ”nej, jag menar…”. Körs med Windows egen AI-modell på din dator – inget skickas iväg.",
                                            "Fixes punctuation, repeated words and self-corrections like “no, I mean…”. Runs with Windows’ own AI model on your PC – nothing is sent anywhere."),
            PolishStatus.NotEnabled => Loc.T("Kräver Windows AI-modell, som är avstängd på den här datorn.", "Requires Windows’ AI model, which is turned off on this PC."),
            PolishStatus.NotReady => Loc.T("Windows AI-modell laddas ned. Det går att slå på när det är klart.", "Windows’ AI model is downloading. You can turn this on when it’s done."),
            _ => Loc.T("Kräver Windows AI-modell (Copilot+ PC), som inte finns på den här datorn.", "Requires Windows’ AI model (Copilot+ PC), which isn’t available on this PC."),
        });
        if (status == PolishStatus.NotEnabled)
        {
            _polishControl.Content = UI.SoftButton(Loc.T("Slå på", "Turn On"),
                () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-systemaimodels")));
            return;
        }
        var toggle = UI.Switch(Loc.T("Putsa med AI", "Polish with AI"), App.Settings.AiPolish && status == PolishStatus.Available, on =>
        {
            App.Settings.AiPolish = on;
            App.SaveSettings();
            if (on) App.Polisher.Prepare();
        });
        _polishControl.Content = Disabled(toggle, status != PolishStatus.Available);
    }

    private void RefreshMicrophone()
    {
        UI.SetDetail(_micDetail, App.MicGranted
            ? App.Microphones.SelectionName(App.Settings.Microphone) + " · " +
              Loc.T("Inbyggd som standard, även med hörlurar i Bluetooth.", "Built-in by default, also with Bluetooth headphones.")
            : Loc.T("Mindtalk behöver få använda mikrofonen.", "Mindtalk needs permission to use the microphone."));
        if (_micShown == App.MicGranted) return;
        _micShown = App.MicGranted;
        _micControl.Content = App.MicGranted
            ? UI.SoftButton(Loc.T("Välj…", "Choose…"), () => Window.ShowSheet(new MicrophonePicker(App, Window.CloseSheet)))
            : UI.InkButton(Loc.T("Tillåt", "Allow"), App.RequestMicrophone);
    }
}
