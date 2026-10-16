using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;

namespace Mindtalk.App.Onboarding;

internal enum Step { Welcome, Language, Permissions, Key, TryIt }

/// <summary>One step of the introduction: centred in the space between the segments and the footer.</summary>
internal abstract partial class StepView : Grid
{
    private readonly bool _entrance;

    protected StepView(App app, bool entrance)
    {
        App = app;
        _entrance = entrance;
    }

    protected App App { get; }

    /// Rises into place after the ones before it (not when the step is only redrawn): the
    /// Mac's .staggered — 16 points up, spring(duration 0.7, bounce 0.15), 0.08 s apart.
    /// (Motion.Stagger's composition spring runs slower than this on Windows.)
    protected T In<T>(T element, int index) where T : FrameworkElement
    {
        if (!_entrance || Motion.Reduced) return element;
        var visual = Anim.Movable(element, new Vector3(0, 16, 0));
        visual.Opacity = 0;
        var delay = index * 0.08 + 0.1;
        Anim.WhenLoaded(element, () =>
        {
            Anim.Spring(visual, "Translation", Vector3.Zero, 0.7, 0.15, delay);
            Anim.Spring(visual, "Opacity", 1f, 0.7, 0.15, delay);
        });
        return element;
    }

    /// The app changed: show it.
    public virtual void Update() { }

    /// Once a second, for "about 2 min left".
    public virtual void Tick() { }

    /// The step is going away.
    public virtual void Leave() { }

    protected StackPanel Column(double spacing)
    {
        var column = new StackPanel { Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(column);
        return column;
    }
}

// MARK: - Step 1: Welcome

internal sealed partial class WelcomeStep : StepView
{
    public WelcomeStep(App app, bool entrance) : base(app, entrance)
    {
        var column = Column(30);
        column.Padding = new Thickness(0, 0, 0, 20);
        column.Children.Add(In(new Grid { Children = { LogoMark.Make(118) } }, 0));
        column.Children.Add(new Typewriter(Loc.T("Prata. Vi skriver.", "Talk. We’ll type."), 54, delay: entrance ? 0.7 : -1));
    }
}

// MARK: - Step 2: Language

internal sealed partial class LanguageStep : StepView
{
    private readonly HashSet<SpeechModel> _chosen;
    private readonly List<LanguageCard> _cards = [];
    private readonly Action _changed;

    public LanguageStep(App app, bool entrance, HashSet<SpeechModel> chosen, Action changed) : base(app, entrance)
    {
        _chosen = chosen;
        _changed = changed;
        var column = Column(26);
        column.Children.Add(In(new StepHeader(Loc.T("Vilket språk pratar du?", "Which language do you speak?"),
            Loc.T("Välj ett eller båda. Modellen laddas ned en gång och fungerar sedan helt offline.",
                  "Pick one or both. Each model downloads once and then works fully offline.")), 0));
        var row = new Grid { ColumnSpacing = 16 };
        for (var i = 0; i < SpeechModel.All.Count; i++)
        {
            var model = SpeechModel.All[i];
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = new LanguageCard(app, model, chosen.Contains(model), () => Toggle(model));
            Grid.SetColumn(card, i);
            row.Children.Add(In(card, i + 1));
            _cards.Add(card);
        }
        column.Children.Add(row);
        var note = Paint.Centered(Loc.T("Båda bygger på NVIDIA Parakeet och körs på din dator. Du byter språk med ett klick i aktivitetsfältet.",
                                        "Both are built on NVIDIA Parakeet and run on your PC. Switch language with one click in the taskbar."),
                                  12, brush: "MutedBrush");
        note.MaxWidth = 520;
        column.Children.Add(In(note, 3));
    }

    private void Toggle(SpeechModel model)
    {
        if (_chosen.Contains(model)) { if (_chosen.Count > 1) _chosen.Remove(model); }
        else _chosen.Add(model);
        foreach (var card in _cards) card.Selected = _chosen.Contains(card.Model);
        _changed();
    }

    public override void Update()
    {
        foreach (var card in _cards) card.Update();
    }
}

internal sealed partial class LanguageCard : Button
{
    private readonly App _app;
    private readonly Raised _raised;
    private readonly Ellipse _disc = new();
    private readonly Shade _discShade;
    private readonly FrameworkElement _mark;
    private readonly FontIcon _check, _installedIcon;
    private readonly TextBlock _installed;
    private readonly Microsoft.UI.Composition.Visual _body, _checkVisual;
    private bool _selected;

