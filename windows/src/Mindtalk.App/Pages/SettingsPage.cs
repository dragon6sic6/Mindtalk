using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Platform;

namespace Mindtalk.App.Pages;

/// <summary>Settings: shortcuts, text, app, languages, microphone, about.</summary>
public sealed partial class SettingsPage : PageView
{
    private readonly MainWindow _window;
    private readonly TextBlock _shortcutDetail, _modeDetail, _mediaDetail, _polishDetail, _micDetail, _languageDetail;
    private readonly ShortcutField _shortcut;
    private readonly Button _restart;
    private readonly ContentControl _polishControl = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly ContentControl _micControl = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _languages = new();
    private readonly FrameworkElement _about;
    private string? _languagesKey;
    private PolishStatus? _polishShown;
    private bool? _micShown;

    public SettingsPage(App app, MainWindow window) : base(app)
    {
        _window = window;
        Column.Spacing = 14;
        var settings = app.Settings;
        var header = UI.PageHeader(Loc.T("Inställningar", "Settings"));
        header.Margin = new Thickness(0, 0, 0, 12);
        Add(header, 0);

        // ---- Shortcuts
        Add(UI.SectionTitle(Loc.T("Genvägar", "Shortcuts")), 1);
        _shortcut = new ShortcutField(app);
        var mode = new MenuPicker<DictationMode>(Enum.GetValues<DictationMode>().Select(m => (m, AppSettings.ModeTitle(m))).ToArray(),
                                                 settings.Mode, app.SetMode);
        Add(UI.Rows(
            UI.Row(Loc.T("Dikteringsgenväg", "Dictation shortcut"), "", _shortcut, out _shortcutDetail),
            UI.Row(Loc.T("Sätt att diktera", "How to dictate"), "", mode, out _modeDetail),
            UI.Row(Loc.T("Klistra in senaste igen", "Paste last dictation again"),
                   Loc.T("Om texten hamnade fel – ställ markören rätt och tryck.", "If the text landed in the wrong place – put the cursor where it belongs and press."),
                   UI.KeyChip("Ctrl Alt V"))), 1);

        // ---- Text
        Add(UI.SectionTitle(Loc.T("Text", "Text")), 2);
        Add(UI.Rows(
            UI.Row(Loc.T("Ta bort tvekljud", "Remove filler sounds"), Loc.T("Eh, öh och um försvinner ur texten.", "Um, uh and er are left out of the text."),
                   UI.Switch(Loc.T("Ta bort tvekljud", "Remove filler sounds"), settings.RemoveFillers, on => { settings.RemoveFillers = on; app.SaveSettings(); })),
            UI.Row(Loc.T("Röstkommandon", "Voice commands"),
                   Loc.T("Säg ”ny rad” eller ”nytt stycke” – på engelska ”new line” och ”new paragraph”.",
                         "Say “new line” or “new paragraph” – in Swedish “ny rad” and “nytt stycke”."),
                   UI.Switch(Loc.T("Röstkommandon", "Voice commands"), settings.VoiceCommands, on => { settings.VoiceCommands = on; app.SaveSettings(); })),
            UI.Row(Loc.T("Lämna texten i urklipp", "Keep text in clipboard"),
                   Loc.T("Texten skrivs in som vanligt och ligger sedan kvar i urklipp, redo för Ctrl+V. Utan markör i ett textfält hamnar den alltid där.",
                         "Text is pasted as usual and then stays in the clipboard, ready for Ctrl+V. With no cursor in a text field, it always goes there."),
                   UI.Switch(Loc.T("Lämna texten i urklipp", "Keep text in clipboard"), settings.KeepInClipboard, on => { settings.KeepInClipboard = on; app.SaveSettings(); })),
            UI.Row(Loc.T("Putsa med AI", "Polish with AI"), "", _polishControl, out _polishDetail)), 2);

        // ---- App
        Add(UI.SectionTitle(Loc.T("App", "App")), 2);
        var updates = UI.HStack(12, UI.SoftButton(Loc.T("Sök nu", "Check Now"), app.Updates.CheckNow),
                                UI.Switch(Loc.T("Sök automatiskt", "Check automatically"), app.Updates.Automatic, on => app.Updates.Automatic = on));
        var media = new MenuPicker<MediaMode>(Enum.GetValues<MediaMode>().Select(m => (m, AppSettings.MediaTitle(m))).ToArray(),
                                              settings.MediaMode, m => { settings.MediaMode = m; app.SaveSettings(); });
        _restart = UI.InkButton(Loc.T("Starta om", "Restart"), () => app.Relaunch(Page.Settings));
        var language = new PillPicker<AppLanguage>(Enum.GetValues<AppLanguage>().Select(l => (l, AppSettings.LanguageTitle(l))).ToArray(),
                                                    settings.Language, l => { settings.Language = l; app.SaveSettings(); });
        var appearance = new PillPicker<Appearance>([(Appearance.Light, AppSettings.AppearanceTitle(Appearance.Light)),
                                                     (Appearance.Dark, AppSettings.AppearanceTitle(Appearance.Dark)),
                                                     (Appearance.System, AppSettings.AppearanceTitle(Appearance.System))],
                                                    settings.Appearance, app.SetAppearance);
        Add(UI.Rows(
            UI.Row(Loc.T("Starta vid inloggning", "Open at login"), null,
                   UI.Switch(Loc.T("Starta vid inloggning", "Open at login"), App.OpensAtLogin, on => App.OpensAtLogin = on)),
            UI.Row(Loc.T("Uppdateringar", "Updates"), Loc.T("Sök efter nya versioner automatiskt, en gång om dagen.", "Check for new versions automatically, once a day."), updates),
            UI.Row(Loc.T("Visa i aktivitetsfältet", "Show in taskbar"),
                   Loc.T("Annars finns Mindtalk bara i meddelandefältet när fönstret är stängt.", "Otherwise Mindtalk is only in the notification area when the window is closed."),
                   UI.Switch(Loc.T("Visa i aktivitetsfältet", "Show in taskbar"), settings.ShowInTaskbar, on => { settings.ShowInTaskbar = on; app.SaveSettings(); })),
            UI.Row(Loc.T("Ljud vid diktering", "Dictation sounds"),
                   Loc.T("Ett diskret ljud när Mindtalk börjar lyssna och när texten klistras in.", "A subtle sound when Mindtalk starts listening and when the text is pasted."),
                   UI.Switch(Loc.T("Ljud vid diktering", "Dictation sounds"), settings.Sounds, on => { settings.Sounds = on; app.SaveSettings(); })),
            UI.Row(Loc.T("Musik medan du dikterar", "Music while you dictate"), "", media, out _mediaDetail),
            UI.Row(Loc.T("Appens språk", "App language"), null, UI.HStack(10, _restart, language), out _languageDetail),
            UI.Row(Loc.T("Utseende", "Appearance"), null, appearance)), 2);

        // ---- Languages
        Add(UI.SectionTitle(Loc.T("Språk", "Language")), 3);
        Add(UI.Card(_languages), 3);
        Column.Children.Add(UI.Text(Loc.T("Byt snabbt i aktivitetsfältet – eller med Ctrl+1 och Ctrl+2 när panelen är öppen.",
                                          "Switch quickly from the taskbar – or with Ctrl+1 and Ctrl+2 while the panel is open."), 12, brush: "MutedBrush", wrap: true));

        // ---- Microphone (Windows has no Accessibility permission to ask for)
        Add(UI.SectionTitle(Loc.T("Behörigheter och ljud", "Permissions and audio")), 4);
        Add(UI.Rows(UI.Row(Loc.T("Mikrofon", "Microphone"), "", _micControl, out _micDetail)), 4);

        // ---- About
        Add(UI.SectionTitle(Loc.T("Om Mindtalk", "About Mindtalk")), 5);
        _about = AboutCard();
        Add(_about, 5);
    }

