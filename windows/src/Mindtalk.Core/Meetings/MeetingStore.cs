using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mindtalk.Core.Meetings;

/// <summary>How long a meeting's audio is kept. The text stays until you delete the meeting.</summary>
public enum KeepAudio { Always, Days90, Days30, Days7 }

/// <summary>Where a search matched.</summary>
public enum HitKind { Title, Summary, Note, Text }

/// <summary>A search hit: the meeting, where in it, and a little of the text around.</summary>
public sealed record MeetingHit(Meeting Meeting, HitKind Kind, double? Time, string Snippet);

/// <summary>
/// The meetings on this PC: one folder each under %LOCALAPPDATA%\Mindtalk\Meetings, with
/// meeting.json and the audio. Readable only by your user account (the folder inherits your
/// profile's permissions). Deleted meetings go to the Recycle Bin. Thread-safe; <see cref="Changed"/>
/// is raised on whichever thread saved.
/// </summary>
public sealed class MeetingStore
{
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "Meetings");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Meeting> _meetings = [];
    private List<string> _folders = [];

    /// Moves a folder to the Recycle Bin (set by the platform layer); without it, folders are deleted.
    public Action<string>? Trash { get; set; }

    public MeetingStore(string? folder = null)
    {
        Root = folder ?? DefaultFolder;
        Directory.CreateDirectory(Root);
        Load();
    }

    public string Root { get; }
    public event Action? Changed;

    private string FoldersPath => Path.Combine(Root, "folders.json");

    public string FolderOf(Guid id) => Path.Combine(Root, id.ToString("N"));
    public string FileOf(Guid id) => Path.Combine(FolderOf(id), "meeting.json");

    /// The meeting's audio file, if it's still kept.
    public string? AudioPath(Meeting meeting) =>
        meeting.AudioFile is { } name && File.Exists(Path.Combine(FolderOf(meeting.Id), name)) ? Path.Combine(FolderOf(meeting.Id), name) : null;

    private void Load()
    {
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, "meeting.json");
            try
            {
                if (!File.Exists(file)) continue;
                var meeting = JsonSerializer.Deserialize<Meeting>(File.ReadAllText(file), Json);
                if (meeting is null) continue;
                // Recording when Mindtalk quit: what was written is kept, the rest is lost.
                if (meeting.State is MeetingState.Recording or MeetingState.Paused or MeetingState.Finishing or MeetingState.Summarizing)
                {
                    meeting.State = meeting.Segments.Count > 0 ? MeetingState.Done : MeetingState.Failed;
                    meeting.Error ??= meeting.Segments.Count > 0 ? null : Loc.T("Mindtalk avslutades under mötet.", "Mindtalk quit during the meeting.");
                }
                _meetings[meeting.Id] = meeting;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        try
        {
            if (File.Exists(FoldersPath)) _folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FoldersPath), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    /// Newest first.
    public IReadOnlyList<Meeting> All
    {
        get { lock (_gate) return _meetings.Values.OrderByDescending(m => m.Started).ToList(); }
    }

    public int Count { get { lock (_gate) return _meetings.Count; } }

    public Meeting? Get(Guid id)
    {
        lock (_gate) return _meetings.GetValueOrDefault(id);
    }

    public void Save(Meeting meeting)
    {
        lock (_gate)
        {
            meeting.Modified = DateTime.Now;
            _meetings[meeting.Id] = meeting;
            Directory.CreateDirectory(FolderOf(meeting.Id));
            try { AtomicFile.WriteAllText(FileOf(meeting.Id), JsonSerializer.Serialize(meeting, Json)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    /// To the Recycle Bin, with its audio.
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (!_meetings.Remove(id)) return;
            var folder = FolderOf(id);
            try
            {
                if (Directory.Exists(folder))
                {
                    if (Trash is { } trash) trash(folder);
                    else Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    // ---- Folders ----------------------------------------------------------------------

    /// Folder names: the ones you made, and any a meeting is in.
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
                return _folders.Concat(_meetings.Values.Select(m => m.Folder).OfType<string>())
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(f => f, StringComparer.Create(Loc.Culture, true)).ToList();
        }
    }

    public void AddFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        lock (_gate)
        {
            if (_folders.Contains(name, StringComparer.CurrentCultureIgnoreCase)) return;
            _folders.Add(name);
            SaveFolders();
        }
        Changed?.Invoke();
    }

    public void RenameFolder(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0) return;
        List<Meeting> moved;
        lock (_gate)
        {
            _folders = _folders.Select(f => string.Equals(f, from, StringComparison.CurrentCultureIgnoreCase) ? to : f).ToList();
            SaveFolders();
            moved = _meetings.Values.Where(m => string.Equals(m.Folder, from, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        foreach (var m in moved) { m.Folder = to; Save(m); }
        Changed?.Invoke();
    }

    /// The folder goes; its meetings stay, out of any folder.
    public void RemoveFolder(string name)
    {
        List<Meeting> moved;
        lock (_gate)
        {
            _folders.RemoveAll(f => string.Equals(f, name, StringComparison.CurrentCultureIgnoreCase));
            SaveFolders();
            moved = _meetings.Values.Where(m => string.Equals(m.Folder, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        foreach (var m in moved) { m.Folder = null; Save(m); }
        Changed?.Invoke();
    }

    private void SaveFolders()
    {
        try { AtomicFile.WriteAllText(FoldersPath, JsonSerializer.Serialize(_folders, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Search ----------------------------------------------------------------------

    /// Titles, summaries, notes and what was said — case- and accent-insensitive, best first.
    public IReadOnlyList<MeetingHit> Search(string query, string? folder = null, int limit = 200)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var hits = new List<MeetingHit>();
        foreach (var m in All)
        {
            if (folder is not null && !string.Equals(m.Folder, folder, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (Contains(m.Title, query)) hits.Add(new(m, HitKind.Title, null, m.Title));
            if (m.Summary is { } s)
            {
                var summaryText = new[] { s.Overview }
                    .Concat(s.Topics.SelectMany(t => t.Items.Select(i => i.Text).Prepend(t.Title)))
                    .Concat(s.Decisions.Select(d => d.Text)).Concat(s.Todos.Select(t => t.Text)).Concat(s.Remember.Select(r => r.Text));
                foreach (var line in summaryText)
                    if (Contains(line, query)) { hits.Add(new(m, HitKind.Summary, null, Snip(line, query))); break; }
            }
            foreach (var n in m.Notes)
                if (Contains(n.Text, query)) hits.Add(new(m, HitKind.Note, n.Time, Snip(n.Text, query)));
            foreach (var seg in m.Segments)
                if (Contains(seg.Text, query)) hits.Add(new(m, HitKind.Text, seg.Start, Snip(seg.Text, query)));
        }
        return hits.Take(limit).ToList();
    }

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static bool Contains(string? text, string query) =>
        text is not null && Compare.IndexOf(text, query, Loose) >= 0;

    /// About 120 characters around the first match.
    public static string Snip(string text, string query, int width = 120)
    {
        var at = Compare.IndexOf(text, query, Loose);
        if (at < 0 || text.Length <= width) return text;
        var start = Math.Max(0, at - width / 3);
        var end = Math.Min(text.Length, start + width);
        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        sb.Append(text, start, end - start);
        if (end < text.Length) sb.Append('…');
        return sb.ToString();
    }

    // ---- Keeping audio ---------------------------------------------------------------

    /// Deletes audio older than the chosen time. The text is kept.
    public int ApplyRetention(KeepAudio keep, DateTime now)
    {
        var days = keep switch { KeepAudio.Days90 => 90, KeepAudio.Days30 => 30, KeepAudio.Days7 => 7, _ => 0 };
        if (days == 0) return 0;
        var removed = 0;
        foreach (var m in All.Where(m => m.State is MeetingState.Done or MeetingState.Failed && m.AudioFile is not null && (now - m.Ended).TotalDays > days))
        {
            DeleteAudio(m);
            removed++;
        }
        return removed;
    }

    public void DeleteAudio(Meeting meeting)
    {
        if (AudioPath(meeting) is { } path)
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        meeting.AudioFile = null;
        Save(meeting);
    }

    /// Bytes on disk, everything included.
    public long SizeOnDisk()
    {
        try { return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