    public LanguageCard(App app, SpeechModel model, bool selected, Action toggle)
    {
        _app = app;
        Model = model;
        _selected = selected;
        Style = Bare.Style;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, model.Title);

        // The model as a mark: "SV" in serif for Swedish, a globe for the multilingual one
        // (a two-letter code there would suggest a single language).
        var discHost = new Grid { IsHitTestVisible = false };
        _discShade = new Shade(discHost, 29);
        _mark = model == SpeechModel.Swedish
            ? new TextBlock { Text = model.Code, FontFamily = UI.Serif, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : UI.Icon(Icons.Globe, 26 * 0.95);
        var badge = new Grid { Width = 58, Height = 58, HorizontalAlignment = HorizontalAlignment.Left, Children = { discHost, _disc, _mark } };
        _check = UI.Icon(Icons.Circle, 22);
        _check.HorizontalAlignment = HorizontalAlignment.Right;
        _check.VerticalAlignment = VerticalAlignment.Top;
        var top = new Grid { Children = { badge, _check } };

        var titleRow = UI.HStack(8, UI.Text(model.Title, 19, FontWeights.SemiBold), model == SpeechModel.Recommended ? Recommended() : null);
        titleRow.HorizontalAlignment = HorizontalAlignment.Left;
        var pitch = UI.Text(model.Pitch, 13, brush: "MutedBrush", wrap: true);
        pitch.Margin = new Thickness(0, 5, 0, 0);
        _installedIcon = UI.Icon(Icons.Download, 11.5, "MutedBrush");
        _installed = UI.Text("", 11.5, brush: "MutedBrush");
        var install = UI.HStack(6, _installedIcon, _installed);
        install.HorizontalAlignment = HorizontalAlignment.Left;
        install.Margin = new Thickness(0, 14, 0, 0);

        var content = new Grid();
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            content.RowDefinitions.Add(new RowDefinition { Height = height });
        content.RowDefinitions[1].MinHeight = 18;
        UIElement[] rows = [top, new Grid(), titleRow, pitch, install];
        for (var i = 0; i < rows.Length; i++) { Grid.SetRow((FrameworkElement)rows[i], i); content.Children.Add(rows[i]); }

        _raised = new Raised(content) { Height = 232 };
        var body = new Grid { Children = { _raised } };
        Content = body;
        _body = Anim.Scalable(body);
        _checkVisual = Anim.Scalable(_check);

        Click += (_, _) => toggle();
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        Apply(animated: false);
        Update();
    }

    public SpeechModel Model { get; }

    public bool Selected
    {
        set
        {
            if (value == _selected) return;
            _selected = value;
            Apply(animated: true);
        }
    }

    private static Border Recommended()
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(100),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = UI.Text(Loc.T("Rekommenderas", "Recommended"), 10.5, FontWeights.SemiBold, "AccentBrush"),
        };
        UI.Bind(pill, Border.BackgroundProperty, "AccentTintBrush");
        return pill;
    }

    /// The download line: what it is, and whether it's here already.
    public void Update()
    {
        var installed = _app.IsInstalled(Model);
        _installedIcon.Glyph = installed ? Icons.CheckCircleFill : Icons.Download;
        _installed.Text = installed
            ? Loc.T($"{Model.ModelName} · redan installerad", $"{Model.ModelName} · already installed")
            : $"{Model.ModelName} · {Model.SizeText}";
    }

    private void Hover(bool on)
    {
        _raised.Hovering = on;
        // spring(duration 0.3)
        var scale = on ? 1.012f : 1;
        Anim.Spring(_body, "Scale", new Vector3(scale, scale, 1), 0.3, 0);
    }

    private void Apply(bool animated)
    {
        _raised.Selected = _selected;
        if (_selected) _disc.Fill = Paint.Accent();
        else UI.Bind(_disc, Shape.FillProperty, "ChipBrush");
        var tint = _selected ? "OnInkBrush" : "AccentBrush";
        if (_mark is TextBlock text) UI.Bind(text, TextBlock.ForegroundProperty, tint);
        else UI.Bind(_mark, IconElement.ForegroundProperty, tint);
        _discShade.Set(Paint.Of("AccentBrush"), _selected ? 0.35 : 0, 12, 5, animated ? 0.35 : 0);
        _check.Glyph = _selected ? Icons.CheckCircleFill : Icons.Circle;
        UI.Bind(_check, IconElement.ForegroundProperty, _selected ? "AccentBrush" : "MutedBrush");
        AutomationProperties.SetItemStatus(this, _selected ? Loc.T("Vald", "Selected") : "");
        if (!animated) return;
        // The check bounces as it changes (spring 0.35, bounce 0.3).
        _checkVisual.Scale = new Vector3(0.75f, 0.75f, 1);
        Anim.Spring(_checkVisual, "Scale", Vector3.One, 0.35, 0.3);
    }
}

