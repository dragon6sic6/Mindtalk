using System.Text.Json.Nodes;
using Mindtalk.Core.AI.Mcp;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;

namespace Mindtalk.Core.Tests;

public class AiAppsRouterTests
{
    private sealed class Data : IAiAppsData
    {
        public List<Meeting> List { get; } = [];
        public IReadOnlyList<Meeting> Meetings => List;
        public IReadOnlyList<Thought> Thoughts { get; set; } = [];
        public Guid? RecordingMeetingId { get; set; }
        public Dictionary<Guid, DateTime> Ended { get; } = [];
        public DateTime? EndedAt(Guid meetingId) => Ended.TryGetValue(meetingId, out var t) ? t : null;
    }

    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0);

    private static (AiAppsRouter Router, Data Data, AccessLog Log) Make(bool enabled = true)
    {
        var data = new Data();
        data.List.Add(TestMeetings.Launch());
        data.Thoughts = [new Thought(Guid.NewGuid(), "Call back about the quote and check the Q4 budget.", Now.AddHours(-2))];
        var log = new AccessLog(null);
        return (new AiAppsRouter(data, log, () => enabled, () => Now), data, log);
    }

    private static JsonObject Call(AiAppsRouter router, string tool, JsonObject? args = null, string client = "claude-ai") =>
        router.Handle(AiPipe.Request(client, tool, args));

    [Fact]
    public void OffMeansNothingIsReadOrLogged()
    {
        var (router, _, log) = Make(enabled: false);
        var reply = Call(router, McpTools.ListMeetings);
        Assert.False((bool)reply["ok"]!);
        Assert.Equal(AiPipe.TurnedOff, (string?)reply["error"]);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void EveryToolAnswersAndEveryReadIsLogged()
    {
        var (router, data, log) = Make();
        var id = data.List[0].Id.ToString("N");
        Assert.True((bool)Call(router, McpTools.Search, new JsonObject { ["query"] = "budget" })["ok"]!);
        Assert.True((bool)Call(router, McpTools.ListMeetings)["ok"]!);
        var read = Call(router, McpTools.ReadMeeting, new JsonObject { ["id"] = id });
        Assert.True((bool)Call(router, McpTools.ListThoughts, client: CommandLine.Client)["ok"]!);
        Assert.True((bool)Call(router, McpTools.ListTasks, client: "cursor-vscode")["ok"]!);

        Assert.Contains("[11:40] Anna: Then we build the meetings feature next.", (string?)read["text"]);
        Assert.Contains($"mindtalk://meeting/{id}", (string?)read["text"]);
        Assert.Equal(["list tasks", "list thoughts", "read meeting", "list meetings", "search"], log.Entries.Select(e => e.Action));
        Assert.Equal(["Cursor", "command line", "Claude", "Claude", "Claude"], log.Entries.Select(e => e.Client));
        Assert.Equal("Weekly meeting about the launch", log.Entries[2].What);
    }

    [Fact]
    public void SearchFindsMeetingsAndThoughtsWithLinksToTheMoment()
    {
        var (router, data, _) = Make();
        var reply = Call(router, McpTools.Search, new JsonObject { ["query"] = "budget" });
        var text = (string)reply["text"]!;
        Assert.Contains($"mindtalk://meeting/{data.List[0].Id:N}?t=980", text);
        Assert.Contains("mindtalk://thought/", text);
        Assert.NotEmpty(reply["data"]!["thoughts"]!.AsArray());
    }

    [Fact]
    public void AMeetingCantBeReadWhileRecordedOrInTheTwoMinutesAfter()
    {
        var (router, data, log) = Make();
        var m = data.List[0];
        var args = new JsonObject { ["id"] = m.Id.ToString("N") };

        data.RecordingMeetingId = m.Id;
        Assert.False((bool)Call(router, McpTools.ReadMeeting, args)["ok"]!);
        Assert.Empty(Call(router, McpTools.Search, new JsonObject { ["query"] = "Thursday" })["data"]!["meetings"]!.AsArray());

        data.RecordingMeetingId = null;
        data.Ended[m.Id] = Now.AddSeconds(-90);
        Assert.False((bool)Call(router, McpTools.ReadMeeting, args)["ok"]!);
        Assert.Contains("Being recorded", (string)Call(router, McpTools.ListMeetings)["text"]!);

        data.Ended[m.Id] = Now.AddSeconds(-121);
        Assert.True((bool)Call(router, McpTools.ReadMeeting, args)["ok"]!);
        Assert.Contains("not shared", log.Entries.Last(e => e.Action == "read meeting").What);
    }

    [Fact]
    public void AMeetingStillBeingFinishedOrJustEndedIsLocked()
    {
        var (router, data, _) = Make();
        var m = data.List[0];
        m.State = MeetingState.Finishing;
        Assert.True(router.IsLocked(m));
        m.State = MeetingState.Done;
        m.Started = Now.AddMinutes(-26);   // 25 minutes long: ended a minute ago
        Assert.True(router.IsLocked(m));
        m.Started = Now.AddMinutes(-30);
        Assert.False(router.IsLocked(m));
    }

    [Fact]
    public void ReadingAnUnknownMeetingSaysSo()
    {
        var (router, _, _) = Make();
        var reply = Call(router, McpTools.ReadMeeting, new JsonObject { ["id"] = Guid.NewGuid().ToString() });
        Assert.False((bool)reply["ok"]!);
        Assert.Contains("no meeting", (string?)reply["error"]);
    }

    [Fact]
    public void TasksAreTheOpenToDosWithOwners()
    {
        var (router, data, _) = Make();
        data.List[0].Summary = new MeetingSummary { Todos = [new("Send the numbers", "Erik", 1100), new("Book a room", null, 20, Done: true)] };
        var text = (string)Call(router, McpTools.ListTasks)["text"]!;
        Assert.Contains("Send the numbers — Erik", text);
        Assert.DoesNotContain("Book a room", text);
        Assert.Contains("Book a room", (string)Call(router, McpTools.ListTasks, new JsonObject { ["include_done"] = true })["text"]!);
    }

    [Fact]
    public void ListMeetingsFiltersByFolderAndDate()
    {
        var (router, data, _) = Make();
        var other = TestMeetings.Swedish("Kundmöte", new DateTime(2026, 10, 1, 9, 0, 0), ("S1", "Hej"));
        other.Folder = "Kunder";
        data.List.Add(other);
        Assert.Equal(1, (int)Call(router, McpTools.ListMeetings, new JsonObject { ["folder"] = "kunder" })["data"]!["count"]!);
        Assert.Equal(1, (int)Call(router, McpTools.ListMeetings, new JsonObject { ["from"] = "2026-11-01" })["data"]!["count"]!);
        Assert.False((bool)Call(router, McpTools.ListMeetings, new JsonObject { ["from"] = "last tuesday" })["ok"]!);
    }

    [Fact]
    public void TheLogIsKeptOnDiskNewestFirstAndCanBeCleared()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mindtalk-log-{Guid.NewGuid():N}.json");
        try
        {
            var log = new AccessLog(path);
            log.Add(new(Now.AddMinutes(-1), "Claude", "search", "budget"));
            log.Add(new(Now, "command line", "list tasks", null));
            var again = new AccessLog(path);
            Assert.Equal(["list tasks", "search"], again.Entries.Select(e => e.Action));
            again.Clear();
            Assert.Empty(new AccessLog(path).Entries);
        }
        finally { File.Delete(path); }
    }
}

