using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Shared, as source, by the app (through Mindtalk.Core) and the helper AI apps start
// (mindtalk-mcp.exe): only the base class library is used here.

namespace Mindtalk.Core.AI.Mcp;

/// <summary>
/// The local pipe between the helper and the app: one request per connection, a line of JSON
/// each way. Only your user account can open it (the pipe's ACL), and the helper checks that the
/// app at the other end runs as you too.
/// </summary>
public static class AiPipe
{
    /// The pipe of the Mindtalk installed in <paramref name="folder"/>, for this user. Builds in
    /// different folders (an installed one, a development one) don't answer for each other.
    public static string NameFor(string folder)
    {
        var key = $"{Environment.UserDomainName}\\{Environment.UserName}|{Path.GetFullPath(folder).TrimEnd('\\', '/').ToLowerInvariant()}";
        return "Mindtalk.AiApps." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    /// Sends a request to the app and waits for its answer. Null when the app isn't there.
    public static async Task<JsonObject?> SendAsync(string pipeName, JsonObject request, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                                                         PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectTimeout, ct); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        await WriteAsync(pipe, request, ct);
        return await ReadAsync(pipe, ct);
    }

    public static async Task WriteAsync(Stream stream, JsonNode message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// One line of JSON (at most 32 MB: a long meeting read in full fits easily).
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[64 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            var newline = Array.IndexOf(one, (byte)'\n', 0, n);
            buffer.Write(one, 0, newline >= 0 ? newline : n);
            if (newline >= 0 || buffer.Length > 32 << 20) break;
        }
        if (buffer.Length == 0) return null;
        try { return JsonNode.Parse(buffer.ToArray()) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// A request: which client, which tool, with what.
    public static JsonObject Request(string client, string tool, JsonObject? arguments) =>
        new() { ["client"] = client, ["tool"] = tool, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() };

    public const string NotRunning =
        "Mindtalk isn't running. Open Mindtalk, and turn on AI apps under Settings → AI apps.";
    public const string TurnedOff =
        "AI apps are turned off in Mindtalk. Turn them on under Settings → AI apps to let AI apps read your meetings and thoughts.";
}

/// <summary>A tool AI apps can call: all of them read-only.</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema);

public static class McpTools
{
    public const string Search = "search";
    public const string ListMeetings = "list_meetings";
    public const string ReadMeeting = "read_meeting";
    public const string ListThoughts = "list_thoughts";
    public const string ListTasks = "list_tasks";

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static readonly IReadOnlyList<McpTool> All =
    [
        new(Search, "Search meetings and thoughts",
            "Searches the user's Mindtalk meetings (what was said, summaries, notes) and thoughts for words, in any language. " +
            "Returns the best matching moments, each with a mindtalk:// link to that exact moment. Dictations are never included.",
            Schema(new JsonObject
            {
                ["query"] = Prop("string", "Words to look for, e.g. \"budget\" or \"lansering\"."),
                ["limit"] = Prop("integer", "Most results to return (default 10, at most 50)."),
            }, "query")),
        new(ListMeetings, "List meetings",
            "Lists the user's meetings, newest first: title, date, length, who took part, folder, and a one-line overview. " +
            "Filter by folder or by date. Use read_meeting with an id to read one.",
            Schema(new JsonObject
            {
                ["folder"] = Prop("string", "Only meetings in this folder."),
                ["from"] = Prop("string", "Only meetings on or after this date (YYYY-MM-DD)."),
                ["to"] = Prop("string", "Only meetings on or before this date (YYYY-MM-DD)."),
                ["limit"] = Prop("integer", "Most meetings to return (default 20, at most 200)."),
            })),
        new(ReadMeeting, "Read a meeting",
            "Reads one meeting: its summary (overview, topics, decisions, to-dos with owners, worth remembering) and the full " +
            "transcript with speakers and times, each with a mindtalk:// link. A meeting can't be read while it's being recorded " +
            "or in the two minutes after it ends.",
            Schema(new JsonObject
            {
                ["id"] = Prop("string", "The meeting's id, from list_meetings or search."),
                ["transcript"] = Prop("boolean", "Include the full transcript (default true)."),
            }, "id")),
        new(ListThoughts, "List thoughts",
            "Lists the user's thoughts (short things they said to keep, not dictations), newest first, pinned ones marked.",
            Schema(new JsonObject
            {
                ["limit"] = Prop("integer", "Most thoughts to return (default 50)."),
                ["include_done"] = Prop("boolean", "Include thoughts ticked off as done (default false)."),
            })),
        new(ListTasks, "List open tasks",
            "Lists the to-dos from all meeting summaries that aren't done yet, with who owns each one and a link to the moment it was agreed.",
            Schema(new JsonObject
            {
                ["include_done"] = Prop("boolean", "Include to-dos already ticked off (default false)."),
                ["owner"] = Prop("string", "Only to-dos owned by this person."),
            })),
    ];
}

/// <summary>
/// The MCP side of the helper (stdio, JSON-RPC 2.0): initialize, tools/list, tools/call, ping.
/// Tool calls go to the app through <see cref="AiPipe"/>; everything else is answered here, so an
/// AI app sees Mindtalk's tools even while the app is closed — and gets a polite word when it calls one.
/// </summary>
public sealed class McpSession(Func<string, JsonObject, string, CancellationToken, Task<JsonObject?>> callApp, string version)
{
    public static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// The client's own name (clientInfo.name), e.g. "claude-ai", "cursor".
    public string? ClientName { get; private set; }

    /// <summary>Answers one message; null for notifications (and anything that wants no answer).</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject message, CancellationToken ct = default)
    {
        var id = message["id"]?.DeepClone();
        var method = (string?)message["method"];
        if (method is null) return null;                         // a response to us: we never ask anything
        if (id is null) return null;                             // notifications/initialized, cancelled…

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var p = message["params"] as JsonObject;
                    ClientName = (string?)p?["clientInfo"]?["name"];
                    var asked = (string?)p?["protocolVersion"];
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "mindtalk", ["title"] = "Mindtalk", ["version"] = version },
                        ["instructions"] =
                            "Mindtalk keeps the user's meetings (transcripts with speakers, summaries, notes) and thoughts on their PC. " +
                            "These tools are read-only. Every result has a mindtalk:// link to the exact moment; include it when you cite something " +
                            "so the user can open it in Mindtalk. The user's dictations, audio and voiceprints are never shared.",
                    });
                }
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject
                    {
                        ["tools"] = new JsonArray(McpTools.All.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["title"] = t.Title,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone(),
                            ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false, ["title"] = t.Title },
                        }).ToArray()),
                    });
                case "tools/call":
                {
                    var p = message["params"] as JsonObject;
                    var name = (string?)p?["name"];
                    if (name is null || McpTools.All.All(t => t.Name != name))
                        return Error(id, -32602, $"Unknown tool: {name}");
                    var arguments = p?["arguments"] as JsonObject ?? new JsonObject();
                    var reply = await callApp(name, (JsonObject)arguments.DeepClone(), ClientName ?? "", ct);
                    var text = reply is null ? AiPipe.NotRunning
                        : (bool?)reply["ok"] == true ? (string?)reply["text"] ?? ""
                        : (string?)reply["error"] ?? "Mindtalk couldn't answer.";
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = reply is null || (bool?)reply["ok"] != true,
                    });
                }
                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    public static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>The command line: mindtalk meetings | search &lt;words&gt; | thoughts | tasks, each with --json.</summary>
