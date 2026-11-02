using System.Text;
using System.Text.Json;

namespace Mindtalk.Core.Thoughts;

/// <summary>A thought: something you said that's kept on the Thoughts page instead of typed.</summary>
public sealed record Thought(Guid Id, string Text, DateTime Created, bool Pinned = false, bool Done = false);

/// <summary>
/// Your thoughts, in %APPDATA%\Mindtalk\thoughts.json. Only the text is kept — the sound is
/// gone once the text is written. Newest first. Call it on the UI thread.
/// </summary>
public sealed class ThoughtStore
{
    public static string DefaultPath { get; } = Path.Combine(AppSettings.Folder, "thoughts.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private List<Thought> _thoughts;

    public ThoughtStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _thoughts = Load(_path);
    }

    public IReadOnlyList<Thought> All => _thoughts;
    public int Count => _thoughts.Count;
    /// Not ticked off: what the sidebar counts.
    public int OpenCount => _thoughts.Count(t => !t.Done);
    public event Action? Changed;

    private static List<Thought> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<List<Thought>>(File.ReadAllText(path), Json) ?? [])
                .Where(t => t?.Text is not null).OrderByDescending(t => t.Created).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void Set(IEnumerable<Thought> thoughts)
    {
        _thoughts = thoughts.OrderByDescending(t => t.Created).ToList();
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_thoughts, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public Thought? Get(Guid id) => _thoughts.FirstOrDefault(t => t.Id == id);

    public Thought? Add(string text, DateTime created)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var thought = new Thought(Guid.NewGuid(), text, created);
        Set(_thoughts.Prepend(thought));
        return thought;
    }

    private void Change(Guid id, Func<Thought, Thought> change) =>
        Set(_thoughts.Select(t => t.Id == id ? change(t) : t));

    public void Edit(Guid id, string text)
    {
        text = text.Trim();
        if (text.Length == 0) { Delete(id); return; }
        Change(id, t => t with { Text = text });
    }

    public void SetPinned(Guid id, bool pinned) => Change(id, t => t with { Pinned = pinned });
    public void SetDone(Guid id, bool done) => Change(id, t => t with { Done = done });

    public void Delete(Guid id) => Set(_thoughts.Where(t => t.Id != id));
    public void Delete(IEnumerable<Guid> ids) { var set = ids.ToHashSet(); Set(_thoughts.Where(t => !set.Contains(t.Id))); }
    public void Clear() => Set([]);

    /// Several thoughts become one: oldest first, a line each, at the oldest one's time.
    public Thought? Merge(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var parts = _thoughts.Where(t => set.Contains(t.Id)).OrderBy(t => t.Created).ToList();
        if (parts.Count < 2) return null;
        var merged = new Thought(Guid.NewGuid(), string.Join("\n", parts.Select(p => p.Text)), parts[0].Created,
                                 parts.Any(p => p.Pinned), parts.All(p => p.Done));
        Set(_thoughts.Where(t => !set.Contains(t.Id)).Append(merged));
        return merged;
    }

    public IReadOnlyList<Thought> Search(string query) =>
        string.IsNullOrWhiteSpace(query) ? _thoughts : _thoughts.Where(t => Meetings.MeetingStore.Contains(t.Text, query.Trim())).ToList();

    /// All thoughts (or some) as Markdown: a heading per day, a bullet per thought.
    public static string Markdown(IEnumerable<Thought> thoughts)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Loc.T("Tankar", "Thoughts")).Append("\n");
        foreach (var day in thoughts.OrderByDescending(t => t.Created).GroupBy(t => t.Created.Date))
        {
            sb.Append("\n## ").Append(day.Key.ToString("D", Loc.Culture)).Append("\n\n");
            foreach (var t in day)
            {
                var box = t.Done ? "[x] " : "";
                var pin = t.Pinned ? " 📌" : "";
                var lines = t.Text.Split('\n');
                sb.Append("- ").Append(box).Append(lines[0]).Append(pin).Append(" (").Append(t.Created.ToString("t", Loc.Culture)).Append(")\n");
                foreach (var line in lines.Skip(1)) sb.Append("  ").Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }
}
