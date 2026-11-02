using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;

namespace Mindtalk.Core.AI.Mcp;

/// <summary>A link to a moment in Mindtalk: mindtalk://meeting/&lt;id&gt;?t=153, mindtalk://thought/&lt;id&gt;.</summary>
public sealed record MindtalkLink(MindtalkLink.Kinds Kind, Guid Id, double? Time = null)
{
    public enum Kinds { Meeting, Thought }

    public const string Scheme = "mindtalk";

    public static string ForMeeting(Guid id, double? time = null) =>
        $"{Scheme}://meeting/{id:N}" + (time is { } t ? $"?t={(long)Math.Floor(Math.Max(0, t))}" : "");

    public static string ForThought(Guid id) => $"{Scheme}://thought/{id:N}";

    public override string ToString() => Kind == Kinds.Meeting ? ForMeeting(Id, Time) : ForThought(Id);

    public static MindtalkLink? Parse(string? text)
    {
        if (text is null || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        return Parse(uri);
    }

    public static MindtalkLink? Parse(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var kind = uri.Host.ToLowerInvariant() switch
        {
            "meeting" => Kinds.Meeting,
            "thought" => Kinds.Thought,
            _ => (Kinds?)null,
        };
        if (kind is null || !Guid.TryParse(uri.AbsolutePath.Trim('/'), out var id)) return null;
        double? time = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("t=", StringComparison.Ordinal) && double.TryParse(part[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t >= 0)
                time = t;
        return new MindtalkLink(kind.Value, id, kind == Kinds.Meeting ? time : null);
    }
}

/// <summary>
/// Every read by an AI app or the command line, newest first, kept on disk for Settings → AI apps
/// (the last 500). Thread-safe; <see cref="Changed"/> is raised on the thread that added.
/// </summary>
public sealed class AccessLog
{
    public const int Max = 500;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string? _path;
    private readonly object _gate = new();
    private List<AccessLogEntry> _entries = [];

    public AccessLog(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path))
                _entries = (JsonSerializer.Deserialize<List<AccessLogEntry>>(File.ReadAllText(path), Json) ?? [])
                    .OrderByDescending(e => e.When).Take(Max).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public static string DefaultPath { get; } = Path.Combine(AppSettings.Folder, "ai-apps-log.json");

    public event Action? Changed;

    public IReadOnlyList<AccessLogEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    public void Add(AccessLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > Max) _entries.RemoveRange(Max, _entries.Count - Max);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); Save(); }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path is null) return;
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>What AI apps may read: meetings and thoughts. Never dictations, audio or voiceprints.</summary>
public interface IAiAppsData
{
    IReadOnlyList<Meeting> Meetings { get; }
    IReadOnlyList<Thought> Thoughts { get; }
    /// The meeting being recorded now, if any.
    Guid? RecordingMeetingId { get; }
    /// When a meeting's recording ended, if Mindtalk saw it end (this session).
    DateTime? EndedAt(Guid meetingId);
}

