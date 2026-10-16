using System.Text.Json;
using System.Text.Json.Serialization;
using Mindtalk.Core.Input;

namespace Mindtalk.Core;

public enum DictationMode
{
    /// Hold for short messages; double-tap to lock hands-free.
    HoldOrDoubleTap,
    /// Only while the key is held.
    Hold,
    /// One press starts, the next one stops.
    Toggle,
}

/// <summary>What happens to music and other sound while you dictate.</summary>
public enum MediaMode
{
    /// Media apps pause and play again afterwards; other sound is faded down.
    Pause,
    /// All sound is faded down and back up.
    Duck,
    /// The sound is left alone.
    Off,
}

public enum Appearance { Light, Dark, System }

/// <summary>The app's own language. System follows Windows (English for anything but Swedish).</summary>
public enum AppLanguage { System, Swedish, English }

/// <summary>A volume Mindtalk faded down, kept so it can be put back after a crash.</summary>
public sealed record DuckedVolume(string DeviceId, float Volume);

/// <summary>Preferences, kept as JSON in %APPDATA%\Mindtalk\settings.json.</summary>
public sealed class AppSettings
{
    /// The language you dictate in; null until chosen (then the recommended one is used).
    public string? Model { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    public DictationMode Mode { get; set; } = DictationMode.HoldOrDoubleTap;
    public bool RemoveFillers { get; set; } = true;
    public bool VoiceCommands { get; set; } = true;
    /// Leave the dictated text on the clipboard instead of restoring what was there.
    public bool KeepInClipboard { get; set; }
    /// Polish with the on-device AI model. Off until you turn it on.
    public bool AiPolish { get; set; }
    /// "builtin", "system", or a WASAPI endpoint ID.
    public string Microphone { get; set; } = "builtin";
    /// Older settings files: a chosen endpoint (null = the system default).
    [JsonInclude, JsonPropertyName("MicrophoneId")] public string? LegacyMicrophoneId { get; set; }
    public MediaMode MediaMode { get; set; } = MediaMode.Pause;
    /// A soft sound when dictation starts and when the text is pasted.
    public bool Sounds { get; set; } = true;
    /// Light by default — the paper look.
    public Appearance Appearance { get; set; } = Appearance.Light;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    /// Keep Mindtalk in the taskbar when the window is closed (it minimizes instead).
    public bool ShowInTaskbar { get; set; }
    /// Look for updates once a day.
    public bool AutomaticUpdates { get; set; } = true;
    /// Set once the introduction has been completed (or closed).
    public bool DidOnboard { get; set; }
    /// The page to open after a relaunch (to switch the app's language).
    public string? ReopenPage { get; set; }
    /// A faded volume to put back if Mindtalk quit before it could.
    public DuckedVolume? Ducked { get; set; }

    public static string Folder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindtalk");

    public static string DefaultPath { get; } = System.IO.Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonIgnore] public string Path { get; private set; } = DefaultPath;

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            settings = new AppSettings();   // A damaged file shouldn't stop the app.
        }
        if (settings.LegacyMicrophoneId is { } id)
        {
            settings.Microphone = id;
            settings.LegacyMicrophoneId = null;
        }
        settings.Path = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, Path, overwrite: true);
    }

    public static string ModeTitle(DictationMode mode) => mode switch
    {
        DictationMode.HoldOrDoubleTap => Loc.T("Håll in eller dubbeltryck", "Hold or double-tap"),
        DictationMode.Hold => Loc.T("Bara håll in", "Hold only"),
        _ => Loc.T("Tryck för att starta och stoppa", "Press to start and stop"),
    };

    public static string ModeExplanation(DictationMode mode) => mode switch
    {
        DictationMode.HoldOrDoubleTap => Loc.T("Håll in för korta meddelanden. Dubbeltryck för att låsa och prata fritt.",
                                               "Hold for short messages. Double-tap to lock and talk freely."),
        DictationMode.Hold => Loc.T("Mindtalk lyssnar så länge du håller in.", "Mindtalk listens as long as you hold."),
        _ => Loc.T("Tryck för att börja, tryck igen för att klistra in.", "Press to start, press again to paste."),
    };

    public static string MediaTitle(MediaMode mode) => mode switch
    {
        MediaMode.Pause => Loc.T("Pausa", "Pause"),
        MediaMode.Duck => Loc.T("Tona ner", "Lower volume"),
        _ => Loc.T("Låt vara", "Leave as is"),
    };

    public static string MediaExplanation(MediaMode mode) => mode switch
    {
        MediaMode.Pause => Loc.T("Musik och video pausas medan du pratar och fortsätter sen. Annat ljud tonas ner.",
                                 "Music and video pause while you talk and resume afterwards. Other sound is lowered."),
        MediaMode.Duck => Loc.T("Allt ljud tonas ner medan du pratar och upp igen efteråt.",
                                "All sound is lowered while you talk and raised again afterwards."),
        _ => Loc.T("Ljudet på datorn lämnas som det är.", "Sound on your PC is left as it is."),
    };

    public static string AppearanceTitle(Appearance appearance) => appearance switch
    {
        Appearance.Light => Loc.T("Ljust", "Light"),
        Appearance.Dark => Loc.T("Mörkt", "Dark"),
        _ => Loc.T("System", "System"),
    };

    /// Each language in its own words, so you can always find yours.
    public static string LanguageTitle(AppLanguage language) => language switch
    {
        AppLanguage.Swedish => "Svenska",
        AppLanguage.English => "English",
        _ => Loc.T("Som datorn", "Same as PC"),
    };
}
