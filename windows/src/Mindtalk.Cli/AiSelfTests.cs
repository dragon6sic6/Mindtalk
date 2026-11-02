using System.Diagnostics;
using System.Text.Json;
using Mindtalk.Core;
using Mindtalk.Core.AI;
using Mindtalk.Core.Meetings;
using Mindtalk.Writers;

/// <summary>Self-tests for the writers, run against the same code the app uses.</summary>
internal static class AiSelfTests
{
    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "--ai-help")
        {
            Console.WriteLine("""
                Writers' self-tests (the --writer is gemma (default), gemma-large, ollama:<model> or lmstudio:<model>):

                  --ai-writers                                  Every writer and its status on this PC
                  --ai-download [--writer gemma|gemma-large]    Download and check a Gemma model
                  --ai-generate "<prompt>" [--writer …]         Stream an answer
                  --ai-summarize <meeting.json> [--writer …] [--out <file>]
                                                                Summarize a meeting, checked against its transcript
                  --ai-title <meeting.json> [--writer …]        A title for a meeting
                  --ai-ask "<question>" --meetings <folder> [--writer …]
                                                                Ask the meetings in a folder (meeting.json files)
                  --ai-synth <out.json> [--minutes 60] [--sv]   Write a long made-up meeting to test with
                """);
            return 0;
        }

        if (args.Contains("--verbose"))
            LLama.Native.NativeLibraryConfig.All.WithLogCallback((level, message) => Console.Error.Write($"[{level}] {message}"));
        var settings = new AppSettings();
        var writers = new WriterService(settings, () => { }, () => new WindowsAiState(false, false, "Not in the self-test"));
        var choice = Option(args, "--writer") ?? "gemma";
        var kind = choice switch
        {
            "gemma-large" => WriterKind.GemmaLarge,
            _ when choice.StartsWith("ollama", StringComparison.Ordinal) => WriterKind.Ollama,
            _ when choice.StartsWith("lmstudio", StringComparison.Ordinal) => WriterKind.LMStudio,
            _ => WriterKind.GemmaSmall,
        };
        await writers.LocalModelsAsync(WriterKind.Ollama);
        await writers.LocalModelsAsync(WriterKind.LMStudio);
        writers.Use(kind, choice.Contains(':') ? choice[(choice.IndexOf(':') + 1)..] : null);

        switch (args[0])
        {
            case "--ai-writers":
                foreach (var s in writers.All)
                    Console.WriteLine($"{s.Kind,-11} {s.Title,-14} available={s.Available,-5} ready={s.Ready,-5} {s.Detail}{(s.Error is null ? "" : " · " + s.Error)}");
                Console.WriteLine($"Ollama models: {string.Join(", ", await writers.LocalModelsAsync(WriterKind.Ollama))}");
                Console.WriteLine($"LM Studio models: {string.Join(", ", await writers.LocalModelsAsync(WriterKind.LMStudio))}");
                return 0;

            case "--ai-download":
            {
                var last = -1;
                writers.Changed += () =>
                {
                    var p = (int)((writers.Status(kind).Progress ?? 1) * 100);
                    if (p != last) Console.Write($"\r{(last = p)} %   ");
                };
                await writers.DownloadAsync(kind);
                var status = writers.Status(kind);
                Console.WriteLine(status.Ready ? "\nReady." : $"\nNot ready: {status.Error}");
                return status.Ready ? 0 : 1;
            }

            case "--ai-generate":
            {
                var model = Ready(writers);
                if (model is null) return 1;
                var watch = Stopwatch.StartNew();
                TimeSpan? first = null;
                var chars = 0;
                await foreach (var piece in model.StreamAsync([ChatMessage.User(args.Length > 1 ? args[1] : "Say hello in Swedish.")], new GenerateOptions(MaxTokens: 300)))
                {
                    first ??= watch.Elapsed;
                    chars += piece.Length;
                    Console.Write(piece);
                }
                Console.WriteLine($"\n\n[{model.Name}{(model is GemmaModel g ? " on " + g.Backend : "")}: first text after {first?.TotalSeconds:0.0} s, {chars} characters in {watch.Elapsed.TotalSeconds:0.0} s]");
                return 0;
            }

            case "--ai-summarize" or "--ai-title":
            {
                var model = Ready(writers);
                if (model is null) return 1;
                var meeting = JsonSerializer.Deserialize<Meeting>(File.ReadAllText(args[1]), MeetingStore.Json)!;
                var summarizer = new MeetingSummarizer(() => writers.Model, () => writers.CurrentName);
                var watch = Stopwatch.StartNew();
                if (args[0] == "--ai-title")
                {
                    Console.WriteLine(await summarizer.TitleAsync(meeting));
                    Console.WriteLine($"[{watch.Elapsed.TotalSeconds:0.0} s]");
                    return 0;
                }
                var lines = SummaryGrounding.Lines(meeting);
                Console.WriteLine($"{meeting.Title}: {lines.Count} lines, {lines.Sum(l => l.Text.Length)} characters, " +
                                  $"{MeetingSummarizer.Chunk(lines, MeetingSummarizer.ChunkChars(model)).Count} part(s)");
                var last = -1;
                var summary = await summarizer.SummarizeAsync(meeting, new Progress<double>(p =>
                {
                    var percent = (int)(p * 100);
                    if (percent / 10 != last / 10) Console.Error.Write($"{percent}% ");
                    last = percent;
                }));
                Console.Error.WriteLine();
                Print(summary, meeting);
                Console.WriteLine($"\n[{summary.Writer}{(model is GemmaModel g ? " on " + g.Backend : "")}, {watch.Elapsed.TotalSeconds:0} s]");
                if (Option(args, "--out") is { } outPath)
                {
                    meeting.Summary = summary;
                    File.WriteAllText(outPath, JsonSerializer.Serialize(meeting, MeetingStore.Json));
                }
                return 0;
            }

            case "--ai-ask":
            {
                var folder = Option(args, "--meetings") ?? throw new ArgumentException("--meetings <folder>");
                var meetings = new MeetingStore(folder).All;
                var asker = new MeetingAsk(() => writers.Model);
                var watch = Stopwatch.StartNew();
                var printed = 0;
                var exchange = await asker.AskAsync(args[1], meetings, new SyncProgress<string>(text =>
                {
                    if (text.Length > printed) { Console.Write(text[printed..]); printed = text.Length; }
                }));
                if (printed == 0) Console.Write(exchange.Answer);
                Console.WriteLine("\n");
                foreach (var c in exchange.Citations)
                {
                    var m = meetings.First(x => x.Id == c.MeetingId);
                    Console.WriteLine($"  · {m.Title}, {Meeting.Clock(c.Time)} — \"{c.Quote}\"  mindtalk://meeting/{c.MeetingId:N}?t={(int)c.Time}");
                }
                Console.WriteLine($"[{watch.Elapsed.TotalSeconds:0.0} s]");
                return 0;
            }

            case "--ai-synth":
            {
                var minutes = int.TryParse(Option(args, "--minutes"), out var m) ? m : 60;
                var meeting = SyntheticMeeting.Create(minutes, args.Contains("--sv"));
                File.WriteAllText(args[1], JsonSerializer.Serialize(meeting, MeetingStore.Json));
                Console.WriteLine($"{meeting.Segments.Count} lines, {meeting.Segments.Sum(s => s.Text.Length)} characters → {args[1]}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown option {args[0]}. Try --ai-help.");
                return 2;
        }
    }

    private static ILanguageModel? Ready(WriterService writers)
    {
        var model = writers.Model;
        if (model is null) Console.Error.WriteLine($"{writers.CurrentName} isn't ready: {writers.Status(writers.Current).Detail}");
        return model;
    }

    private static void Print(MeetingSummary s, Meeting meeting)
    {
        string At(double? t) => t is { } v ? $"[{Meeting.Clock(v)}] " : "";
        Console.WriteLine($"\nOVERVIEW\n{s.Overview}");
        foreach (var t in s.Topics)
        {
            Console.WriteLine($"\n## {t.Title}");
            foreach (var i in t.Items) Console.WriteLine($"  - {At(i.Time)}{i.Text}\n      ← \"{i.Quote}\"");
        }
        Console.WriteLine("\nDECISIONS");
        foreach (var i in s.Decisions) Console.WriteLine($"  - {At(i.Time)}{i.Text}\n      ← \"{i.Quote}\"");
        Console.WriteLine("\nTO DO");
        foreach (var i in s.Todos) Console.WriteLine($"  - {At(i.Time)}{i.Text}{(i.Owner is null ? "" : $" ({i.Owner})")}\n      ← \"{i.Quote}\"");
        Console.WriteLine("\nWORTH REMEMBERING");
        foreach (var i in s.Remember) Console.WriteLine($"  - {At(i.Time)}{i.Text}\n      ← \"{i.Quote}\"");
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
