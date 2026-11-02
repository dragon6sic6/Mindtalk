using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Input;
using Mindtalk.Platform;
using Windows.UI;

namespace Mindtalk.App.Onboarding;

// 2.0's introduction: the app icon on Welcome, and the last two steps — Thoughts and Meetings.

/// <summary>The app icon: the dot-sphere on a black rounded tile, with a soft shadow.</summary>
internal static class AppIcon
{
    public static FrameworkElement Make(double size)
    {
        var tile = new Grid { Width = size, Height = size };
        var face = new Border { CornerRadius = new CornerRadius(size * 0.225), Background = new SolidColorBrush(Color.FromArgb(255, 4, 4, 4)) };
        // In dark mode a hairline keeps the black tile off the black paper.
        if (UI.Dark) { face.BorderThickness = new Thickness(1); face.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)); }
        tile.Children.Add(face);
        tile.Children.Add(Sphere(size * 0.8, 700));
        var shadowed = Shade.Around(tile, size * 0.225, out var shade);
        shade.Set(Colors.Black, UI.Dark ? 0.5 : 0.22, 28, 14);
        var holder = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { shadowed } };
        AutomationProperties.SetAccessibilityView(holder, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return holder;
    }

    /// The white sphere on black with the icon's bolder dots, as in the Mac's icon.
    public static Orb Sphere(double size, int count) => new(size, onDark: true, count: count)
    {
        DotScale = 1.7,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>Little pieces both tour steps use.</summary>
internal static class TourParts
{
    /// "HOW A MEETING STARTS": small spaced capitals.
    public static TextBlock Caps(string text, string brush = "MutedBrush")
    {
        var caps = UI.Text(text.ToUpper(Loc.Culture), 11.5, FontWeights.SemiBold, brush);
        caps.CharacterSpacing = 220;
        return caps;
    }

    /// A key as a keycap chip on paper ("Ctrl Alt M").
    public static Border Keys(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(10, 4, 10, 5),
            VerticalAlignment = VerticalAlignment.Top,
            Child = UI.Text(text, 14, FontWeights.SemiBold),
        };
        UI.Bind(chip, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(chip, Border.BorderBrushProperty, "FieldStrokeBrush");
        return chip;
    }

    /// The ink disc with a check: "✓ The voice model is on your PC."
    public static Grid CheckDisc(double size = 26)
    {
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, "InkBrush");
        var check = UI.Icon(Icons.Check, size * 0.45, "OnInkBrush");
        check.FontWeight = FontWeights.Bold;
        return new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Children = { disc, check } };
    }

    /// A rounded hint: an icon in a circle, a word and a few more.
    public static Raised Hint(string glyph, string title, string text)
    {
        var icon = new Grid { Width = 30, Height = 30, Children = { new Ellipse(), UI.Icon(glyph, 13, "AccentBrush") } };
        UI.Bind(icon.Children[0], Shape.FillProperty, "ChipBrush");
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { UI.Text(title, 13, FontWeights.SemiBold), UI.Text(text, 11.5, brush: "MutedBrush", wrap: true) } };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return new Raised(row, 14, new Thickness(12)) { Width = 190 };
    }
}

// MARK: - Step 6: Thoughts

/// <summary>
/// Say a thought: hold left Alt, say what's on your mind, let go — it's saved on the Thoughts
/// page instead of being typed where you are. Try it here: a thought said while this step is showing
/// takes the example's place.
/// </summary>
internal sealed partial class ThoughtsStep : StepView
{
    private readonly Keycap _keycap = new();
    private readonly TextBlock _text, _when, _badgeText;
    private readonly Border _badge;
    private readonly DateTime _since = DateTime.Now;
    private readonly DispatcherQueueTimerHolder _keys = new();
    private Guid? _shown;

