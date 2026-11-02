using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mindtalk.Core.Voices;

/// <summary>
/// A remembered voice: a name and a voiceprint (a few hundred numbers from the voice model,
/// never audio of the voice).
/// </summary>
public sealed record Voice(Guid Id, string Name, float[] Print, DateTime Created, DateTime Updated, int Samples);

/// <summary>
/// The voices Mindtalk recognizes, in %LOCALAPPDATA%\Mindtalk\voices.json. Only used with
/// Remember voices on. Forget one person or everyone at any time. Thread-safe.
/// </summary>
public sealed class VoiceStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "voices.json");

    /// Cosine similarity a voice has to reach to be taken for a remembered one.
    public const float MatchThreshold = 0.62f;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<Voice> _voices;

    public VoiceStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _voices = Load(_path);
    }

    public IReadOnlyList<Voice> All { get { lock (_gate) return _voices.ToList(); } }
    public event Action? Changed;

    private static List<Voice> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Voice>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_voices.Count == 0) File.Delete(_path);
            else AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_voices, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// Remembers a voice under a name — or, if that name is known, blends the new print in.
    public Voice Remember(string name, float[] print)
    {
        Voice voice;
        lock (_gate)
        {
            var existing = _voices.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase));
            var unit = Normalize(print);
            if (existing is null)
            {
                voice = new Voice(Guid.NewGuid(), name, unit, DateTime.Now, DateTime.Now, 1);
                _voices.Add(voice);
            }
            else
            {
                // A running mean, capped so a voice keeps adapting a little.
                var n = Math.Min(existing.Samples, 9);
                var blended = existing.Print.Zip(unit, (a, b) => (a * n + b) / (n + 1)).ToArray();
                voice = existing with { Print = Normalize(blended), Updated = DateTime.Now, Samples = existing.Samples + 1, Name = name };
                _voices[_voices.IndexOf(existing)] = voice;
            }
            Save();
        }
        Changed?.Invoke();
        return voice;
    }

    public void Rename(Guid id, string name)
    {
        lock (_gate)
        {
            var i = _voices.FindIndex(v => v.Id == id);
            if (i < 0) return;
            _voices[i] = _voices[i] with { Name = name };
            Save();
        }
        Changed?.Invoke();
    }

    public void Forget(Guid id)
    {
        lock (_gate) { _voices.RemoveAll(v => v.Id == id); Save(); }
        Changed?.Invoke();
    }

    public void ForgetAll()
    {
        lock (_gate) { _voices.Clear(); Save(); }
        Changed?.Invoke();
    }

    /// The remembered voice closest to this print, if it's close enough.
    public (Voice Voice, float Score)? Match(float[] print, float threshold = MatchThreshold, IEnumerable<Guid>? exclude = null)
    {
        var skip = exclude?.ToHashSet() ?? [];
        var unit = Normalize(print);
        lock (_gate)
        {
            (Voice, float)? best = null;
            foreach (var v in _voices)
            {
                if (skip.Contains(v.Id) || v.Print.Length != unit.Length) continue;
                var score = Cosine(v.Print, unit);
                if (score >= threshold && (best is null || score > best.Value.Item2)) best = (v, score);
            }
            return best;
        }
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v.ToArray() : v.Select(x => (float)(x / norm)).ToArray();
    }
}

/// <summary>
/// Names to suggest when you name a speaker (2.3): voices it recognizes, names from earlier
/// meetings, and your own name. Matching ignores case and accents; the suggestion keeps the
/// spelling it was saved with ("an" → "Anna", "o" → "Örjan" only if typed "ö"… or "o").
/// </summary>
public static class NameSuggestions
{
    public static IReadOnlyList<string> Known(IEnumerable<string?> voices, IEnumerable<string?> fromMeetings, string? me)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var names = new List<string>();
        foreach (var name in voices.Concat(me is null ? [] : [me]).Concat(fromMeetings))
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
            names.Add(n);
        }
        return names.OrderBy(n => n, StringComparer.Create(Loc.Culture, true)).ToList();
    }

    /// The first known name that starts with what's typed, or null.
    public static string? Complete(string typed, IEnumerable<string> known)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        return Filter(typed, known).FirstOrDefault(n => n.Length > typed.Length);
    }

    /// Known names that start with what's typed (all of them for an empty field).
    public static IEnumerable<string> Filter(string typed, IEnumerable<string> known)
    {
        typed = typed.TrimStart();
        if (typed.Length == 0) return known;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        return known.Where(n => compare.IsPrefix(n, typed, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));
    }
}