    public override void Refresh()
    {
        var settings = App.Settings;
        _shortcut.Refresh();
        UI.SetDetail(_shortcutDetail, App.PickingKey
            ? Loc.T("Håll in en eller flera modifierartangenter tillsammans och släpp för att spara. Du kan också trycka på en vanlig tangent. Esc avbryter.",
                    "Hold one or more modifier keys together, then release to save. You can also press a regular key. Esc cancels.")
            : settings.Hotkey.Note);
        UI.SetDetail(_modeDetail, AppSettings.ModeExplanation(settings.Mode));
        UI.SetDetail(_mediaDetail, AppSettings.MediaExplanation(settings.MediaMode));

        // The app's language: a choice other than the running one needs a restart.
        var differs = Loc.Resolve(settings.Language) != (Loc.IsSwedish ? AppLanguage.Swedish : AppLanguage.English);
        _restart.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        _restart.IsEnabled = App.Dictation.State.Phase == Phase.Idle;
        UI.SetDetail(_languageDetail, differs ? Loc.T("Mindtalk startar om för att byta språk.", "Mindtalk restarts to switch language.") : null);

        RefreshPolish();
        RefreshMicrophone();
        RefreshLanguages();
    }

    // ---- Polish with AI ---------------------------------------------------------------

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
        toggle.IsEnabled = status == PolishStatus.Available;
        toggle.Opacity = status == PolishStatus.Available ? 1 : 0.4;
        _polishControl.Content = toggle;
    }

    // ---- Microphone -----------------------------------------------------------------

    private void RefreshMicrophone()
    {
        UI.SetDetail(_micDetail, App.MicGranted
            ? App.Microphones.SelectionName(App.Settings.Microphone)
            : Loc.T("Mindtalk behöver få använda mikrofonen.", "Mindtalk needs permission to use the microphone."));
        if (_micShown == App.MicGranted) return;
        _micShown = App.MicGranted;
        _micControl.Content = App.MicGranted
            ? UI.SoftButton(Loc.T("Ändra", "Change"), () => _window.ShowSheet(new MicrophonePicker(App, _window.CloseSheet)))
            : UI.InkButton(Loc.T("Tillåt", "Allow"), App.RequestMicrophone);
    }

    // ---- Languages --------------------------------------------------------------------

    private void RefreshLanguages()
    {
        var key = string.Join("|", SpeechModel.All.Select(m =>
            $"{m.Id}:{App.IsInstalled(m)}:{App.Speech.DownloadProgress(m) is not null}:{App.Speech.DownloadError(m)}:{App.Engine == m}:{App.Model}"));
        if (key == _languagesKey)
        {
            foreach (var row in _languages.Children.OfType<LanguageRow>()) row.Progress();
            return;
        }
        _languagesKey = key;
        _languages.Children.Clear();
        foreach (var model in SpeechModel.All)
        {
            if (_languages.Children.Count > 0) _languages.Children.Add(UI.Divider());
            _languages.Children.Add(new LanguageRow(App, model));
        }
    }

    /// <summary>One language: use it, download it, or remove it.</summary>
    private sealed partial class LanguageRow : Grid
    {
        private readonly App _app;
        private readonly SpeechModel _model;
        private ProgressBar? _bar;
        private TextBlock? _percent;

        public LanguageRow(App app, SpeechModel model)
        {
            _app = app;
            _model = model;
            Padding = new Thickness(22, 16, 22, 16);
            ColumnSpacing = 16;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var active = app.Engine == model;

            var badge = new Grid { Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center };
            var disc = new Microsoft.UI.Xaml.Shapes.Ellipse();
            UI.Bind(disc, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, active ? "AccentTintBrush" : "ChipBrush");
            badge.Children.Add(disc);
            badge.Children.Add(Badge(model, 13, active ? "AccentBrush" : "MutedBrush"));
            Children.Add(badge);

            var error = app.Speech.DownloadError(model);
            var text = UI.VStack(3, UI.Text(model.Title, 14, FontWeights.SemiBold), UI.Muted(error ?? model.Pitch, 13));
            if (error is null)
                text.Children.Add(UI.Text(Loc.T($"{model.ModelName} av {model.Credit} · {model.SizeText}", $"{model.ModelName} by {model.Credit} · {model.SizeText}"), 11.5, brush: "MutedBrush"));
            Grid.SetColumn(text, 1);
            Children.Add(text);

            var control = Control(active);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            Children.Add(control);
        }

        private FrameworkElement Control(bool active)
        {
            if (_app.Speech.DownloadProgress(_model) is { } p)
            {
                _bar = new ProgressBar { Width = 90, Maximum = 100, Value = p * 100, VerticalAlignment = VerticalAlignment.Center };
                _percent = UI.Text($"{(int)Math.Round(p * 100)} %", 12, brush: "MutedBrush");
                return UI.HStack(10, _bar, _percent, UI.SoftButton(Loc.T("Avbryt", "Cancel"), () => _app.CancelDownload(_model)));
            }
            if (!_app.IsInstalled(_model))
                return UI.InkButton(_app.Speech.DownloadError(_model) is null ? Loc.T("Ladda ned", "Download") : Loc.T("Försök igen", "Try Again"),
                                    () => _app.DownloadModel(_model));
            if (active)
            {
                var inUse = UI.HStack(6, UI.Icon(Icons.CheckCircleFill, 13, "AccentBrush"), UI.Text(Loc.T("Används", "In use"), 13, FontWeights.SemiBold, "AccentBrush"));
                if (_app.Model == ModelState.Loading) inUse.Children.Insert(0, new ProgressRing { Width = 14, Height = 14, IsActive = true });
                return inUse;
            }
            var trash = UI.PlainButton(UI.Icon(Icons.Trash, 13, "MutedBrush"), ConfirmRemove,
                                       Loc.T($"Ta bort {_model.ModelName} ({_model.SizeText}) från din dator", $"Remove {_model.ModelName} ({_model.SizeText}) from your PC"));
            return UI.HStack(8, trash, UI.SoftButton(Loc.T("Använd", "Use"), () => _app.SetEngine(_model)));
        }

        public void Progress()
        {
            if (_app.Speech.DownloadProgress(_model) is not { } p || _bar is null || _percent is null) return;
            _bar.Value = p * 100;
            _percent.Text = $"{(int)Math.Round(p * 100)} %";
        }

        private async void ConfirmRemove()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = UI.Theme,
                Title = Loc.T($"Ta bort {_model.ModelName} ({_model.SizeText})?", $"Remove {_model.ModelName} ({_model.SizeText})?"),
                Content = Loc.T("Den kan laddas ned igen senare.", "You can download it again later."),
                PrimaryButtonText = Loc.T("Ta bort", "Remove"),
                CloseButtonText = Loc.T("Avbryt", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) _app.RemoveModel(_model);
        }
    }

    /// The language model as a small mark: "SV" for Swedish, a globe for the multilingual one
    /// (a two-letter code there would suggest a single language).
    public static FrameworkElement Badge(SpeechModel model, double size, string brush, bool serif = false)
    {
        if (model.Id == "swedish")
        {
            var code = UI.Text(model.Code, size, serif ? FontWeights.Normal : FontWeights.Bold, brush);
            if (serif) code.FontFamily = UI.Serif;
            code.HorizontalAlignment = HorizontalAlignment.Center;
            return code;
        }
        var globe = UI.Icon(Icons.Globe, size * (serif ? 0.95 : 1.05), brush);
        globe.HorizontalAlignment = HorizontalAlignment.Center;
        globe.VerticalAlignment = VerticalAlignment.Center;
        return globe;
    }

    // ---- About --------------------------------------------------------------------

    /// Credits and licences — what CC BY 4.0 asks of us, said plainly.
    private Border AboutCard()
    {
        const string ccBy = "https://creativecommons.org/licenses/by/4.0/deed.sv";
        var rows = new List<UIElement>
        {
            AboutRow($"Mindtalk {App.Version}", "© 2026 Mindact Solutions AB",
                     UI.SoftButton(Loc.T("Visa introduktionen", "Show Introduction"), App.ShowIntroduction)),
        };
        foreach (var m in SpeechModel.All)
            rows.Add(AboutRow(m.ModelName, Loc.T($"Av {m.Credit}, byggd på NVIDIA Parakeet. CC BY 4.0.", $"By {m.Credit}, built on NVIDIA Parakeet. CC BY 4.0."),
                              Links((Loc.T("Modell", "Model"), m.ModelPage), ($"ONNX ({m.ConversionCredit})", m.ConversionPage), (Loc.T("Licens", "License"), ccBy))));
        rows.Add(AboutRow("NVIDIA Parakeet TDT 0.6B v3", Loc.T("Grundmodellen båda bygger på, av NVIDIA. CC BY 4.0.", "The base model both build on, by NVIDIA. CC BY 4.0."),
                          Links((Loc.T("Modell", "Model"), "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"), (Loc.T("Licens", "License"), ccBy))));
        rows.Add(AboutRow("ONNX Runtime", Loc.T("Kör modellerna på din dator. Av Microsoft, MIT-licens.", "Runs the models on your PC. By Microsoft, MIT license."),
                          Links((Loc.T("Källkod", "Source"), "https://github.com/microsoft/onnxruntime"))));
        rows.Add(AboutRow("NAudio", Loc.T("Spelar in från mikrofonen. Av Mark Heath, MIT-licens.", "Records from the microphone. By Mark Heath, MIT license."),
                          Links((Loc.T("Källkod", "Source"), "https://github.com/naudio/NAudio"))));
        rows.Add(AboutRow(Loc.T("Licenser", "Licenses"), Loc.T("Alla licenstexter som följer med Mindtalk.", "All license texts that come with Mindtalk."),
                          UI.SoftButton(Loc.T("Visa licenser…", "Show Licenses…"), () => _window.ShowSheet(LicensesSheet()))));
        return UI.Rows(rows.ToArray());
    }

    private static Grid AboutRow(string title, string detail, UIElement trailing)
    {
        var grid = new Grid { Padding = new Thickness(22, 14, 22, 14), ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UI.VStack(3, UI.Text(title, 14, FontWeights.SemiBold), UI.Muted(detail, 12.5)));
        Grid.SetColumn((FrameworkElement)trailing, 1);
        ((FrameworkElement)trailing).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(trailing);
        return grid;
    }

    private static StackPanel Links(params (string Title, string Url)[] links) =>
        UI.HStack(12, links.Select(l => (UIElement)UI.Link(l.Title, l.Url)).ToArray());

    /// The licence texts that come with the app (Assets/Acknowledgements.txt).
    private FrameworkElement LicensesSheet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Acknowledgements.txt");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var done = UI.InkButton(Loc.T("Klar", "Done"), _window.CloseSheet);
        var bar = new Grid { Padding = new Thickness(20) };
        bar.Children.Add(UI.Text(Loc.T("Licenser", "Licenses"), 20, FontWeights.SemiBold));
        done.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(done);
        var body = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
        };
        UI.Bind(body, TextBlock.ForegroundProperty, "PrimaryBrush");
        var sheet = new Grid { Width = 680, Height = 560 };
        sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sheet.Children.Add(bar);
        var divider = UI.Divider();
        Grid.SetRow(divider, 1);
        sheet.Children.Add(divider);
        var scroll = new ScrollViewer { Content = body };
        Grid.SetRow(scroll, 2);
        sheet.Children.Add(scroll);
        return sheet;
    }

    /// Settings scrolled to "About Mindtalk".
    public void ScrollToAbout() => DispatcherQueue.TryEnqueue(async () =>
    {
        await Task.Delay(450);
        var at = _about.TransformToVisual(Column).TransformPoint(default).Y;
        Scroll.ChangeView(null, Math.Max(0, at - 40), null);
    });
}

