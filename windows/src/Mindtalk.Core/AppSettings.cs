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

/// <summary>Preferences, kept as JSON in %APPDATA%\Mindtalk\settings.json.</summary>
public sealed class AppSettings
{
    public string Model { get; set; } = "swedish";
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    public DictationMode Mode { get; set; } = DictationMode.HoldOrDoubleTap;
    public bool RemoveFillers { get; set; } = true;
    public bool VoiceCommands { get; set; } = true;
    /// Leave the dictated text on the clipboard instead of restoring what was there.
    public bool KeepInClipboard { get; set; }
    /// WASAPI endpoint ID; null = the system's default input.
    public string? MicrophoneId { get; set; }

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindtalk", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
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
}