// MARK: - Step 3: Permissions

/// <summary>
/// Windows asks for no Accessibility permission, so the second card is the one other thing
/// worth setting up front: Mindtalk opening at login, ready in the taskbar.
/// </summary>
internal sealed partial class PermissionsStep : StepView
{
    private readonly PermissionCard _mic, _login;
    private readonly Grid _status = new() { MinHeight = 18 };
    private string? _shown;

    public PermissionsStep(App app, bool entrance) : base(app, entrance)
    {
        var column = Column(26);
        column.Children.Add(In(new StepHeader(Loc.T("Två snabba inställningar", "Two quick settings"),
            Loc.T("Så att Mindtalk kan höra dig och finns till hands när du behöver det.",
                  "So Mindtalk can hear you and is there whenever you need it.")), 0));
        _mic = new PermissionCard(Icons.MicFill, Loc.T("Mikrofon", "Microphone"),
            Loc.T("Används bara medan du dikterar. Ljudet lämnar aldrig din dator.", "Only used while you dictate. The audio never leaves your PC."),
            Loc.T("Tillåt", "Allow"), Loc.T("Tillåtet", "Allowed"), app.RequestMicrophone);
        _login = new PermissionCard(Glyphs.Power, Loc.T("Starta vid inloggning", "Open at login"),
            Loc.T("Mindtalk startar med Windows och väntar i aktivitetsfältet, redo när du är det.",
                  "Mindtalk starts with Windows and waits in the taskbar, ready when you are."),
            Loc.T("Slå på", "Turn On"), Loc.T("Påslaget", "Turned on"), TurnOnLogin);
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_login, 1);
        row.Children.Add(In(_mic, 1));
        row.Children.Add(In(_login, 2));
        column.Children.Add(row);
        column.Children.Add(In(_status, 3));
        app.RefreshPermissions();
        Update();
    }

    private void TurnOnLogin()
    {
        try { global::Mindtalk.App.App.OpensAtLogin = true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        Update();
    }

    public override void Update()
    {
        var mic = App.MicGranted;
        var login = global::Mindtalk.App.App.OpensAtLogin;
        _mic.Set(mic);
        _login.Set(login);

        var shown = mic && login ? "done" : !mic ? "mic" : "";
        if (shown == _shown) return;
        var first = _shown is null;
        _shown = shown;
        _status.Children.Clear();
        FrameworkElement? line = shown switch
        {
            "done" => UI.GrantedLabel(Loc.T("Allt klart – fortsätt när du vill.", "All set – continue whenever you like.")),
            "mic" => Info(Loc.T("Slå på mikrofonåtkomst för skrivbordsappar under Sekretess och säkerhet → Mikrofon. Kortet bockas av av sig självt.",
                                "Turn on microphone access for desktop apps under Privacy & security → Microphone. The card checks itself off.")),
            _ => null,
        };
        if (line is null) return;
        line.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Children.Add(line);
        if (first) return;
        // All set springs in (scale 0.9 + opacity, spring 0.45 bounce 0.3); the hint fades.
        var visual = shown == "done" ? Anim.Scalable(line) : Anim.Of(line);
        visual.Opacity = 0;
        Anim.WhenLoaded(line, () =>
        {
            Anim.Spring(visual, "Opacity", 1f, 0.45, 0);
            if (shown != "done") return;
            visual.Scale = new Vector3(0.9f, 0.9f, 1);
            Anim.Spring(visual, "Scale", Vector3.One, 0.45, 0.3);
        });
    }

    private static StackPanel Info(string text)
    {
        var label = UI.Text(text, 12.5, FontWeights.Medium, "MutedBrush", wrap: true);
        label.TextAlignment = TextAlignment.Center;
        label.MaxWidth = 620;
        return UI.HStack(7, UI.Icon(Icons.Info, 13, "MutedBrush"), label);
    }
}

