using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mindtalk.Core.AI.Mcp;

// mindtalk-mcp.exe — started by AI apps (Claude, Cursor, Codex…) with no arguments: MCP over
// stdio. With a command (meetings, search, thoughts, tasks) it's the `mindtalk` command line.
// Either way it reads nothing itself; it asks the running Mindtalk over its pipe.

var pipe = AiPipe.NameFor(AppContext.BaseDirectory);
var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

if (args.Length == 0 || args[0] is "mcp" or "--mcp" or "--stdio") return await Mcp.RunAsync(pipe, version);
return await Cli.RunAsync(pipe, args);

internal static class Mcp
{
    public static async Task<int> RunAsync(string pipe, string version)
    {
        // stdout carries only JSON-RPC; anything else goes to stderr.
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var session = new McpSession(
            (tool, arguments, client, ct) => AiPipe.SendAsync(pipe, AiPipe.Request(client, tool, arguments), TimeSpan.FromSeconds(2), ct),
            version);

        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await output.WriteLineAsync(McpSession.Error(null, -32700, "Parse error").ToJsonString());
                continue;
            }
            if (message is JsonArray batch)
            {
                var answers = new JsonArray();
                foreach (var item in batch.OfType<JsonObject>())
                    if (await session.HandleAsync(item) is { } a) answers.Add(a);
                if (answers.Count > 0) await output.WriteLineAsync(answers.ToJsonString());
            }
            else if (message is JsonObject single && await session.HandleAsync(single) is { } answer)
                await output.WriteLineAsync(answer.ToJsonString());
        }
        return 0;
    }
}

internal static class Cli
{
    public static async Task<int> RunAsync(string pipe, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args[0] is "-h" or "--help" or "help" || CommandLine.Parse(args) is not { } command)
        {
            Console.WriteLine(CommandLine.Help);
            return args[0] is "-h" or "--help" or "help" ? 0 : 2;
        }

        var reply = await AiPipe.SendAsync(pipe, AiPipe.Request(CommandLine.Client, command.Tool, command.Arguments), TimeSpan.FromSeconds(2));
        if (reply is null)
        {
            Console.Error.WriteLine(AiPipe.NotRunning);
            return 1;
        }
        if ((bool?)reply["ok"] != true)
        {
            Console.Error.WriteLine((string?)reply["error"] ?? "Mindtalk couldn't answer.");
            return 1;
        }
        if (command.Json)
            Console.WriteLine(reply["data"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}");
        else
            Console.WriteLine((string?)reply["text"]);
        return 0;
    }
}
