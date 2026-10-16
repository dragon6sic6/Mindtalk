using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Speech;

namespace Mindtalk.App.Pages;

/// <summary>
/// Dictation: what's missing, statistics, how to use it, and a box to try it in.
/// </summary>
public sealed partial class DictationPage : PageView
{
    private readonly Border _setup = new();
    private readonly Border _howTo = new();
    private readonly StackPanel _try;
    private readonly StatsCard _stats;
    private readonly TextBlock _placeholder;
    private string? _setupKey, _howToKey;

    public DictationPage(App app) : base(app)
    {
        Column.Spacing = 26;
        Add(UI.PageHeader(Loc.T("Diktering", "Dictation"),
            Loc.T("Prata istället för att skriva – i vilken app som helst. Allt tolkas på din dator.",
                  "Talk instead of typing – in any app. Everything is transcribed on your PC.")), 0);
        Add(_setup, 1);
        _stats = new StatsCard(app);
        Add(_stats, 1);
        Add(_howTo, 2);

        // Try it: a box to dictate into, outlined in ink once there's something in it.
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17,
            MinHeight = 130,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(box, Loc.T("Prova diktering här", "Try dictation here"));
        AutomationProperties.SetAutomationId(box, "TryBox");
        UI.Bind(box, Control.BackgroundProperty, "FieldBrush");
        UI.Bind(box, Control.BorderBrushProperty, "FieldStrokeBrush");
        _placeholder = UI.Muted("", 17, wrap: true);
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.Margin = new Thickness(17, 12, 17, 0);
        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(_placeholder);
        box.TextChanged += (_, _) =>
        {
            var empty = box.Text.Length == 0;
            _placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            box.BorderThickness = new Thickness(empty ? 1 : 1.5);
            UI.Bind(box, Control.BorderBrushProperty, empty ? "FieldStrokeBrush" : "AccentBrush");
        };
        _try = UI.VStack(10, UI.SectionTitle(Loc.T("Prova", "Try it")), field);
        Add(_try, 3);
    }

    public override void Refresh()
    {
        // The setup card: only what's still missing, each with one button.
        var setupKey = App.NeedsSetup ? $"{App.MicGranted}|{App.Model}|{App.Engine.Id}" : "";
        if (setupKey != _setupKey)
        {
            _setupKey = setupKey;
            _setup.Child = App.NeedsSetup ? SetupCard() : null;
            _setup.Visibility = App.NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
        }
        _modelControl?.Refresh();
        if (_modelDetail is not null) UI.SetDetail(_modelDetail, ModelDetail(App));

        var hotkey = App.Settings.Hotkey;
        var howToKey = $"{hotkey}|{App.Settings.Mode}";
        if (howToKey != _howToKey)
        {
            _howToKey = howToKey;
            _howTo.Child = HowTo();
        }
        _try.Visibility = App.NeedsSetup ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Text = Loc.T($"Klicka här, håll in {hotkey.InlineName} och säg något.", $"Click here, hold {hotkey.InlineName} and say something.");
        _stats.Refresh();
    }

    // ---- Get started --------------------------------------------------------------

    private ModelControl? _modelControl;
    private TextBlock? _modelDetail;

    private StackPanel SetupCard()
    {
        _modelControl = new ModelControl(App);
        var mic = App.MicGranted
            ? (UIElement)UI.GrantedLabel(Loc.T("Tillåten", "Allowed"))
            : UI.InkButton(Loc.T("Tillåt", "Allow"), App.RequestMicrophone);
        var engine = App.Engine.Title.ToLower(Loc.Culture);
        var model = UI.Row(Loc.T($"Språkmodell – {engine}", $"Speech model – {engine}"), ModelDetail(App), _modelControl, out var detail);
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

    // ---- How to ---------------------------------------------------------------------

    private Border HowTo()
    {
        var hotkey = App.Settings.Hotkey;
        var mode = App.Settings.Mode;
        UIElement Chip(string verb) => UI.HStack(6, UI.Text(verb, 13, brush: "MutedBrush"), UI.KeyChip(hotkey.Chip));
        return UI.Rows(
            Step(UI.Icon(Icons.HandTap, 16, "MutedBrush"), Loc.T("Håll in och prata", "Hold and talk"),
                 Loc.T("Släpp när du är klar – texten skrivs där markören står.", "Let go when you’re done – the text is typed where your cursor is."),
                 Chip(Loc.T("Håll", "Hold"))),
            mode == DictationMode.Hold ? null : Step(UI.Icon(Icons.Lock, 16, "MutedBrush"), Loc.T("Handsfree", "Hands-free"),
                 mode == DictationMode.Toggle
                     ? Loc.T("Tryck en gång för att börja, en gång till för att klistra in.", "Press once to start, once more to paste.")
                     : Loc.T("Dubbeltryck – eller mellanslag medan du håller in – för att låsa. Tryck igen när du är klar.",
                             "Double-tap – or press Space while holding – to lock. Press again when you’re done."),
                 Chip(mode == DictationMode.Toggle ? Loc.T("Tryck", "Press") : Loc.T("Dubbeltryck", "Double-tap"))),
            Step(UI.Icon(Icons.Escape, 16, "MutedBrush"), Loc.T("Ångra dig", "Cancel anytime"),
                 Loc.T("Avbryter utan att något skrivs.", "Cancels without typing anything."), UI.KeyChip("esc")));
    }

    private static Grid Step(UIElement icon, string title, string detail, UIElement trailing)
    {
        var grid = new Grid { Padding = new Thickness(22, 16, 22, 16), ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        var text = UI.VStack(3, UI.Text(title, 14, FontWeights.SemiBold), UI.Muted(detail, 13));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)trailing, 2);
        grid.Children.Add(trailing);
        return grid;
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