    public ThoughtsStep(App app, bool entrance) : base(app, entrance)
    {
        var key = app.Settings.ThoughtHotkey;
        var column = Column(24);
        column.Children.Add(In(new StepHeader(Loc.T("Säg en tanke", "Say a thought"),
            key is { } k
                ? Loc.T($"Håll in {k.InlineName}, säg vad du tänker på och släpp. Den sparas under Tankar, i stället för att skrivas där du är.",
                        $"Hold {k.InlineName}, say what’s on your mind and let go. It’s saved on the Thoughts page instead of being typed where you are.")
                : Loc.T($"Tryck på {Shortcut.Thought}, säg vad du tänker på och tryck igen. Den sparas under Tankar.",
                        $"Press {Shortcut.Thought}, say what’s on your mind and press again. It’s saved on the Thoughts page.")) { MaxWidth = 600 }, 0));

        _keycap.Set(key?.Chip ?? Shortcut.Thought.ToString(), false, key is null || key.ModifierKeys.Count > 1);
        column.Children.Add(In(_keycap, 1));

        // A thought as the Thoughts page shows it: an example until you say one.
        _badgeText = UI.Text("", 11, FontWeights.SemiBold, "MutedBrush");
        _badge = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(9, 3, 9, 4), Child = _badgeText, HorizontalAlignment = HorizontalAlignment.Right };
        UI.Bind(_badge, Border.BackgroundProperty, "ChipBrush");
        var top = new Grid { Children = { TourParts.Caps(Loc.T("Tankar", "Thoughts")), _badge } };
        _text = new TextBlock { FontFamily = UI.Serif, FontSize = 21, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis };
        UI.Bind(_text, TextBlock.ForegroundProperty, "PrimaryBrush");
        _when = UI.Text("", 12, brush: "MutedBrush");
        var card = new Raised(new StackPanel { Spacing = 12, Children = { top, _text, _when } }, 20, new Thickness(22, 18, 22, 18))
        {
            Width = 470,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        column.Children.Add(In(card, 2));

        var hints = UI.HStack(12,
            TourParts.Hint(Icons.HandTap, Loc.T("Håll in", "Hold"), Loc.T("medan du pratar", "while you talk")),
            TourParts.Hint(Icons.Lock, Loc.T("Snabbt tryck", "Quick tap"), Loc.T("för att prata fritt", "to talk freely")),
            TourParts.Hint(Icons.Lightbulb, Loc.T("Släpp", "Let go"), Loc.T("och tanken sparas", "and it’s saved")));
        hints.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(In(hints, 3));

        Example();
        Update();
        // The keycap sinks while you hold the keys.
        Loaded += (_, _) => _keys.Start(DispatcherQueue, 0.05, Keys);
        Unloaded += (_, _) => _keys.Stop();
    }

    private void Keys()
    {
        if (App.Settings.ThoughtHotkey is not { } key) return;
        var held = key.ModifierKeys.All(k => KeyboardHook.IsPhysicallyDown(k.Key));
        _keycap.Set(key.Chip, held, key.ModifierKeys.Count > 1);
    }

    private void Example()
    {
        _text.Text = Loc.T("”Fråga Anna om lanseringen kan flyttas till torsdag.”", "“Ask Anna whether the launch can move to Thursday.”");
        _when.Text = Loc.T("I dag", "Today");
        _badgeText.Text = Loc.T("Exempel", "Example");
    }

    /// A thought said while this step shows takes the example's place.
    public override void Update()
    {
        var newest = App.Thoughts.All.Where(t => t.Created >= _since).MaxBy(t => t.Created);
        if (newest is null || newest.Id == _shown) return;
        _shown = newest.Id;
        _text.Text = newest.Text;
        _when.Text = Loc.T($"Nyss · {newest.Created.ToString("t", Loc.Culture)}", $"Just now · {newest.Created.ToString("t", Loc.Culture)}");
        _badgeText.Text = Loc.T("✓ Sparad under Tankar", "✓ Saved on the Thoughts page");
        UI.Bind(_badgeText, TextBlock.ForegroundProperty, "OnInkBrush");
        UI.Bind(_badge, Border.BackgroundProperty, "InkBrush");
        var visual = Anim.Scalable(_text);
        visual.Opacity = 0;
        Anim.Spring(visual, "Opacity", 1f, 0.45, 0);
    }

    public override void Leave() => _keys.Stop();
}

// MARK: - Step 7: Meetings

