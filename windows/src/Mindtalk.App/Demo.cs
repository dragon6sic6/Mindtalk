using System.Globalization;
using System.Text.Json;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>
/// Demo mode for screenshots, as the Mac's (debug builds only): `--demo` fills Recent, the
/// statistics and the vocabulary with example data in a folder of its own — nothing is read
/// from or written to your own — and treats the app as ready.
///
///   Mindtalk --demo [--show-panel] [--page settings] [--onboarding-step key] [--dark]
///   Mindtalk --demo-hud [--demo-locked] [--demo-transcribing] [--demo-language] [--demo-failed] [--demo-copied]
///   Mindtalk --models-dir &lt;path&gt;   (a first install, without touching your models)
/// </summary>
internal static class Demo
{
    private static readonly string[] Args = Environment.GetCommandLineArgs();
    private static string? _folder;

    public static bool Has(string flag) => Args.Contains(flag);

    /// This build's own demo folder: builds from different checkouts don't share one.
    private static string Build => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..8];

    private static string? Value(string flag)
    {
        var i = Array.IndexOf(Args, flag);
        return i > 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

#if DEBUG
    public static bool On => Has("--demo");
    public static string? ModelsDirectory => Value("--models-dir");
#else
    public static bool On => false;
    public static string? ModelsDirectory => null;
#endif

    /// Where a store keeps its file — in demo mode, the demo folder.
    public static string? File(string name) => _folder is null ? null : Path.Combine(_folder, name);

    /// Demo mode reads a copy of your settings and saves only that copy.
    public static string? SettingsPath
    {
        get
        {
            if (!On) return null;
            _folder = Path.Combine(Path.GetTempPath(), "mindtalk-demo-" + Build);
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "settings.json");
            if (System.IO.File.Exists(AppSettings.DefaultPath)) System.IO.File.Copy(AppSettings.DefaultPath, path, overwrite: true);
            else System.IO.File.Delete(path);
            return path;
        }
    }

    public static string? OnboardingStep => On ? Value("--onboarding-step") : null;

#if DEBUG
    /// A speaking waveform (the Mac's demo levels).
    private static readonly float[] Voice =
    [
        0.05f, 0.12f, 0.30f, 0.52f, 0.41f, 0.66f, 0.35f, 0.22f, 0.48f, 0.71f, 0.58f, 0.33f,
        0.18f, 0.40f, 0.62f, 0.50f, 0.28f, 0.45f, 0.69f, 0.54f, 0.31f, 0.47f, 0.60f, 0.38f,
    ];

    /// Writes the example data and points the stores at it. Your settings are read but never saved.
    public static void Prepare(AppSettings settings)
    {
        if (!On) return;
        if (Has("--dark")) settings.Appearance = Appearance.Dark;
        if (Has("--light")) settings.Appearance = Appearance.Light;
        if (Has("--sv")) settings.Language = AppLanguage.Swedish;
        if (Has("--en")) settings.Language = AppLanguage.English;
        Loc.Use(settings.Language);
        settings.DidOnboard = true;
        _folder ??= Path.Combine(Path.GetTempPath(), "mindtalk-demo-" + Build);
        Directory.CreateDirectory(_folder);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        object[] vocabulary =
        [
            new { id = Guid.NewGuid(), word = "Mindtalk", heardAs = new[] { "mind talk" }, fixes = 42 },
            new { id = Guid.NewGuid(), word = "Klang", heardAs = new[] { "klangen", "clang" }, fixes = 9 },
            new { id = Guid.NewGuid(), word = "Parakeet", heardAs = Array.Empty<string>(), fixes = 3 },
            new { id = Guid.NewGuid(), word = "Linnéa", heardAs = new[] { "linnea", "lin nea" }, fixes = 14 },
        ];
        System.IO.File.WriteAllText(Path.Combine(_folder, "vocabulary.json"), JsonSerializer.Serialize(vocabulary, json));

        var now = DateTime.Now;
        (string Text, double Minutes)[] texts = Loc.IsSwedish
            ?
            [
                ("Tack för i dag! Jag skickar över offerten i morgon förmiddag, så hörs vi på torsdag.", 4),
                ("Kan vi flytta mötet till klockan tre? Jag sitter fast i ett samtal fram till dess.", 38),
                ("Sammanfattning: kunden vill ha en pilot i januari, budgeten är godkänd och nästa steg är ett avtal.", 95),
                ("Let's ship the new onboarding on Monday and collect feedback during the week.", 180),
                ("Kom ihåg att köpa mjölk, bröd och kaffe på vägen hem.", 60 * 26),
            ]
            :
            [
                ("Thanks for today! I'll send over the proposal tomorrow morning and we'll talk on Thursday.", 4),
                ("Can we move the meeting to three o'clock? I'm stuck on a call until then.", 38),
                ("Summary: the customer wants a pilot in January, the budget is approved and the next step is a contract.", 95),
                ("Tack för i dag, vi ses på måndag!", 180),
                ("Remember to pick up milk, bread and coffee on the way home.", 60 * 26),
            ];
        var recent = texts.Select(t => new RecentEntry(Guid.NewGuid(), t.Text, now.AddMinutes(-t.Minutes))).ToList();
        System.IO.File.WriteAllText(Path.Combine(_folder, "Recent.json"), JsonSerializer.Serialize(recent, json));

        // A believable week: busier midweek, today under way.
        int[] words = [1_240, 1_860, 2_310, 1_690, 980, 420, 310];
        var today = now.Date;
        var weekday = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0
        var days = new Dictionary<string, object>();
        for (var offset = 0; offset <= weekday + 21; offset++)
        {
            var date = today.AddDays(-offset);
            var w = offset == 0 ? 640 : words[((int)date.DayOfWeek + 6) % 7];
            days[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new { words = w, seconds = w / 150.0 * 60, dictations = Math.Max(1, w / 45) };
        }
        System.IO.File.WriteAllText(Path.Combine(_folder, "stats.json"), JsonSerializer.Serialize(days, json));
    }

    public static bool Hud(HudWindow hud)
    {
        if (!Has("--demo-hud")) return false;
        var phase = Has("--demo-transcribing") ? Phase.Transcribing
                  : Has("--demo-failed") ? Phase.Failed
                  : Has("--demo-copied") ? Phase.Copied
                  : Phase.Recording;
        var state = new DictationState(phase, Has("--demo-locked"), true,
            Has("--demo-failed") ? Loc.T("Du bytte app – tryck Ctrl+Alt+V för att klistra in texten.",
                                          "You switched apps – press Ctrl+Alt+V to paste the text.") : null,
            Has("--demo-language"), Has("--demo-multilingual") ? "multilingual" : "swedish");
        hud.Demo(state, n => Voice[^n..]);
        return true;
    }

    /// Opens what the flags ask for; true if it did (the normal start is skipped).
    public static bool Start(App app)
    {
        if (!On) return false;
        if (OnboardingStep is not null) { app.ShowOnboarding(); return true; }
        if (Has("--show-panel")) { app.TogglePanel(); return true; }
        app.ShowMain(Enum.TryParse<Page>(Value("--page"), ignoreCase: true, out var page) ? page : Page.Dictation);
        return true;
    }
#endif
}
