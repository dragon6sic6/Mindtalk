using System.Text.Json;

namespace Mindtalk.Core.Dictation;

/// <summary>One dictation, as it was typed.</summary>
public sealed record RecentEntry(Guid Id, string Text, DateTime Date);

/// <summary>
/// The last dictations, in %APPDATA%\Mindtalk\Recent.json — so a paste that landed in the
/// wrong place can be copied again, even after a restart. Never leaves the PC. Newest
/// first. Call it on the UI thread.
/// </summary>
public sealed class RecentStore
{
    public const int Limit = 50;

    public static string DefaultPath { get; } = Path.Combine(AppSettings.Folder, "Recent.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private List<RecentEntry> _entries;

    public RecentStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _entries = Load(_path);
    }

    public IReadOnlyList<RecentEntry> Entries => _entries;
    public RecentEntry? Latest => _entries.Count > 0 ? _entries[0] : null;
    public event Action? Changed;

    private static List<RecentEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(path), Json) ?? [];
            return entries.Where(e => e?.Text is not null).Take(Limit).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void Set(List<RecentEntry> entries)
    {
        _entries = entries;
        try
        {
            if (entries.Count == 0) File.Delete(_path);   // nothing kept, nothing on disk
            else AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public RecentEntry Add(string text, DateTime date)
    {
        var entry = new RecentEntry(Guid.NewGuid(), text, date);
        Set(_entries.Prepend(entry).Take(Limit).ToList());
        return entry;
    }

    public void Remove(Guid id)
    {
        if (_entries.All(e => e.Id != id)) return;
        Set(_entries.Where(e => e.Id != id).ToList());
    }

    public void Clear() => Set([]);

    // ---- How long ago ------------------------------------------------------------

    /// <summary>
    /// "now", "5 min", "9 hr", "2 days", "1 wk" — the compact form menus use, one unit,
    /// never "m" for minutes (metres).
    /// </summary>
    public static string Ago(DateTime date, DateTime now)
    {
        var seconds = (now - date).TotalSeconds;
        if (seconds < 60) return Loc.T("nu", "now");
        // The largest unit that fits, rounded to the nearest (90 min → "2 hr", 59.5 min → "1 hr").
        (double Size, int Next)[] units = [(604_800, int.MaxValue), (86_400, 7), (3_600, 24), (60, 60)];
        var u = Array.FindIndex(units, x => seconds >= x.Size);
        var n = (int)Math.Round(seconds / units[u].Size, MidpointRounding.AwayFromZero);
        if (n >= units[u].Next) { u--; n = 1; }
        return u switch
        {
            0 => Loc.T($"{n} v", $"{n} wk"),
            1 => Loc.T($"{n} d", n == 1 ? "1 day" : $"{n} days"),
            2 => Loc.T($"{n} tim", $"{n} hr"),
            _ => $"{n} min",
        };
    }

    /// <summary>
    /// "4 minutes ago" in the last hour, "Today 10:00 PM", "Yesterday 9:35 PM", then the
    /// date and time.
    /// </summary>
    public static string When(DateTime date, DateTime now)
    {
        var culture = Loc.Culture;
        if ((now - date).TotalSeconds < 3600)
        {
            var s = Relative(date, now);
            return char.ToUpper(s[0], culture) + s[1..];
        }
        var time = date.ToString("t", culture);
        if (date.Date == now.Date) return Loc.T($"I dag {time}", $"Today {time}");
        if (date.Date == now.Date.AddDays(-1)) return Loc.T($"I går {time}", $"Yesterday {time}");
        return Loc.IsSwedish
            ? $"{date.ToString("d MMM yyyy", culture)} {time}"
            : $"{date.ToString("MMM d, yyyy", culture)} at {time}";
    }

    /// Full, numeric relative time like the Mac's RelativeDateTimeFormatter: "4 minutes ago",
    /// "för 4 minuter sedan", "in 0 seconds" (sic — the moment itself is "in").
    private static string Relative(DateTime date, DateTime now)
    {
        var delta = (date - now).TotalSeconds;
        var past = delta < 0;
        var magnitude = (int)Math.Abs(delta);
        var minutes = magnitude >= 60;
        var n = minutes ? magnitude / 60 : magnitude;
        var one = n == 1;
        if (Loc.IsSwedish)
        {
            var unit = minutes ? (one ? "minut" : "minuter") : (one ? "sekund" : "sekunder");
            return past ? $"för {n} {unit} sedan" : $"om {n} {unit}";
        }
        var english = minutes ? (one ? "minute" : "minutes") : (one ? "second" : "seconds");
        return past ? $"{n} {english} ago" : $"in {n} {english}";
    }
}