internal sealed partial class PermissionCard : Grid
{
    private readonly string _glyph, _grantedText;
    private readonly Ellipse _disc = new();
    private readonly FontIcon _icon;
    private readonly Shade _shade;
    private readonly Grid _action = new() { Height = 34 };
    private readonly Button _button;
    private readonly Microsoft.UI.Composition.Visual _badge;
    private bool? _granted;

    public PermissionCard(string glyph, string title, string text, string button, string granted, Action action)
    {
        _glyph = glyph;
        _grantedText = granted;
        var host = new Grid { IsHitTestVisible = false };
        _shade = new Shade(host, 26);
        _icon = UI.Icon(glyph, 20);
        var badge = new Grid { Width = 52, Height = 52, HorizontalAlignment = HorizontalAlignment.Left, Children = { host, _disc, _icon } };
        _badge = Anim.Scalable(badge);
        var heading = UI.Text(title, 18, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 18, 0, 0);
        var detail = UI.Text(text, 13, brush: "MutedBrush", wrap: true);
        detail.Margin = new Thickness(0, 5, 0, 0);
        _button = UI.InkButton(button, action);
        _button.HorizontalAlignment = HorizontalAlignment.Left;

        var content = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new RowDefinition { Height = height });
        content.RowDefinitions[3].MinHeight = 16;
        UIElement[] rows = [badge, heading, detail, new Grid(), _action];
        for (var i = 0; i < rows.Length; i++) { Grid.SetRow((FrameworkElement)rows[i], i); content.Children.Add(rows[i]); }
        Children.Add(new Raised(content) { Height = 236 });
    }

    public void Set(bool granted)
    {
        if (granted == _granted) return;
        var animated = _granted is not null;
        _granted = granted;
        _icon.Glyph = granted ? Icons.Check : _glyph;
        UI.Bind(_icon, IconElement.ForegroundProperty, granted ? "OnInkBrush" : "AccentBrush");
        UI.Bind(_disc, Shape.FillProperty, granted ? "InkBrush" : "ChipBrush");
        _shade.Set(Microsoft.UI.Colors.Black, granted ? 0.18 : 0, 10, 4, animated ? 0.45 : 0);
        _action.Children.Clear();
        FrameworkElement action = granted ? UI.GrantedLabel(_grantedText) : _button;
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.VerticalAlignment = VerticalAlignment.Center;
        _action.Children.Add(action);
        if (!animated) return;
        // The symbol bounces as it changes; the label fades in.
        _badge.Scale = new Vector3(0.82f, 0.82f, 1);
        Anim.Spring(_badge, "Scale", Vector3.One, 0.45, 0.3);
        var visual = Anim.Of(action);
        visual.Opacity = 0;
        Anim.WhenLoaded(action, () => Anim.EaseOut(visual, "Opacity", 1, 0.3));
    }
}

// MARK: - Step 4: Your key

