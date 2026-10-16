using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Interop;
using Mindtalk.Core;
using Mindtalk.Core.Speech;
using Mindtalk.Platform;
using Windows.Graphics;

namespace Mindtalk.App;

/// <summary>Setup and settings: speech model, shortcut, microphone, text options, and a box to try it in.</summary>
public sealed partial class MainWindow : Window
{
    private readonly App _app;
    private readonly Dictionary<string, (TextBlock Detail, ProgressBar Progress, Button Action, Button Cancel)> _cards = [];
    private List<Microphone> _microphones = [];
    private bool _loading = true;
    private bool _picking;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;

        Title = "Mindtalk";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(App.IconPath);
        var scale = Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(640 * scale), (int)(860 * scale)));
        // Closing hides the window; Mindtalk keeps running in the tray.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            if (_picking) StopPicking();
            AppWindow.Hide();
        };

        Tagline.Text = Loc.T("Prata. Vi skriver.", "Talk. We'll type.");
        ModelHeader.Text = Loc.T("Språkmodell", "Speech model");
        ShortcutHeader.Text = Loc.T("Kortkommando", "Shortcut");
        HotkeyLabel.Text = Loc.T("Diktera", "Dictate");
        MicrophoneHeader.Text = Loc.T("Mikrofon", "Microphone");
        TextHeader.Text = Loc.T("Text", "Text");
        FillersSwitch.Header = Loc.T("Ta bort utfyllnadsljud (eh, öh, um)", "Remove filler sounds (eh, um, uh)");
        CommandsSwitch.Header = Loc.T("Röstkommandon: ”ny rad”, ”nytt stycke”", "Voice commands: “new line”, “new paragraph”");
        ClipboardSwitch.Header = Loc.T("Behåll texten i urklipp", "Keep text in clipboard");
        LoginSwitch.Header = Loc.T("Öppna vid inloggning", "Open at login");
        TryHeader.Text = Loc.T("Prova", "Try it");
        Credits.Text = Loc.T(
            "Taligenkänning: Klang Pianissimo av Klang AI AB och Parakeet Ultra av Moondream, båda byggda på NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0). Allt körs på din dator – inget ljud lämnar den.",
            "Speech recognition: Klang Pianissimo by Klang AI AB and Parakeet Ultra by Moondream, both built on NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0). Everything runs on your PC — no audio leaves it.");

        foreach (var mode in Enum.GetValues<DictationMode>()) ModeButtons.Items.Add(AppSettings.ModeTitle(mode));
        ModeButtons.SelectedIndex = (int)app.Settings.Mode;
        FillersSwitch.IsOn = app.Settings.RemoveFillers;
        CommandsSwitch.IsOn = app.Settings.VoiceCommands;
        ClipboardSwitch.IsOn = app.Settings.KeepInClipboard;
        LoginSwitch.IsOn = App.OpensAtLogin;

        foreach (var model in SpeechModel.All) ModelCards.Children.Add(Card(model));
        LoadMicrophones();
        _loading = false;

        app.Changed += () => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    public void ShowAndActivate()
    {
        LoadMicrophones();
        AppWindow.Show();
        Activate();
        Win32.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // ---- Speech models ------------------------------------------------------------

    private Border Card(SpeechModel model)
    {
        var title = new TextBlock { Text = $"{model.Title} – {model.ModelName}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var detail = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var progress = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var action = new Button { MinWidth = 110 };
        action.Click += (_, _) =>
        {
            if (_app.Speech.IsInstalled(model)) _app.UseModel(model);
            else _app.Download(model);
        };
        var cancel = new Button { Content = Loc.T("Avbryt", "Cancel"), Visibility = Visibility.Collapsed };
        cancel.Click += (_, _) => _app.Speech.CancelDownload(model);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(cancel);
        buttons.Children.Add(action);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        _cards[model.Id] = (detail, progress, action, cancel);
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = grid };
    }

    private void Refresh()
    {
        var speech = _app.Speech;
        Status.Message = _app.StatusLine();
        Status.Severity = speech.Status.Phase switch
        {
            ModelPhase.Ready => InfoBarSeverity.Success,
            ModelPhase.Failed => InfoBarSeverity.Error,
            ModelPhase.Missing => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        Status.Title = speech.Status.Phase == ModelPhase.Failed ? speech.Status.Error ?? "" : "";

        foreach (var model in SpeechModel.All)
        {
            var (detail, progress, action, cancel) = _cards[model.Id];
            var installed = speech.IsInstalled(model);
            var current = speech.Status.Model == model;
            var downloading = speech.DownloadProgress(model);
            var pitch = model == SpeechModel.Swedish
                ? Loc.T("Bäst på svenska – hänger med i engelska också.", "Best at Swedish — keeps up with English too.")
                : Loc.T("25 språk, bland annat svenska, engelska, tyska och franska.", "25 languages, including English, Swedish, German and French.");
            detail.Text = speech.DownloadError(model) is { } error
                ? error
                : $"{pitch} {model.Credit} · {model.DownloadBytes / 1_000_000} MB";

            progress.Visibility = downloading is null ? Visibility.Collapsed : Visibility.Visible;
            progress.Value = downloading ?? 0;
            cancel.Visibility = downloading is null ? Visibility.Collapsed : Visibility.Visible;
            action.Visibility = downloading is null ? Visibility.Visible : Visibility.Collapsed;
            action.Content = installed
                ? current
                    ? speech.Status.Phase == ModelPhase.Loading ? Loc.T("Startar…", "Starting…") : Loc.T("Används", "In use")
                    : Loc.T("Använd", "Use")
                : Loc.T("Ladda ned", "Download");
            action.IsEnabled = !(installed && current);
            action.Style = !installed || !current ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        }

        if (!_picking) HotkeyButton.Content = _app.Settings.Hotkey.Name;
        HotkeyNote.Text = _app.Settings.Hotkey.Note;
        TryBox.PlaceholderText = Loc.T($"Klicka här, håll in {_app.Settings.Hotkey.Name.ToLowerInvariant()} och prata.",
                                       $"Click here, hold {_app.Settings.Hotkey.Name} and speak.");
    }

    // ---- Shortcut -------------------------------------------------------------------

    private void OnPickHotkey(object sender, RoutedEventArgs e)
    {
        if (_picking) { StopPicking(); return; }
        _picking = true;
        HotkeyButton.Content = Loc.T("Tryck på tangenten…", "Press the key…");
        HotkeyNote.Text = Loc.T("Håll in en eller flera modifierartangenter och släpp, eller tryck på en vanlig tangent. Esc avbryter.",
                                "Hold one or more modifier keys and let go, or press a regular key. Esc cancels.");
        _app.PickHotkey(_ =>
        {
            _picking = false;
            Refresh();
        });
    }

    private void StopPicking()
    {
        _picking = false;
        _app.StopPickingHotkey();
        Refresh();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeButtons.SelectedIndex < 0) return;
        _app.Settings.Mode = (DictationMode)ModeButtons.SelectedIndex;
        _app.SaveSettings();
    }

    // ---- Microphone -----------------------------------------------------------------

    private void LoadMicrophones()
    {
        var wasLoading = _loading;
        _loading = true;
        try { _microphones = WasapiRecorder.List().ToList(); }
        catch { _microphones = []; }
        MicrophonePicker.Items.Clear();
        MicrophonePicker.Items.Add(Loc.T("Systemets standardmikrofon", "System default microphone"));
        foreach (var microphone in _microphones) MicrophonePicker.Items.Add(microphone.Name);
        var index = _microphones.FindIndex(m => m.Id == _app.Settings.Microphone);
        MicrophonePicker.SelectedIndex = index + 1;
        _loading = wasLoading;
    }

    private void OnMicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicrophonePicker.SelectedIndex < 0) return;
        _app.Settings.Microphone = MicrophonePicker.SelectedIndex == 0 ? "system" : _microphones[MicrophonePicker.SelectedIndex - 1].Id;
        _app.SaveSettings();
    }

    // ---- Text -----------------------------------------------------------------------

    private void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, LoginSwitch)) { App.OpensAtLogin = LoginSwitch.IsOn; return; }
        _app.Settings.RemoveFillers = FillersSwitch.IsOn;
        _app.Settings.VoiceCommands = CommandsSwitch.IsOn;
        _app.Settings.KeepInClipboard = ClipboardSwitch.IsOn;
        _app.SaveSettings();
    }
}
