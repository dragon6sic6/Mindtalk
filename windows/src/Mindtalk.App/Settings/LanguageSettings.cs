using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Speech;

namespace Mindtalk.App.Settings;

/// <summary>
/// Settings → Language: "I speak Swedish / several languages", and both speech models —
/// download, progress, remove, which one is in use. The choice applies to dictation,
/// thoughts and meetings.
/// </summary>
public sealed partial class LanguageSettings : SettingsSectionPage
{
    private readonly PillPicker<SpeechModel> _speak;
    private readonly TextBlock _speakDetail;
    private readonly StackPanel _models = new();
    private string? _modelsKey;

    public LanguageSettings(App app, MainWindow window) : base(app, window, Loc.T("Språk", "Language"))
    {
        _speak = new PillPicker<SpeechModel>([(SpeechModel.Swedish, Loc.T("Svenska", "Swedish")),
                                              (SpeechModel.Multilingual, Loc.T("Flera språk", "Several languages"))],
                                             app.Engine, Choose);
        Group(Row(Loc.T("Jag pratar", "I speak"), "", _speak, out _speakDetail));
        Group(_models);
        Note(Loc.T("Varje modell laddas ned en gång från Hugging Face, kontrolleras fil för fil och fungerar sedan offline. Byt snabbt i panelen i meddelandefältet, med Ctrl+1 och Ctrl+2.",
                   "Each model downloads once from Hugging Face, is checked file by file, and then works offline. Switch quickly in the panel in the notification area, with Ctrl+1 and Ctrl+2."));
    }

    /// Here already: use it. Not yet: download it, then use it.
    private void Choose(SpeechModel model)
    {
        if (App.IsInstalled(model)) App.SetEngine(model);
        else App.DownloadModel(model, thenUse: true);
        App.Refresh();
    }

    public override void Refresh()
    {
        var engine = App.Engine;
        var waiting = SpeechModel.All.FirstOrDefault(m => m != engine && App.Speech.DownloadProgress(m) is not null && _speak.Selection == m);
        if (waiting is null && !Equals(_speak.Selection, engine)) _speak.Selection = engine;
        UI.SetDetail(_speakDetail, waiting is not null
            ? Loc.T($"Mindtalk byter till {waiting.Title.ToLower(Loc.Culture)} när nedladdningen är klar.",
                    $"Mindtalk switches to {waiting.Title.ToLower(Loc.Culture)} when the download is done.")
            : Loc.T("Gäller diktering, tankar och möten.", "Applies to dictation, thoughts and meetings."));

        var key = string.Join("|", SpeechModel.All.Select(m =>
            $"{m.Id}:{App.IsInstalled(m)}:{App.Speech.DownloadProgress(m) is not null}:{App.Speech.DownloadError(m)}:{App.Engine == m}:{App.Model}"));
        if (key == _modelsKey)
        {
            foreach (var row in _models.Children.OfType<ModelRow>()) row.Progress();
            return;
        }
        _modelsKey = key;
        _models.Children.Clear();
        foreach (var model in SpeechModel.All)
        {
            if (_models.Children.Count > 0) _models.Children.Add(Divider());
            _models.Children.Add(new ModelRow(App, model));
        }
    }

    /// One speech model: use it, download it, or remove it.
    private sealed partial class ModelRow : Grid
    {
        private readonly App _app;
        private readonly SpeechModel _model;
        private ProgressBar? _bar;
        private TextBlock? _percent;

        public ModelRow(App app, SpeechModel model)
        {
            _app = app;
            _model = model;
            Padding = new Thickness(24, 18, 24, 18);
            ColumnSpacing = 16;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var active = app.Engine == model;

            var badge = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
            var disc = new Microsoft.UI.Xaml.Shapes.Ellipse();
            UI.Bind(disc, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, active ? "InkBrush" : "ChipBrush");
            badge.Children.Add(disc);
            badge.Children.Add(Badge(model, 14, active ? "OnInkBrush" : "MutedBrush"));
            Children.Add(badge);

            var error = app.Speech.DownloadError(model);
            var text = UI.VStack(3, UI.Text(model.Title, 15, FontWeights.SemiBold), UI.Muted(error ?? model.Pitch, 13.5));
            if (error is null)
                text.Children.Add(UI.Text(Loc.T($"{model.ModelName} av {model.Credit} · {model.SizeText}", $"{model.ModelName} by {model.Credit} · {model.SizeText}"), 12, brush: "FaintBrush"));
            text.VerticalAlignment = VerticalAlignment.Center;
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
                var inUse = Check(Loc.T("Används", "In use"));
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
    public static FrameworkElement Badge(SpeechModel model, double size, string brush)
    {
        if (model.Id == "swedish")
        {
            var code = UI.Text(model.Code, size, FontWeights.Bold, brush);
            code.HorizontalAlignment = HorizontalAlignment.Center;
            return code;
        }
        var globe = UI.Icon(Icons.Globe, size * 1.05, brush);
        globe.HorizontalAlignment = HorizontalAlignment.Center;
        globe.VerticalAlignment = VerticalAlignment.Center;
        return globe;
    }
}