internal sealed partial class KeyStep : StepView
{
    private readonly Keycap _keycap = new();
    private readonly Waveform _wave = new() { Width = 240, Height = 36, Gap = 3, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _status;
    private readonly FontIcon _ear = UI.Icon(Icons.Ear, 13, "AccentBrush");
    private readonly TextBlock _statusText = UI.Text("", 13, FontWeights.Medium, "MutedBrush", wrap: true);
    private readonly Button _choose;
    private string? _statusShown;
    private bool _rendering;

    public KeyStep(App app, bool entrance) : base(app, entrance)
    {
        var column = Column(26);
        column.Children.Add(In(new StepHeader(Loc.T("Din genväg", "Your key"),
            Loc.T("Håll in den var som helst, prata och släpp. Prova nu!", "Hold it anywhere, talk, and let go. Try it now!")), 0));

        _wave.Color = Paint.Of("AccentBrush");
        _wave.SetLevels(new float[DictationController.LevelCount]);
        _statusText.TextAlignment = TextAlignment.Center;
        _status = UI.HStack(6, _ear, _statusText);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.MinHeight = 18;
        // A row gives its text all the width it wants: the wrap width goes on the text.
        _statusText.MaxWidth = 520;
        // At rest the wave is a calm dotted line; it wakes when you hold the key.
        column.Children.Add(In(new StackPanel { Spacing = 18, Children = { _keycap, _wave, _status } }, 1));

        var hints = UI.HStack(12,
            Hint(Icons.HandTap, Loc.T("Håll in", "Hold"), Loc.T("för korta meddelanden", "for short messages")),
            Hint(Icons.Lock, Loc.T("Dubbeltryck", "Double-tap"), Loc.T("för att prata fritt", "to talk freely")),
            Hint(Icons.Escape, "Esc", Loc.T("avbryter", "cancels")));
        hints.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(In(hints, 2));

        _choose = UI.SoftButton("", () => { if (App.PickingKey) App.StopPickingKey(); else App.PickKey(); });
        _choose.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTipService.SetToolTip(_choose, Loc.T(
            "Klicka och håll in önskade modifierartangenter tillsammans. Släpp för att spara. Du kan också trycka på en vanlig tangent. Esc avbryter.",
            "Click and hold the modifier keys you want together. Release to save. You can also press a regular key. Esc cancels."));
        column.Children.Add(In(_choose, 3));

        Unloaded += (_, _) => StopFrames();
        Update();
    }

    private static Raised Hint(string glyph, string title, string text)
    {
        var icon = new Grid { Width = 30, Height = 30, Children = { new Ellipse(), UI.Icon(glyph, 13, "AccentBrush") } };
        UI.Bind(icon.Children[0], Shape.FillProperty, "ChipBrush");
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { UI.Text(title, 13, FontWeights.SemiBold), UI.Text(text, 11.5, brush: "MutedBrush") } };
        return new Raised(UI.HStack(10, icon, words), 14, new Thickness(12)) { Width = 180 };
    }

    private Phase Phase => App.Dictation.State.Phase;

    public override void Update()
    {
        var hotkey = App.Settings.Hotkey;
        var recording = Phase == Phase.Recording;
        var transcribing = Phase == Phase.Transcribing;
        _keycap.Set(App.PickingKey ? "…" : hotkey.Chip, recording, !App.PickingKey && hotkey.ModifierKeys.Count > 1);
        _wave.Dimmed = transcribing;
        _wave.Opacity = recording || transcribing ? 1 : 0.55;
        _choose.Content = App.PickingKey ? Loc.T("Tangent eller modifierare…", "Key or modifiers…") : Loc.T("Välj en annan tangent", "Choose Another Key");
        if (recording || transcribing) StartFrames();
        else
        {
            StopFrames();
            _wave.SetLevels(App.Dictation.Levels(DictationController.LevelCount));
        }
        ShowStatus();
    }

    private void ShowStatus()
    {
        var recording = Phase == Phase.Recording;
        var levels = App.Dictation.Levels(8);
        var hearing = recording && levels.Length > 0 && levels.Max() > 0.08f;
        var (text, accent) =
            App.PickingKey ? (Loc.T("Håll in en eller flera modifierartangenter tillsammans och släpp för att spara. Du kan också trycka på en vanlig tangent. Esc avbryter.",
                                    "Hold one or more modifier keys together, then release to save. You can also press a regular key. Esc cancels."), false)
            : hearing ? (Loc.T("Vi hör dig!", "We hear you!"), true)
            : recording ? (Loc.T("Säg något…", "Say something…"), false)
            : Phase == Phase.Transcribing ? (Loc.T("Skriver…", "Typing…"), false)
            : (Loc.T($"Håll in {App.Settings.Hotkey.InlineName} och säg något.", $"Hold {App.Settings.Hotkey.InlineName} and say something."), false);
        if (text == _statusShown) return;
        var first = _statusShown is null;
        _statusShown = text;
        _statusText.Text = text;
        _ear.Visibility = accent ? Visibility.Visible : Visibility.Collapsed;
        UI.Bind(_statusText, TextBlock.ForegroundProperty, accent ? "AccentBrush" : "MutedBrush");
        if (first) return;
        // contentTransition(.opacity), easeOut(0.2)
        var visual = Anim.Of(_status);
        visual.Opacity = 0;
        Anim.EaseOut(visual, "Opacity", 1, 0.2);
    }