/// <summary>
/// Meetings, in the room and on calls: an example of a saved meeting on black, and how a
/// meeting starts — your shortcut in the room, a question when a call starts. The voice model
/// can be downloaded from here if it isn't on the PC yet.
/// </summary>
internal sealed partial class MeetingsStep : StepView
{
    private readonly ContentControl _voiceModel = new() { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private string? _voiceKey;

    public MeetingsStep(App app, bool entrance) : base(app, entrance)
    {
        var column = Column(0);
        column.Margin = new Thickness(-28, 0, -28, 0);
        var header = new StepHeader(Loc.T("Möten, i rummet och i samtal", "Meetings, in the room and on calls"),
            Loc.T("Mindtalk hör vem som säger vad, skriver ned det medan ni pratar och sammanfattar efteråt.",
                  "Mindtalk hears who says what, writes it down while you talk and summarizes afterwards.")) { MaxWidth = 700 };
        column.Children.Add(In(header, 0));

        var row = new Grid { ColumnSpacing = 16, Height = 312, Margin = new Thickness(0, 30, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.36, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var example = Example();
        row.Children.Add(In(example, 1));
        var how = HowItStarts(app);
        Grid.SetColumn(how, 1);
        row.Children.Add(In(how, 2));
        column.Children.Add(row);

        var tell = Paint.Centered(Loc.T("Säg till alla att mötet spelas in.", "Tell everyone the meeting is being recorded."), 13, brush: "MutedBrush");
        tell.Margin = new Thickness(0, 22, 0, 0);
        column.Children.Add(In(tell, 3));
        Update();
    }

    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    /// Segoe Fluent's handset (the shared Phone glyph is a mobile).
    private const string Handset = "";
    private static SolidColorBrush Grey(byte v, byte a = 255) => new(Color.FromArgb(a, v, v, v));
    private static SolidColorBrush Sage => new(Color.FromArgb(255, 0x8F, 0xBF, 0x9C));

    private static TextBlock On(string text, double size, Windows.UI.Text.FontWeight? weight = null, SolidColorBrush? brush = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Grey(255) };
        return block;
    }

    /// The black example card: a saved meeting with its summary, a decision and a task.
    private static Grid Example()
    {
        var content = new StackPanel { Spacing = 0 };

        var badge = new Border { CornerRadius = new CornerRadius(100), Padding = new Thickness(9, 3, 9, 4), Background = Grey(255, 36), Child = On(Loc.T("Exempel", "Example"), 11.5, FontWeights.SemiBold, Grey(205)) };
        var saved = UI.HStack(6, new FontIcon { Glyph = Icons.Check, FontSize = 12, FontFamily = UI.Icon(Icons.Check).FontFamily, Foreground = Grey(255) },
                              On(Loc.T("Mötet är sparat.", "The meeting is saved."), 13, FontWeights.SemiBold), On("0:14", 13, FontWeights.SemiBold, Grey(160)));
        content.Children.Add(UI.HStack(12, badge, saved));

        var summary = On(Loc.T("Sammanfattning", "Summary"), 16, FontWeights.SemiBold);
        summary.Margin = new Thickness(0, 22, 0, 12);
        content.Children.Add(summary);
        content.Children.Add(On(Loc.T("Ni stämde av lanseringen och vem som kör testerna.", "You went over the launch and who runs the tests."), 14));

        content.Children.Add(Item(Icons.CheckCircle, Loc.T("Beslut", "Decisions"), Loc.T("Lanseringen blir den fjärde november.", "The launch is on the fourth of November."), null));
        var speaker = new Border
        {
            CornerRadius = new CornerRadius(100),
            Padding = new Thickness(9, 3, 10, 4),
            Background = Grey(255, 30),
            VerticalAlignment = VerticalAlignment.Top,
            Child = UI.HStack(6, new Ellipse { Width = 7, Height = 7, Fill = Sage }, On(Loc.T("Talare 3", "Speaker 3"), 12.5, FontWeights.SemiBold)),
        };
        content.Children.Add(Item(Icons.Circle, Loc.T("Att göra", "To do"), Loc.T("Kör klart testerna och ge besked på torsdag.", "Finish the tests and report back on Thursday."), speaker));

        var quote = new Grid { ColumnSpacing = 8, Margin = new Thickness(28, 12, 0, 0) };
        quote.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        quote.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quote.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(255, 0x6E, 0x8F, 0x78)), RadiusX = 1, RadiusY = 1 });
        var words = On(Loc.T("”Jag tar testerna och ger besked på torsdag.”  0:08", "“I’ll take the tests and let you know on Thursday.”  0:08"), 12.5, null, Grey(165));
        Grid.SetColumn(words, 1);
        quote.Children.Add(words);
        content.Children.Add(quote);