public static class CommandLine
{
    public const string Client = "command line";

    public const string Help = """
        mindtalk — your Mindtalk meetings and thoughts, from the command line (read-only)

          mindtalk meetings [--folder <name>] [--from YYYY-MM-DD] [--to YYYY-MM-DD] [--limit N]
          mindtalk meeting <id> [--no-transcript]   Read one meeting: its summary and transcript
          mindtalk search <words>                   Search meetings and thoughts
          mindtalk thoughts [--all] [--limit N]     Your thoughts (--all: also the ones ticked off)
          mindtalk tasks [--all] [--owner <name>]   Open to-dos from your meetings

          Add --json to any of them for JSON.

        Turn it on in Mindtalk under Settings → AI apps. Every read is logged there.
        """;

    /// <summary>The tool and arguments for a command line, or null (then show <see cref="Help"/>).</summary>
    public static (string Tool, JsonObject Arguments, bool Json)? Parse(string[] args)
    {
        var json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToList();
        if (rest.Count == 0) return null;
        string? Option(string name)
        {
            var i = rest.IndexOf(name);
            return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null;
        }
        int? Number(string name) => int.TryParse(Option(name), out var n) ? n : null;
        var a = new JsonObject();
        switch (rest[0].ToLowerInvariant())
        {
            case "meetings":
                if (Option("--folder") is { } folder) a["folder"] = folder;
                if (Option("--from") is { } from) a["from"] = from;
                if (Option("--to") is { } to) a["to"] = to;
                if (Number("--limit") is { } limit) a["limit"] = limit;
                return (McpTools.ListMeetings, a, json);
            case "meeting" or "read" when rest.Count > 1:
                a["id"] = rest[1];
                if (rest.Contains("--no-transcript")) a["transcript"] = false;
                return (McpTools.ReadMeeting, a, json);
            case "search" when rest.Count > 1:
                a["query"] = string.Join(" ", rest.Skip(1).Where(w => !w.StartsWith("--", StringComparison.Ordinal)));
                if (Number("--limit") is { } l) a["limit"] = l;
                return (McpTools.Search, a, json);
            case "thoughts":
                if (rest.Contains("--all")) a["include_done"] = true;
                if (Number("--limit") is { } lt) a["limit"] = lt;
                return (McpTools.ListThoughts, a, json);
            case "tasks":
                if (rest.Contains("--all")) a["include_done"] = true;
                if (Option("--owner") is { } owner) a["owner"] = owner;
                return (McpTools.ListTasks, a, json);
            default:
                return null;
        }
    }
}