/// <summary>A dictation shortcut in a white field — choose a key or hold modifiers together.</summary>
public sealed partial class ShortcutField : Button
{
    private readonly App _app;
    private readonly Border _face;
    private readonly ContentControl _content = new() { IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };

    public ShortcutField(App app)
    {
        _app = app;
        Style = UI.Style("PlainButtonStyle");
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_content);
        var pencil = UI.Icon(Icons.Pencil, 13, "MutedBrush");
        Grid.SetColumn(pencil, 1);
        row.Children.Add(pencil);
        _face = new Border
        {
            Width = 280,
            MinHeight = 38,
            Padding = new Thickness(8, 0, 12, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = row,
        };
        UI.Bind(_face, Border.BackgroundProperty, "FieldBrush");
        Content = _face;
        ToolTipService.SetToolTip(this, Loc.T("Klicka och håll in önskade modifierartangenter tillsammans. Släpp för att spara. Du kan också trycka på en vanlig tangent. Esc avbryter.",
                                              "Click and hold the modifier keys you want together. Release to save. You can also press a regular key. Esc cancels."));
        Click += (_, _) => { if (_app.PickingKey) _app.StopPickingKey(); else _app.PickKey(); };
        Refresh();
    }

    public void Refresh()
    {
        UI.Bind(_face, Border.BorderBrushProperty, _app.PickingKey ? "InkBrush" : "FieldStrokeBrush");
        _content.Content = _app.PickingKey
            ? UI.Text(Loc.T("Tangent eller modifierare…", "Key or modifiers…"), 13, brush: "MutedBrush")
            : UI.KeyChip(_app.Settings.Hotkey.Chip);
    }
}