/// <summary>
/// Answers the helper's requests: the five read-only tools, as text that reads well (and JSON for
/// --json). The rules are the Mac's: only when AI apps are on; a meeting can't be read while it's
/// being recorded or in the two minutes after; every read is logged.
/// </summary>
public sealed class AiAppsRouter(IAiAppsData data, AccessLog log, Func<bool> enabled, Func<DateTime>? clock = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);
    private DateTime Now => (clock ?? (() => DateTime.Now))();

    /// "claude-ai" → "Claude", "cursor-vscode" → "Cursor"…
    public static string ClientTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "AI app";
        var r = raw.Trim().ToLowerInvariant();
        if (r == CommandLine.Client) return CommandLine.Client;
        if (r.Contains("claude-code") || r.Contains("claude code")) return "Claude Code";
        if (r.Contains("claude")) return "Claude";
        if (r.Contains("codex")) return "ChatGPT (Codex)";
        if (r.Contains("chatgpt") || r.Contains("openai")) return "ChatGPT";
        if (r.Contains("cursor")) return "Cursor";
        if (r.Contains("visual studio code") || r == "vscode" || r.Contains("copilot")) return "VS Code";
        if (r.Contains("windsurf")) return "Windsurf";
        if (r.Contains("zed")) return "Zed";
        return raw.Trim().Length > 40 ? raw.Trim()[..40] : raw.Trim();
    }

    /// <summary>Being recorded, being finished, or ended less than two minutes ago.</summary>
    public bool IsLocked(Meeting m)
    {
        if (data.RecordingMeetingId == m.Id) return true;
        if (m.State is MeetingState.Recording or MeetingState.Paused or MeetingState.Finishing) return true;
        var now = Now;
        if (data.EndedAt(m.Id) is { } ended && now - ended < Quiet) return true;
        // Recorded (not imported): its end is at least its start plus its length.
        return m.Source != MeetingSource.Import && now - m.Ended < Quiet && now >= m.Started;
    }

    /// <summary>Handles one request from the pipe: {client, tool, arguments} → {ok, text, data} or {ok: false, error}.</summary>
    public JsonObject Handle(JsonObject request)
    {
        var client = ClientTitle((string?)request["client"]);
        var tool = (string?)request["tool"] ?? "";
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        if (!enabled()) return Fail(AiPipe.TurnedOff);
        try
        {
            return tool switch
            {
                McpTools.Search => Search(client, args),
                McpTools.ListMeetings => ListMeetings(client, args),
                McpTools.ReadMeeting => ReadMeeting(client, args),
                McpTools.ListThoughts => ListThoughts(client, args),
                McpTools.ListTasks => ListTasks(client, args),
                _ => Fail($"Unknown tool: {tool}"),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private static JsonObject Ok(string text, JsonNode data) => new() { ["ok"] = true, ["text"] = text, ["data"] = data };
    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    private void Log(string client, string action, string? what) => log.Add(new AccessLogEntry(Now, client, action, what));

    private static int Limit(JsonObject args, string key, int fallback, int max) =>
        Math.Clamp(args[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n
                   : args[key] is JsonValue s && s.TryGetValue<string>(out var str) && int.TryParse(str, out var m) ? m : fallback, 1, max);

    private static bool Flag(JsonObject args, string key, bool fallback) =>
        args[key] is JsonValue v ? v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) ? s is "true" or "1" : fallback : fallback;

    private static string Date(DateTime d) => d.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static List<string> People(Meeting m) => m.Talkers.Select(s => s.DisplayName).ToList();

    // ---- search -------------------------------------------------------------------------

    private JsonObject Search(string client, JsonObject args)
    {
        var query = ((string?)args["query"])?.Trim();
        if (string.IsNullOrEmpty(query)) return Fail("Give some words to search for.");
        var limit = Limit(args, "limit", 10, 50);
        Log(client, "search", query);

        var meetings = data.Meetings.Where(m => !IsLocked(m)).ToList();
        var hits = Retrieval.Search(query, meetings, Now, limit);
        // Plain words too (a name, a number), in case the ranking found nothing.
        if (hits.Count == 0)
            hits = meetings.SelectMany(Retrieval.Passages)
                .Where(p => MeetingStore.Contains(p.Text, query)).Take(limit).Select(p => new PassageHit(p, 1)).ToList();

        var words = TextMatch.ContentWords(query);
        var thoughts = data.Thoughts
            .Select(t => (Thought: t, Score: MeetingStore.Contains(t.Text, query) ? 100 : words.Count(w => TextMatch.Words(t.Text).Any(x => TextMatch.SameWord(w, x) || x.Contains(w, StringComparison.Ordinal)))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => x.Thought.Created).Take(Math.Max(3, limit / 2)).Select(x => x.Thought).ToList();

        var sb = new StringBuilder();
        var count = hits.Count + thoughts.Count;
        if (count == 0) sb.Append($"Nothing in your meetings or thoughts matches \"{query}\".");
        else sb.Append($"{count} {(count == 1 ? "match" : "matches")} for \"{query}\"\n");
        var meetingData = new JsonArray();
        if (hits.Count > 0)
        {
            sb.Append("\nMeetings\n");
            foreach (var h in hits)
            {
                var p = h.Passage;
                var link = MindtalkLink.ForMeeting(p.Meeting.Id, p.Time);
                sb.Append($"• {p.Meeting.Title} — {Date(p.Meeting.Started)}, at {Meeting.Clock(p.Time)}{KindLabel(p.Kind)}\n");
                sb.Append($"  {(p.Speaker is not null && p.Kind == PassageKind.Said ? p.Speaker + ": " : "")}\"{p.Text}\"\n  {link}\n");
                meetingData.Add(new JsonObject
                {
                    ["meeting_id"] = p.Meeting.Id.ToString("N"), ["meeting"] = p.Meeting.Title, ["started"] = Iso(p.Meeting.Started),
                    ["time"] = Math.Floor(p.Time), ["kind"] = p.Kind.ToString().ToLowerInvariant(), ["speaker"] = p.Speaker,
                    ["text"] = p.Text, ["link"] = link,
                });
            }
        }
        var thoughtData = new JsonArray();
        if (thoughts.Count > 0)
        {
            sb.Append("\nThoughts\n");
            foreach (var t in thoughts)
            {
                sb.Append($"• \"{t.Text}\" — {Date(t.Created)}\n  {MindtalkLink.ForThought(t.Id)}\n");
                thoughtData.Add(ThoughtJson(t));
            }
        }
        return Ok(sb.ToString().TrimEnd(), new JsonObject { ["query"] = query, ["meetings"] = meetingData, ["thoughts"] = thoughtData });
    }

    private static string KindLabel(PassageKind kind) => kind switch
    {
        PassageKind.Overview => " (summary)",
        PassageKind.Point => " (summary)",
        PassageKind.Decision => " (decision)",
        PassageKind.Todo => " (to-do)",
        PassageKind.Remember => " (worth remembering)",
        PassageKind.Note => " (your note)",
        _ => "",
    };

    // ---- list_meetings ----------------------------------------------------------------

    private JsonObject ListMeetings(string client, JsonObject args)
    {
        var folder = ((string?)args["folder"])?.Trim();
        var from = ParseDate((string?)args["from"], "from");
        var to = ParseDate((string?)args["to"], "to");
        var limit = Limit(args, "limit", 20, 200);
        var filter = string.Join(", ", new[] { folder is null ? null : $"folder {folder}", from is null ? null : $"from {args["from"]}", to is null ? null : $"to {args["to"]}" }.OfType<string>());
        Log(client, "list meetings", filter.Length > 0 ? filter : null);

        var list = data.Meetings
            .Where(m => folder is null || string.Equals(m.Folder, folder, StringComparison.CurrentCultureIgnoreCase))
            .Where(m => from is null || m.Started.Date >= from.Value)
            .Where(m => to is null || m.Started.Date <= to.Value)
            .OrderByDescending(m => m.Started).ToList();
        var shown = list.Take(limit).ToList();

        var sb = new StringBuilder();
        if (shown.Count == 0) sb.Append(folder is null && from is null && to is null ? "There are no meetings yet." : "No meetings match.");
        else sb.Append(list.Count > shown.Count ? $"{shown.Count} of {list.Count} meetings, newest first\n" : $"{shown.Count} {(shown.Count == 1 ? "meeting" : "meetings")}, newest first\n");
        var array = new JsonArray();
        foreach (var m in shown)
        {
            var locked = IsLocked(m);
            sb.Append($"\n• {(m.Title.Length > 0 ? m.Title : "Untitled meeting")} — {Date(m.Started)}, {Meeting.Length(m.Duration)}");
            if (m.Folder is not null) sb.Append($" · folder: {m.Folder}");
            sb.Append($"\n  id: {m.Id:N}\n");
            if (locked) sb.Append("  Being recorded — it can be read two minutes after it ends.\n");
            else
            {
                var people = People(m);
                if (people.Count > 0) sb.Append($"  With {string.Join(", ", people)}\n");
                if (m.Summary?.Overview is { Length: > 0 } overview) sb.Append($"  {FirstSentence(overview)}\n");
            }
            sb.Append($"  {MindtalkLink.ForMeeting(m.Id)}\n");
            array.Add(new JsonObject
            {
                ["id"] = m.Id.ToString("N"), ["title"] = m.Title, ["started"] = Iso(m.Started), ["duration_seconds"] = Math.Round(m.Duration),
                ["folder"] = m.Folder, ["people"] = locked ? new JsonArray() : new JsonArray(People(m).Select(p => (JsonNode)p).ToArray()),
                ["overview"] = locked ? null : m.Summary?.Overview, ["readable"] = !locked, ["link"] = MindtalkLink.ForMeeting(m.Id),
            });
        }
        return Ok(sb.ToString().TrimEnd(), new JsonObject { ["count"] = list.Count, ["meetings"] = array });
    }

    private static DateTime? ParseDate(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d.Date;
        throw new FormatException($"\"{name}\" should be a date like 2026-11-02.");
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOfAny(['.', '!', '?']);
        return end > 0 && end < text.Length - 1 ? text[..(end + 1)] : text;
    }

    // ---- read_meeting -----------------------------------------------------------------

    private JsonObject ReadMeeting(string client, JsonObject args)
    {
        var idText = ((string?)args["id"])?.Trim() ?? "";
        // A link works as well as an id.
        var id = MindtalkLink.Parse(idText) is { Kind: MindtalkLink.Kinds.Meeting } link ? link.Id
               : Guid.TryParse(idText, out var g) ? g : (Guid?)null;
        var m = id is null ? null : data.Meetings.FirstOrDefault(x => x.Id == id);
        if (m is null)
        {
            Log(client, "read meeting", $"{idText} (not found)");
            return Fail($"There's no meeting with id {idText}. Use list_meetings or search to find one.");
        }
        if (IsLocked(m))
        {
            Log(client, "read meeting", $"{m.Title} (not shared: being recorded)");
            return Fail($"\"{m.Title}\" is being recorded, or ended less than two minutes ago. It can be read two minutes after it ends.");
        }
        Log(client, "read meeting", m.Title);
        var transcript = Flag(args, "transcript", true);

        var sb = new StringBuilder();
        sb.Append($"# {(m.Title.Length > 0 ? m.Title : "Untitled meeting")}\n{Date(m.Started)} · {Meeting.Length(m.Duration)}");
        var people = People(m);
        if (people.Count > 0) sb.Append($" · {string.Join(", ", people)}");
        if (m.Folder is not null) sb.Append($" · folder: {m.Folder}");
        sb.Append($"\n{MindtalkLink.ForMeeting(m.Id)}\n");

        string At(double? t) => t is { } v ? $" [{Meeting.Clock(v)}]({MindtalkLink.ForMeeting(m.Id, v)})" : "";
        var json = new JsonObject
        {
            ["id"] = m.Id.ToString("N"), ["title"] = m.Title, ["started"] = Iso(m.Started), ["duration_seconds"] = Math.Round(m.Duration),
            ["folder"] = m.Folder, ["people"] = new JsonArray(people.Select(p => (JsonNode)p).ToArray()), ["link"] = MindtalkLink.ForMeeting(m.Id),
        };

        if (m.Summary is { } s)
        {
            sb.Append($"\n## Summary\n{s.Overview}\n");
            foreach (var t in s.Topics)
            {
                sb.Append($"\n### {t.Title}\n");
                foreach (var i in t.Items) sb.Append($"- {i.Text}{At(i.Time)}\n");
            }
            if (s.Decisions.Count > 0)
            {
                sb.Append("\n## Decisions\n");
                foreach (var d in s.Decisions) sb.Append($"- {d.Text}{At(d.Time)}\n");
            }
            if (s.Todos.Count > 0)
            {
                sb.Append("\n## To do\n");
                foreach (var t in s.Todos) sb.Append($"- [{(t.Done ? "x" : " ")}] {t.Text}{(t.Owner is null ? "" : $" — {t.Owner}")}{At(t.Time)}\n");
            }
            if (s.Remember.Count > 0)
            {
                sb.Append("\n## Worth remembering\n");
                foreach (var r in s.Remember) sb.Append($"- {r.Text}{At(r.Time)}\n");
            }
            json["summary"] = new JsonObject
            {
                ["overview"] = s.Overview,
                ["topics"] = new JsonArray(s.Topics.Select(t => (JsonNode)new JsonObject
                {
                    ["title"] = t.Title, ["points"] = new JsonArray(t.Items.Select(i => (JsonNode)ItemJson(m, i.Text, i.Time)).ToArray()),
                }).ToArray()),
                ["decisions"] = new JsonArray(s.Decisions.Select(d => (JsonNode)ItemJson(m, d.Text, d.Time)).ToArray()),
                ["todos"] = new JsonArray(s.Todos.Select(t =>
                {
                    var o = ItemJson(m, t.Text, t.Time);
                    o["owner"] = t.Owner;
                    o["done"] = t.Done;
                    return (JsonNode)o;
                }).ToArray()),
                ["remember"] = new JsonArray(s.Remember.Select(r => (JsonNode)ItemJson(m, r.Text, r.Time)).ToArray()),
                ["written_by"] = s.Writer,
            };
        }
        else sb.Append("\nNo summary yet.\n");

        if (m.Notes.Count > 0)
        {
            sb.Append("\n## Notes taken during the meeting\n");
            foreach (var n in m.Notes) sb.Append($"- {n.Text}{At(n.Time)}\n");
            json["notes"] = new JsonArray(m.Notes.Select(n => (JsonNode)ItemJson(m, n.Text, n.Time)).ToArray());
        }

        if (transcript)
        {
            var lines = SummaryGrounding.Lines(m);
            sb.Append("\n## Transcript\n");
            foreach (var l in lines) sb.Append($"[{Meeting.Clock(l.Segment.Start)}] {l.Speaker}: {l.Text}\n");
            json["transcript"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
            {
                ["time"] = Math.Floor(l.Segment.Start), ["speaker"] = l.Speaker, ["text"] = l.Text,
            }).ToArray());
            sb.Append($"\nEach line's moment opens with {MindtalkLink.ForMeeting(m.Id)}?t=<seconds>.\n");
        }
        return Ok(sb.ToString().TrimEnd(), json);
    }

    private static JsonObject ItemJson(Meeting m, string text, double? time) => new()
    {
        ["text"] = text, ["time"] = time is { } t ? Math.Floor(t) : null, ["link"] = MindtalkLink.ForMeeting(m.Id, time),
    };

    // ---- list_thoughts ----------------------------------------------------------------

    private JsonObject ListThoughts(string client, JsonObject args)
    {
        var limit = Limit(args, "limit", 50, 500);
        var all = Flag(args, "include_done", false);
        Log(client, "list thoughts", null);
        var list = data.Thoughts.Where(t => all || !t.Done)
            .OrderByDescending(t => t.Pinned).ThenByDescending(t => t.Created).Take(limit).ToList();
        var sb = new StringBuilder(list.Count == 0 ? "No thoughts yet." : $"{list.Count} {(list.Count == 1 ? "thought" : "thoughts")}\n");
        foreach (var t in list)
        {
            var marks = (t.Pinned ? " (pinned)" : "") + (t.Done ? " (done)" : "");
            sb.Append($"\n• {t.Text.Replace("\n", "\n  ")}{marks}\n  {Date(t.Created)} · {MindtalkLink.ForThought(t.Id)}\n");
        }
        return Ok(sb.ToString().TrimEnd(), new JsonObject { ["thoughts"] = new JsonArray(list.Select(t => (JsonNode)ThoughtJson(t)).ToArray()) });
    }

    private static JsonObject ThoughtJson(Thought t) => new()
    {
        ["id"] = t.Id.ToString("N"), ["text"] = t.Text, ["created"] = Iso(t.Created), ["pinned"] = t.Pinned, ["done"] = t.Done,
        ["link"] = MindtalkLink.ForThought(t.Id),
    };

    // ---- list_tasks -------------------------------------------------------------------

    private JsonObject ListTasks(string client, JsonObject args)
    {
        var all = Flag(args, "include_done", false);
        var owner = ((string?)args["owner"])?.Trim();
        Log(client, "list tasks", owner);
        var tasks = data.Meetings.Where(m => !IsLocked(m) && m.Summary is not null)
            .OrderByDescending(m => m.Started)
            .SelectMany(m => m.Summary!.Todos.Select(t => (Meeting: m, Task: t)))
            .Where(x => all || !x.Task.Done)
            .Where(x => owner is null || (x.Task.Owner is { } o && TextMatch.Fold(o).StartsWith(TextMatch.Fold(owner), StringComparison.Ordinal)))
            .ToList();
        var sb = new StringBuilder(tasks.Count == 0 ? (all ? "No to-dos in your meetings." : "No open to-dos in your meetings.")
                                                    : $"{tasks.Count} {(all ? "" : "open ")}{(tasks.Count == 1 ? "to-do" : "to-dos")}\n");
        foreach (var group in tasks.GroupBy(x => x.Meeting))
        {
            sb.Append($"\n{group.Key.Title} — {Date(group.Key.Started)}\n");
            foreach (var (m, t) in group)
                sb.Append($"• [{(t.Done ? "x" : " ")}] {t.Text}{(t.Owner is null ? "" : $" — {t.Owner}")}\n  {MindtalkLink.ForMeeting(m.Id, t.Time)}\n");
        }
        return Ok(sb.ToString().TrimEnd(), new JsonObject
        {
            ["tasks"] = new JsonArray(tasks.Select(x => (JsonNode)new JsonObject
            {
                ["text"] = x.Task.Text, ["owner"] = x.Task.Owner, ["done"] = x.Task.Done, ["meeting_id"] = x.Meeting.Id.ToString("N"),
                ["meeting"] = x.Meeting.Title, ["started"] = Iso(x.Meeting.Started), ["time"] = x.Task.Time is { } t ? Math.Floor(t) : null,
                ["link"] = MindtalkLink.ForMeeting(x.Meeting.Id, x.Task.Time),
            }).ToArray()),
        });
    }
}