        var face = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Black),
            Padding = new Thickness(18, 18, 18, 18),
            Child = content,
            BorderThickness = new Thickness(1),
            BorderBrush = UI.Dark ? Grey(48) : new SolidColorBrush(Black),
        };
        var card = new Grid { Children = { face } };
        // The mark in the corner, slowly turning.
        var orb = AppIcon.Sphere(76, 420);
        orb.HorizontalAlignment = HorizontalAlignment.Right;
        orb.VerticalAlignment = VerticalAlignment.Top;
        orb.Margin = new Thickness(0, 4, 6, 0);
        card.Children.Add(orb);
        var shadowed = Shade.Around(card, 22, out var shade);
        shadowed.HorizontalAlignment = HorizontalAlignment.Stretch;
        shadowed.VerticalAlignment = VerticalAlignment.Stretch;
        shade.Set(Colors.Black, UI.Dark ? 0.4 : 0.22, 20, 10);
        return shadowed;
    }

    /// "◯ To do / Finish the tests…", with an optional chip on the right.
    private static Grid Item(string glyph, string label, string text, FrameworkElement? trailing)
    {
        var item = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, FontSize = 15, FontFamily = UI.Icon(glyph).FontFamily, Foreground = Grey(185), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        item.Children.Add(icon);
        var words = new StackPanel { Spacing = 3, Children = { On(label, 12, FontWeights.SemiBold, Grey(185)), On(text, 14) } };
        Grid.SetColumn(words, 1);
        item.Children.Add(words);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); item.Children.Add(trailing); }
        return item;
    }

    /// "How a meeting starts": in the room, in a call — and whether the voice model is here.
    private Raised HowItStarts(App app)
    {
        var stack = new Grid { RowSpacing = 0 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        top.Children.Add(TourParts.Caps(Loc.T("Så börjar ett möte", "How a meeting starts")));
        var room = UI.HStack(10, UI.Icon(Icons.People, 18), UI.Text(Loc.T("I rummet", "In the room"), 17, FontWeights.SemiBold));
        room.Margin = new Thickness(0, 18, 0, 10);
        top.Children.Add(room);
        var keys = new Grid { ColumnSpacing = 12 };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keys.Children.Add(TourParts.Keys((app.Settings.MeetingShortcut ?? Shortcut.Meeting).ToString().Replace("+", " ")));
        var opens = UI.Text(Loc.T("öppnar ett nytt möte, var du än är.", "opens a new meeting, wherever you are."), 13, brush: "MutedBrush", wrap: true);
        opens.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(opens, 1);
        keys.Children.Add(opens);
        top.Children.Add(keys);
        var call = UI.HStack(10, UI.Icon(Handset, 16), UI.Text(Loc.T("I samtal", "In a call"), 17, FontWeights.SemiBold));
        call.Margin = new Thickness(0, 22, 0, 8);
        top.Children.Add(call);
        top.Children.Add(UI.Text(Loc.T("När ett samtal i Teams, Zoom eller Meet börjar frågar Mindtalk först. Inget spelas in förrän du trycker.",
                                       "When a call in Teams, Zoom or Meet starts, Mindtalk asks first. Nothing is recorded until you press."), 13, brush: "MutedBrush", wrap: true));
        stack.Children.Add(top);
        Grid.SetRow(_voiceModel, 2);
        stack.Children.Add(_voiceModel);
        return new Raised(stack, 22, new Thickness(20, 20, 20, 18));
    }

    /// The voice model: here, coming, or one click away.
    public override void Update()
    {
        var service = Settings.SettingsServices.Meetings(App);
        var progress = service?.SpeakerModelProgress;
        var key = service is null ? "none" : $"{service.SpeakerModelReady}:{progress is not null}:{service.SpeakerModelError is not null}";
        if (key == _voiceKey)
        {
            if (progress is { } p && _voiceModel.Content is StackPanel { Children: [_, TextBlock percent] })
                percent.Text = Loc.T($"Röstmodellen laddas ned – {(int)Math.Round(p * 100)} %", $"Downloading the voice model – {(int)Math.Round(p * 100)}%");
            return;
        }
        _voiceKey = key;
        FrameworkElement content;
        if (service is null || service.SpeakerModelReady)
        {
            // Without the meeting engine (yet), the model comes with it — nothing to do here.
            content = UI.HStack(10, TourParts.CheckDisc(), UI.Text(service is null
                ? Loc.T("Röstmodellen kommer med möten.", "The voice model comes with meetings.")
                : Loc.T("Röstmodellen finns på din dator.", "The voice model is on your PC."), 13.5, FontWeights.SemiBold, wrap: true));
        }
        else if (progress is { } p)
        {
            var ring = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
            UI.Bind(ring, Control.ForegroundProperty, "InkBrush");
            content = UI.HStack(10, ring, UI.Text(Loc.T($"Röstmodellen laddas ned – {(int)Math.Round(p * 100)} %", $"Downloading the voice model – {(int)Math.Round(p * 100)}%"), 13, FontWeights.SemiBold, wrap: true));
        }
        else
        {
            var download = UI.InkButton(service.SpeakerModelError is null ? Loc.T("Ladda ned röstmodellen", "Download the Voice Model") : Loc.T("Försök igen", "Try Again"),
                                        () => _ = service.DownloadSpeakerModelAsync());
            download.HorizontalAlignment = HorizontalAlignment.Left;
            content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    UI.Text(service.SpeakerModelError ?? Loc.T("Den håller isär talarna. Laddas ned en gång.", "It keeps the speakers apart. Downloaded once."), 12.5, brush: "MutedBrush", wrap: true),
                    download,
                },
            };
        }
        _voiceModel.Content = content;
    }

    public override void Tick() => Update();
}
