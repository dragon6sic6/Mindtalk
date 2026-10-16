using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mindtalk.Core.Stats;

/// <summary>One day's dictation.</summary>
public sealed record DayStat
{
    public int Words { get; init; }
    public double Seconds { get; init; }
    public int Dictations { get; init; }

    /// Time saved against typing at <see cref="StatsStore.TypingWordsPerMinute"/>.
    [JsonIgnore]
    public double SavedSeconds => Math.Max(0, Words / StatsStore.TypingWordsPerMinute * 60 - Seconds);
}

/// <summary>A day of this week, for the chart.</summary>
public sealed record WeekDay(DateTime Date, DayStat Stat, bool IsToday, bool IsFuture);

/// <summary>
/// Words per day, kept on this PC only (never the text itself), in
/// %APPDATA%\Mindtalk\stats.json. "Saved time" compares typing the same words at 40 words a
/// minute — a typical pace — with the time you actually spent talking. Port of the Mac's Stats.
/// </summary>
public sealed class StatsStore
{
    public const double TypingWordsPerMinute = 40;
    private const int KeepDays = 400;

    public static string DefaultPath { get; } = Path.Combine(AppSettings.Folder, "stats.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Func<DateTime> _now;
    private Dictionary<string, DayStat> _days;

    /// <param name="clock">Local time now (tests pass their own).</param>
    public StatsStore(string? path = null, Func<DateTime>? clock = null)
    {
        _path = path ?? DefaultPath;
        _now = clock ?? (() => DateTime.Now);
        _days = Load(_path);
    }

    /// "2026-09-25" → that day.
    public IReadOnlyDictionary<string, DayStat> Days => _days;

    public event Action? Changed;

    private static Dictionary<string, DayStat> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DayStat>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void Save()
    {
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_days, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Record(string text, double seconds)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0) return;
        var key = Key(_now());
        var day = _days.GetValueOrDefault(key) ?? new DayStat();
        var updated = new Dictionary<string, DayStat>(_days)
        {
            [key] = day with { Words = day.Words + words, Seconds = day.Seconds + seconds, Dictations = day.Dictations + 1 },
        };
        if (updated.Count > KeepDays)
            foreach (var old in updated.Keys.Order(StringComparer.Ordinal).Take(updated.Count - KeepDays).ToList())
                updated.Remove(old);
        _days = updated;
        Save();
        Changed?.Invoke();
    }

    public DayStat Today => _days.GetValueOrDefault(Key(_now())) ?? new DayStat();
    public int TotalWords => _days.Values.Sum(d => d.Words);
    public double TotalSaved => _days.Values.Sum(d => d.SavedSeconds);

    /// <summary>This calendar week, Monday to Sunday (Swedish weeks, whatever the language).</summary>
    public IReadOnlyList<WeekDay> Week
    {
        get
        {
            var now = _now();
            var start = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            return Enumerable.Range(0, 7).Select(offset =>
            {
                var date = start.AddDays(offset);
                var isToday = date == now.Date;
                return new WeekDay(date, _days.GetValueOrDefault(Key(date)) ?? new DayStat(), isToday, !isToday && date > now);
            }).ToList();
        }
    }

    /// ISO 8601 week number.
    public int WeekNumber => ISOWeek.GetWeekOfYear(_now());
    public int WeekWords => Week.Sum(d => d.Stat.Words);

    /// <summary>Days in a row with at least one dictation, counting today (or up to yesterday).</summary>
    public int Streak
    {
        get
        {
            var date = _now().Date;
            if (!_days.ContainsKey(Key(date))) date = date.AddDays(-1);
            var count = 0;
            while (_days.ContainsKey(Key(date)))
            {
                count++;
                date = date.AddDays(-1);
            }
            return count;
        }
    }

    /// <summary>"40 min", "2 h 5 min", "45 s".</summary>
    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (s < 60) return $"{s} s";
        var minutes = s / 60;
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>"12 345" in Swedish, "12,345" in English.</summary>
    public static string Number(int n) => n.ToString("N0", Loc.Culture);
}