    // While you hold the key, the wave and "We hear you!" follow the mic every frame.
    private void StartFrames()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        _wave.SetLevels(App.Dictation.Levels(DictationController.LevelCount));
        ShowStatus();
    }

    public override void Leave()
    {
        StopFrames();
        App.StopPickingKey();
    }
}

// MARK: - Step 5: Try it

internal sealed partial class TryStep : StepView
{
    private readonly StepHeader _header;
    private readonly Grid _area;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Border _edge;
    private readonly Grid _overlay = new();
    private readonly Grid _below = new();
    private readonly Confetti _confetti = new();
    private readonly Animated _height = new(160);
    private ModelState? _overlayShown;
    private TextBlock? _progressTitle, _progressText;
    private Border? _progressFill;
    private bool _celebrated, _rendering;
    private bool? _ready;
    private long _lastFrame;

    public TryStep(App app, bool entrance) : base(app, entrance)
    {
        var column = Column(22);
        _header = new StepHeader(Title, Subtitle);
        column.Children.Add(In(_header, 0));

        // The practice box: a field with a soft shadow, its edge going ink once there's text.
        var host = new Grid { IsHitTestVisible = false };
        var shade = new Shade(host, 18);
        shade.Set(Microsoft.UI.Colors.Black, 0.05, 16, 8);
        var fill = new Border { CornerRadius = new CornerRadius(18) };
        UI.Bind(fill, Border.BackgroundProperty, "FieldBrush");
        _box = PracticeBox();
        _box.TextChanged += (_, _) => Typed();
        _placeholder = UI.Text(Loc.T("”Hej! Det här är min första diktering.”", "“Hi! This is my first dictation.”"), 19, brush: "MutedBrush");
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.Margin = new Thickness(21, 16, 21, 0);
        _placeholder.IsHitTestVisible = false;
        _edge = new Border { CornerRadius = new CornerRadius(18), IsHitTestVisible = false };
        _area = new Grid { Height = 160, Children = { host, fill, _box, _placeholder, _edge, _overlay } };
        column.Children.Add(In(_area, 1));
        column.Children.Add(_below);
        Children.Add(_confetti);

        Loaded += (_, _) => { if (_ready == true) _box.Focus(FocusState.Programmatic); };
        Unloaded += (_, _) => StopFrames();
        Edge();
        Update();
    }

    private bool Ready => App.Model == ModelState.Ready;

    private string Title => _celebrated ? Loc.T("Snyggt. Så enkelt är det.", "Nice. It’s that simple.") : Loc.T("Prova på riktigt", "Try it for real");

    private string Subtitle =>
        _celebrated ? Loc.T("Mindtalk fungerar i alla appar – mejl, Slack, dokument, var du vill.",
                            "Mindtalk works in every app – email, Slack, documents, wherever you like.")
        : Ready ? Loc.T($"Klicka i rutan, håll in {App.Settings.Hotkey.InlineName} och säg något.",
                        $"Click in the box, hold {App.Settings.Hotkey.InlineName} and say something.")
        : Loc.T("Snart kan du prova – när språkmodellen är klar.", "You can try it as soon as the speech model is ready.");