public class McpSessionTests
{
    private static async Task<JsonObject?> Send(McpSession session, string json) => await session.HandleAsync(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public async Task InitializeListAndCall()
    {
        var calls = new List<(string Tool, string Client)>();
        var session = new McpSession((tool, args, client, ct) =>
        {
            calls.Add((tool, client));
            return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["text"] = $"answer for {args["query"]}" });
        }, "1.0");

        var init = await Send(session, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"claude-ai"}}}""");
        Assert.Equal("2025-03-26", (string?)init!["result"]!["protocolVersion"]);
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);
        Assert.Null(await Send(session, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var list = await Send(session, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = list!["result"]!["tools"]!.AsArray();
        Assert.Equal(["search", "list_meetings", "read_meeting", "list_thoughts", "list_tasks"], tools.Select(t => (string)t!["name"]!));
        Assert.All(tools, t => Assert.True((bool)t!["annotations"]!["readOnlyHint"]!));

        var call = await Send(session, """{"jsonrpc":"2.0","id":"x","method":"tools/call","params":{"name":"search","arguments":{"query":"budget"}}}""");
        Assert.Equal("x", (string?)call!["id"]);
        Assert.Equal("answer for budget", (string?)call["result"]!["content"]![0]!["text"]);
        Assert.False((bool)call["result"]!["isError"]!);
        Assert.Equal(("search", "claude-ai"), calls.Single());
    }

    [Fact]
    public async Task PoliteWhenTheAppIsntThereOrOff()
    {
        var notRunning = new McpSession((_, _, _, _) => Task.FromResult<JsonObject?>(null), "1");
        var off = new McpSession((_, _, _, _) => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = false, ["error"] = AiPipe.TurnedOff }), "1");
        const string call = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings"}}""";
        var a = (await Send(notRunning, call))!["result"]!;
        var b = (await Send(off, call))!["result"]!;
        Assert.True((bool)a["isError"]!);
        Assert.Equal(AiPipe.NotRunning, (string?)a["content"]![0]!["text"]);
        Assert.Equal(AiPipe.TurnedOff, (string?)b["content"]![0]!["text"]);
    }

    [Fact]
    public async Task UnknownToolsAndMethodsAreErrors()
    {
        var session = new McpSession((_, _, _, _) => throw new InvalidOperationException("not called"), "1");
        Assert.Equal(-32602, (int)(await Send(session, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"delete_everything"}}"""))!["error"]!["code"]!);
        Assert.Equal(-32601, (int)(await Send(session, """{"jsonrpc":"2.0","id":5,"method":"resources/list"}"""))!["error"]!["code"]!);
        Assert.NotNull((await Send(session, """{"jsonrpc":"2.0","id":6,"method":"ping"}"""))!["result"]);
    }
}

public class CommandLineAndLinkTests
{
    [Fact]
    public void CommandsBecomeTools()
    {
        Assert.Equal(McpTools.ListMeetings, CommandLine.Parse(["meetings", "--json"])!.Value.Tool);
        Assert.True(CommandLine.Parse(["meetings", "--json"])!.Value.Json);
        var search = CommandLine.Parse(["search", "höst", "budget"])!.Value;
        Assert.Equal(McpTools.Search, search.Tool);
        Assert.Equal("höst budget", (string?)search.Arguments["query"]);
        Assert.Equal(true, (bool?)CommandLine.Parse(["tasks", "--all"])!.Value.Arguments["include_done"]);
        Assert.Equal(McpTools.ListThoughts, CommandLine.Parse(["thoughts"])!.Value.Tool);
        Assert.Null(CommandLine.Parse(["search"]));
        Assert.Null(CommandLine.Parse(["dictations"]));   // never shared
    }

    [Fact]
    public void LinksRoundTrip()
    {
        var id = Guid.NewGuid();
        var link = MindtalkLink.Parse(MindtalkLink.ForMeeting(id, 153.7))!;
        Assert.Equal((MindtalkLink.Kinds.Meeting, id, 153.0), (link.Kind, link.Id, link.Time));
        Assert.Equal($"mindtalk://meeting/{id:N}?t=153", link.ToString());
        Assert.Equal(MindtalkLink.Kinds.Thought, MindtalkLink.Parse($"mindtalk://thought/{id}")!.Kind);
        Assert.Null(MindtalkLink.Parse("https://example.com/meeting/1"));
        Assert.Null(MindtalkLink.Parse("mindtalk://dictation/" + id));
    }

    [Fact]
    public void ClientNamesReadWell()
    {
        Assert.Equal("Claude", AiAppsRouter.ClientTitle("claude-ai"));
        Assert.Equal("Claude Code", AiAppsRouter.ClientTitle("claude-code"));
        Assert.Equal("ChatGPT (Codex)", AiAppsRouter.ClientTitle("codex-mcp-client"));
        Assert.Equal("AI app", AiAppsRouter.ClientTitle(null));
    }
}