    /// A plain multi-line box: Mindtalk draws the field, its edge and its placeholder itself.
    private static TextBox PracticeBox()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 19,
            Padding = new Thickness(21, 16, 21, 16),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
            "TextControlElevationBorderBrush", "TextControlElevationBorderFocusedBrush",
        }) box.Resources[key] = clear;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "TextControlForegroundDisabled" })
            box.Resources[key] = UI.Brush("PrimaryBrush");
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.SelectionHighlightColor = new SolidColorBrush(Paint.Of("AccentBrush")) { Opacity = 0.25 };
        box.SelectionHighlightColorWhenNotFocused = new SolidColorBrush(Paint.Of("AccentBrush")) { Opacity = 0.12 };
        AutomationProperties.SetName(box, Loc.T("Prova här", "Try it here"));
        return box;
    }

    private void Edge()
    {
        var empty = _box.Text.Length == 0;
        _edge.BorderThickness = new Thickness(empty ? 1 : 2);
        UI.Bind(_edge, Border.BorderBrushProperty, empty ? "FieldStrokeBrush" : "InkBrush");
        _placeholder.Visibility = empty && Ready ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Typed()
    {
        Edge();
        if (_celebrated || string.IsNullOrWhiteSpace(_box.Text)) return;
        Celebrate();
    }

    /// The first words: confetti, a little sound, and what you've set up.
    private void Celebrate()
    {
        _celebrated = true;
        _confetti.Burst();
        App.Dictation.Cues?.Celebrate();
        _header.Set(Title, Subtitle);
        // spring(duration 0.55, bounce 0.2): the box makes room for the summary.
        _height.Spring(110, 0.55, 0.2);
        StartFrames();
        _below.Children.Clear();
        var summary = Summary();
        _below.Children.Add(summary);
        // The summary comes up from below (move(edge: .bottom) + opacity).
        var visual = Anim.Movable(summary, new Vector3(0, 60, 0));
        visual.Opacity = 0;
        Anim.WhenLoaded(summary, () =>
        {
            Anim.Spring(visual, "Translation", Vector3.Zero, 0.55, 0.2);
            Anim.Spring(visual, "Opacity", 1f, 0.55, 0);
        });
    }

    public override void Update()
    {
        var ready = Ready;
        if (ready != _ready)
        {
            var first = _ready is null;
            _ready = ready;
            _box.IsEnabled = ready;
            Edge();
            _header.Set(Title, Subtitle);
            if (!_celebrated)
            {
                _below.Children.Clear();
                if (ready)
                {
                    var tip = Tip();
                    _below.Children.Add(first ? In(tip, 2) : tip);
                }
            }
            if (ready && !first) _box.Focus(FocusState.Programmatic);
        }
        else _header.Set(Title, Subtitle);
        ShowModelState();
    }

    public override void Tick() => ShowModelState();

    public override void Leave() => StopFrames();

    // MARK: Model state

    /// Until the model is ready, the box says what's happening — downloading (with MB and time
    /// left), getting ready, or what went wrong — instead of inviting you to talk when nothing
    /// would happen yet.
    private void ShowModelState()
    {
        var state = App.Model;
        if (state != _overlayShown)
        {
            var first = _overlayShown is null;
            _overlayShown = state;
            _overlay.Children.Clear();
            _progressTitle = _progressText = null;
            _progressFill = null;
            if (state != ModelState.Ready)
            {
                var content = ModelStateContent(state);
                _overlay.Children.Add(content);
                if (!first)
                {
                    var visual = Anim.Of(content);
                    visual.Opacity = 0;
                    Anim.WhenLoaded(content, () => Anim.EaseOut(visual, "Opacity", 1, 0.3));
                }
            }
        }
        if (state != ModelState.Downloading || _progressText is null) return;
        var engine = App.Engine;
        var p = App.EngineProgress;
        _progressTitle!.Text = SpeechModel.IsPreparing(p)
            ? Loc.T($"Förbereder {engine.Title.ToLower(Loc.Culture)}", $"Preparing {engine.Title.ToLower(Loc.Culture)}")
            : Loc.T($"{engine.Title} laddas ned", $"Downloading {engine.Title}");
        _progressFill!.Width = 300 * Math.Clamp(p / 0.9, 0, 1);
        _progressText.Text = engine.ProgressText(p, App.Speech.DownloadStarted(engine), DateTime.Now);
    }

    private StackPanel ModelStateContent(ModelState state)
    {
        var engine = App.Engine;
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        TextBlock Heading(string text) => Paint.Centered(text, 16, FontWeights.SemiBold);
        switch (state)
        {
            case ModelState.Downloading:
                _progressTitle = Heading("");
                var track = new Border { Width = 300, Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Center };
                UI.Bind(track, Border.BackgroundProperty, "ChipBrush");
                _progressFill = new Border { Width = 0, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
                UI.Bind(_progressFill, Border.BackgroundProperty, "InkBrush");
                track.Child = _progressFill;
                _progressText = Paint.Centered("", 13, brush: "MutedBrush");
                stack.Children.Add(_progressTitle);
                stack.Children.Add(track);
                stack.Children.Add(_progressText);
                break;
            case ModelState.Loading:
                var ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
                UI.Bind(ring, Control.ForegroundProperty, "InkBrush");
                stack.Children.Add(ring);
                stack.Children.Add(Heading(Loc.T($"Gör {engine.Title.ToLower(Loc.Culture)} redo för din dator", $"Getting {engine.Title.ToLower(Loc.Culture)} ready for your PC")));
                stack.Children.Add(Paint.Centered(Loc.T("Bara första gången – upp till en minut.", "Only the first time – up to a minute."), 13, brush: "MutedBrush"));
                break;
            case ModelState.Failed:
                var warning = UI.Icon(Icons.WarningFill, 22, "OrangeBrush");
                warning.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(warning);
                stack.Children.Add(Heading(Loc.T($"{engine.Title} laddades inte ned", $"{engine.Title} didn’t download")));
                var message = Paint.Centered(App.ModelError ?? "", 12.5, brush: "MutedBrush");
                message.MaxLines = 2;
                stack.Children.Add(message);
                stack.Children.Add(Centered(UI.InkButton(Loc.T("Försök igen", "Try Again"), () => App.DownloadModel())));
                break;
            default:
                stack.Children.Add(Heading(Loc.T($"{engine.Title} är inte nedladdad", $"{engine.Title} isn’t downloaded")));
                stack.Children.Add(Centered(UI.InkButton(Loc.T($"Ladda ned {engine.SizeText}", $"Download {engine.SizeText}"), () => App.DownloadModel())));
                break;
        }
        return stack;
    }

    private static T Centered<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    private static StackPanel Tip()
    {
        var tip = UI.HStack(7, UI.Icon(Icons.Lightbulb, 13, "MutedBrush"),
            UI.Text(Loc.T("Tips: dubbeltryck på tangenten för att prata fritt, tryck igen när du är klar.",
                          "Tip: double-tap the key to talk freely, press again when you’re done."), 12.5, brush: "MutedBrush"));
        tip.HorizontalAlignment = HorizontalAlignment.Center;
        return tip;
    }

    // MARK: Summary

    /// What you've set up, and where Mindtalk lives.
    private Raised Summary()
    {
        var grid = new Grid();
        for (var i = 0; i < 5; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Item(int column, string label, FrameworkElement value)
        {
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var item = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var caption = UI.Text(label, 11.5, FontWeights.Medium, "MutedBrush");
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            item.Children.Add(caption);
            item.Children.Add(value);
            Grid.SetColumn(item, column);
            grid.Children.Add(item);
        }
        void Divider(int column)
        {
            var line = new Rectangle { Width = 1, Height = 38, VerticalAlignment = VerticalAlignment.Center };
            UI.Bind(line, Shape.FillProperty, "DividerBrush");
            Grid.SetColumn(line, column);
            grid.Children.Add(line);
        }
        Item(0, Loc.T("Din genväg", "Your shortcut"), UI.KeyChip(App.Settings.Hotkey.Chip));
        Divider(1);
        Item(2, Loc.T("Språk", "Language"), UI.Text(App.Engine.Title, 14, FontWeights.SemiBold));
        Divider(3);
        var arrow = UI.Icon(Icons.ArrowUpRight, 12, "AccentBrush");
        Item(4, Loc.T("Hittas i aktivitetsfältet", "Find it in the taskbar"), UI.HStack(8, new MarkGlyph(), arrow));
        Nudge(arrow);
        return new Raised(grid, 18, new Thickness(0, 16, 0, 16));
    }

    /// The arrow nudges towards the taskbar: easeInOut(0.8), back and forth for ever.
    private static void Nudge(UIElement arrow)
    {
        if (Motion.Reduced) return;
        var visual = Anim.Movable(arrow);
        var compositor = visual.Compositor;
        var nudge = compositor.CreateVector3KeyFrameAnimation();
        nudge.InsertKeyFrame(0, Vector3.Zero);
        nudge.InsertKeyFrame(1, new Vector3(3, -3, 0), compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
        nudge.Duration = TimeSpan.FromSeconds(0.8);
        nudge.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
        nudge.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        Anim.WhenLoaded((FrameworkElement)arrow, () => visual.StartAnimation("Translation", nudge));
    }

    // MARK: Frames

    private void StartFrames()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = Environment.TickCount64;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var moving = _height.Step(Math.Min(0.1, (now - _lastFrame) / 1000.0));
        _lastFrame = now;
        _area.Height = _height.Value;
        if (!moving) StopFrames();
    }
}

/// <summary>Segoe Fluent Icons the shared set doesn't have.</summary>
internal static class Glyphs
{
    public const string Power = "";
}
