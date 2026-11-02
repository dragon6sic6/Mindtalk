using System.Globalization;
using System.Text;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.AI;

/// <summary>A model that says how much text it can take in at once (tokens), so long meetings are split to fit.</summary>
public interface IContextWindow
{
    int ContextTokens { get; }
}

/// <summary>
/// Writes a meeting's summary and title with the chosen writer. The model gets the transcript
/// with numbered lines, your notes and marks, and answers in JSON; every item it writes is then
/// checked against the transcript (<see cref="SummaryGrounding"/>). A long meeting is summarized
/// in parts that fit the model, and the parts are then put together.
/// </summary>
public sealed class MeetingSummarizer(Func<ILanguageModel?> model, Func<string> writerName, Func<string, string>? fix = null) : IMeetingSummarizer
{
    /// Characters per token, on the safe side for transcript lines (numbers, names, short words).
    private const double CharsPerToken = 2.6;
    private const int AnswerTokens = 2500;
    private const int PromptTokens = 900;

    public string WriterName => writerName();
    public bool IsReady => model() is not null;

    /// How many transcript characters fit in one go.
    public static int ChunkChars(ILanguageModel m)
    {
        var context = Math.Max(4096, (m as IContextWindow)?.ContextTokens ?? 8192);
        return Math.Max(3000, (int)((context - AnswerTokens - PromptTokens) * CharsPerToken));
    }

    public async Task<MeetingSummary> SummarizeAsync(Meeting meeting, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var m = model() ?? throw new InvalidOperationException(Loc.T("Ingen skrivare är redo. Välj en under Inställningar → Möten.",
                                                                     "No writer is ready. Choose one under Settings → Meetings."));
        var lines = SummaryGrounding.Lines(meeting);
        var summary = new MeetingSummary { Writer = m.Name, Written = DateTime.Now };
        if (lines.Count == 0) { progress?.Report(1); return summary; }

        var language = MeetingLanguage(lines);
        var chunks = Chunk(lines, ChunkChars(m));
        var drafts = new List<DraftSummary>();
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // The parts take 0…0.8 when they have to be merged afterwards, else nearly all of it.
            var span = chunks.Count > 1 ? 0.8 : 0.98;
            var start = span * i / chunks.Count;
            var share = span / chunks.Count;
            var prompt = SummaryPrompt(meeting, chunks[i], language, part: chunks.Count > 1 ? (i + 1, chunks.Count) : null);
            var draft = await AskJsonAsync(m, prompt, p => progress?.Report(start + share * p), ct);
            if (draft is not null) drafts.Add(draft);
        }

        DraftSummary? final;
        if (drafts.Count == 0) final = null;
        else if (drafts.Count == 1) final = drafts[0];
        else
        {
            progress?.Report(0.8);
            // Decisions, to-dos and what's worth remembering are kept from every part; the model
            // only writes the overview and puts the topics together.
            var merged = await AskJsonAsync(m, MergePrompt(meeting, drafts, language, ChunkChars(m)), p => progress?.Report(0.8 + 0.18 * p), ct);
            final = Concat(drafts, merged?.Overview);
            if (merged is { Topics.Count: > 0 } && merged.Topics.Sum(t => t.Points.Count) >= final.Topics.Sum(t => t.Points.Count) / 3)
                final.Topics = merged.Topics;
        }
        if (final is null) throw new InvalidOperationException(Loc.T("Sammanfattningen kunde inte skrivas.", "The summary couldn't be written."));

        var grounded = SummaryGrounding.Ground(final, meeting, lines, fix);
        grounded.Writer = m.Name;
        grounded.Written = DateTime.Now;
        progress?.Report(1);
        return grounded;
    }

    public async Task<string> TitleAsync(Meeting meeting, CancellationToken ct = default)
    {
        var m = model() ?? throw new InvalidOperationException(Loc.T("Ingen skrivare är redo.", "No writer is ready."));
        var lines = SummaryGrounding.Lines(meeting);
        var language = MeetingLanguage(lines);
        var sb = new StringBuilder();
        if (meeting.Summary is { } s && s.Overview.Length > 0)
        {
            sb.Append("Summary: ").Append(s.Overview).Append('\n');
            if (s.Topics.Count > 0) sb.Append("Topics: ").Append(string.Join("; ", s.Topics.Select(t => t.Title))).Append('\n');
        }
        sb.Append("\nTranscript (beginning):\n");
        foreach (var line in lines)
        {
            if (sb.Length > 6000) break;
            sb.Append(line.Speaker).Append(": ").Append(line.Text).Append('\n');
        }
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You name meetings. Answer with the title only."),
            ChatMessage.User($"""
                Write a short, specific title for this meeting in {LanguageName(language)}: 3 to 6 words that say what it was about,
                like "Weekly meeting about the launch", "Customer meeting, kick-off" or "Veckomöte om lanseringen".
                Only the title: no quotes, no full stop, no date.

                {sb}
                """),
        };
        var title = await m.CompleteAsync(messages, new GenerateOptions(MaxTokens: 40, Temperature: 0.3), ct);
        return CleanTitle(title, fix);
    }

    /// The title as the model wrote it, without "Title:", quotes, a full stop or a second line.
    public static string CleanTitle(string raw, Func<string, string>? fix = null)
    {
        var title = raw.Replace("**", "").Trim();
        var firstLine = title.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        foreach (var prefix in new[] { "title:", "titel:", "rubrik:" })
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) firstLine = firstLine[prefix.Length..].Trim();
        firstLine = firstLine.Trim('"', '\'', '“', '”', '«', '»', '#', ' ', '*').TrimEnd('.', '!');
        if (firstLine.Length > 70) firstLine = firstLine[..70].TrimEnd() + "…";
        if (firstLine.Length > 0) firstLine = char.ToUpper(firstLine[0], CultureInfo.CurrentCulture) + firstLine[1..];
        return fix?.Invoke(firstLine) ?? firstLine;
    }

    // ---- Prompts -----------------------------------------------------------------------

    /// "sv" or "en", by what's said in the meeting.
    public static string MeetingLanguage(IReadOnlyList<TranscriptLine> lines) =>
        TextMatch.Language(string.Join(" ", lines.Take(200).Select(l => l.Text)));

    public static string LanguageName(string code) => code == "sv" ? "Swedish" : "English";

    /// The lines, split into parts that each fit the model.
    public static List<List<TranscriptLine>> Chunk(IReadOnlyList<TranscriptLine> lines, int maxChars)
    {
        var chunks = new List<List<TranscriptLine>> { new() };
        var size = 0;
        foreach (var line in lines)
        {
            var length = line.Text.Length + line.Speaker.Length + 16;
            if (size + length > maxChars && chunks[^1].Count > 0) { chunks.Add([]); size = 0; }
            chunks[^1].Add(line);
            size += length;
        }
        return chunks;
    }

    private const string Schema = """
        {
          "overview": "2–3 sentences: what the meeting was about and what came out of it",
          "topics": [
            { "title": "a short heading", "points": [ { "text": "one point, a short sentence", "line": 12, "quote": "words copied exactly from line 12" } ] }
          ],
          "decisions": [ { "text": "what was decided", "line": 0, "quote": "…" } ],
          "todos": [ { "text": "what is to be done", "owner": "the person who will do it, or null", "line": 0, "quote": "…" } ],
          "remember": [ { "text": "a fact, number or date worth remembering", "line": 0, "quote": "…" } ]
        }
        """;

    public static IReadOnlyList<ChatMessage> SummaryPrompt(Meeting meeting, IReadOnlyList<TranscriptLine> lines, string language, (int Index, int Count)? part = null)
    {
        var people = meeting.Talkers.Where(s => s.Name is not null).Select(s => s.Name!).ToList();
        var sb = new StringBuilder();
        sb.Append(part is { } p
            ? $"This is part {p.Index} of {p.Count} of the transcript of a meeting"
            : "This is the transcript of a meeting");
        sb.Append(meeting.Title.Length > 0 ? $" (\"{meeting.Title}\")" : "").Append(". Each line is: L<number> [time] Speaker: what was said.\n");

        var first = lines.Count > 0 ? lines[0].Segment.Start : 0;
        var last = lines.Count > 0 ? lines[^1].Segment.End : double.MaxValue;
        var notes = meeting.Notes.Where(n => n.Time >= first - 60 && n.Time <= last + 60).ToList();
        if (notes.Count > 0)
        {
            sb.Append("\nNotes the user typed during the meeting (they show what mattered to them):\n");
            foreach (var n in notes) sb.Append($"[{Meeting.Clock(n.Time)}] {n.Text}\n");
        }

        var marks = MarksByLine(meeting, lines);
        sb.Append("\nTRANSCRIPT\n");
        foreach (var line in lines)
        {
            sb.Append('L').Append(line.Number).Append(" [").Append(Meeting.Clock(line.Segment.Start)).Append("] ")
              .Append(line.Speaker).Append(": ").Append(line.Text);
            if (marks.TryGetValue(line.Number, out var kinds))
                sb.Append("   <- marked by the user: ").Append(string.Join(", ", kinds.Select(MarkName)));
            sb.Append('\n');
        }

        sb.Append($"""

            Write notes from this transcript as one JSON object:
            {Schema}

            Rules:
            - Write everything in {LanguageName(language)}.
            - Use only what is said in the transcript. Never add anything that isn't there.
            - Every item gives the line it comes from ("line") and copies a few words from that line exactly ("quote").
            - Topic points carry the substance: facts, numbers, dates, proposals, problems. Skip small talk, filler and remarks about the meeting itself.
            - Decisions: only where the transcript shows something being decided or agreed.
            - To-dos: only concrete work someone takes on for after the meeting ("I'll book the movers", "can you send the plan by Friday?"), not something done there and then ("let me check my notes"). "owner" is {(people.Count > 0 ? "one of: " + string.Join(", ", people) : "null (nobody is named)")}, or null if it isn't clear.
            - A line marked Decision by the user is probably a decision, To do a to-do, Important worth remembering.
            - 2 to 5 topics with 1 to 5 points each. Keep each point short. Don't repeat yourself.
            - Leave a list empty rather than guess.
            """);
        if (part is not null) sb.Append("\n- This is only part of the meeting: the overview covers this part.");

        return
        [
            ChatMessage.System("You write clear, short meeting notes from a transcript. You answer with JSON only."),
            ChatMessage.User(sb.ToString()),
        ];
    }

    private static string MarkName(MarkKind kind) => kind switch
    {
        MarkKind.Decision => "Decision",
        MarkKind.Todo => "To do",
        _ => "Important",
    };

    /// Each mark on the line it was set during (or the last one before it).
    private static Dictionary<int, List<MarkKind>> MarksByLine(Meeting meeting, IReadOnlyList<TranscriptLine> lines)
    {
        var result = new Dictionary<int, List<MarkKind>>();
        foreach (var mark in meeting.Marks)
        {
            var line = lines.LastOrDefault(l => l.Segment.Start <= mark.Time + 1);
            if (line is null || mark.Time - line.Segment.End > SummaryGrounding.MarkReach) continue;
            if (!result.TryGetValue(line.Number, out var list)) result[line.Number] = list = [];
            if (!list.Contains(mark.Kind)) list.Add(mark.Kind);
        }
        return result;
    }

    private const string MergeSchema = """
        {
          "overview": "2–3 sentences about the whole meeting: what it was about and what came out of it",
          "topics": [
            { "title": "a short heading", "points": [ { "text": "one point, a short sentence", "line": 12, "quote": "the quote, as given" } ] }
          ]
        }
        """;

    public static IReadOnlyList<ChatMessage> MergePrompt(Meeting meeting, IReadOnlyList<DraftSummary> drafts, string language, int maxChars)
    {
        var sb = new StringBuilder();
        // Fewer points per topic if it would be too long.
        for (var perList = 8; perList >= 2; perList--)
        {
            sb.Clear();
            for (var i = 0; i < drafts.Count; i++)
            {
                var d = drafts[i];
                sb.Append($"\nPART {i + 1}\nOverview: {d.Overview}\n");
                foreach (var x in d.Decisions.Take(perList)) sb.Append("Decided: ").Append(x.Text).Append('\n');
                foreach (var t in d.Topics.Take(perList))
                {
                    sb.Append("Topic: ").Append(t.Title).Append('\n');
                    foreach (var pt in t.Points.Take(perList)) Item(sb, "  point", pt);
                }
            }
            if (sb.Length <= maxChars) break;
        }
        return
        [
            ChatMessage.System("You write clear, short meeting notes. You answer with JSON only."),
            ChatMessage.User($"""
                A long meeting{(meeting.Title.Length > 0 ? $" (\"{meeting.Title}\")" : "")} was summarized in {drafts.Count} parts, below. Write the overview of the whole meeting and put the topics together, as one JSON object:
                {MergeSchema}

                Rules:
                - Write everything in {LanguageName(language)}.
                - Merge topics that are about the same thing: 3 to 6 topics in all, each with its most important points (at most 5).
                - Keep each point's "line" and "quote" exactly as they are below. Don't invent points.
                {sb}
                """),
        ];
    }

    private static void Item(StringBuilder sb, string kind, DraftItem item)
    {
        sb.Append(kind).Append(": ").Append(item.Text);
        if (item.Owner is not null) sb.Append(" (owner: ").Append(item.Owner).Append(')');
        if (item.Line is { } l) sb.Append(" [line ").Append(l).Append(']');
        if (item.Quote is not null) sb.Append(" [quote: \"").Append(item.Quote).Append("\"]");
        sb.Append('\n');
    }

    /// The parts as they are, one after another, when they can't be merged.
    public static DraftSummary Concat(IReadOnlyList<DraftSummary> drafts, string? overview)
    {
        var all = new DraftSummary { Overview = overview ?? string.Join(" ", drafts.Select(d => d.Overview).Where(o => o.Length > 0).Take(2)) };
        foreach (var d in drafts)
        {
            foreach (var t in d.Topics)
            {
                var same = all.Topics.FirstOrDefault(x => TextMatch.Overlap(x.Title, t.Title) >= 0.8);
                if (same is not null) same.Points.AddRange(t.Points);
                else all.Topics.Add(new DraftTopic(t.Title, [.. t.Points]));
            }
            all.Decisions.AddRange(d.Decisions);
            all.Todos.AddRange(d.Todos);
            all.Remember.AddRange(d.Remember);
        }
        return all;
    }

    /// <summary>Asks for JSON; if the answer can't be read, asks once more, saying so.</summary>
    public static async Task<DraftSummary?> AskJsonAsync(ILanguageModel m, IReadOnlyList<ChatMessage> prompt, Action<double>? progress, CancellationToken ct)
    {
        var options = new GenerateOptions(MaxTokens: AnswerTokens, Temperature: 0.2, Json: true);
        var answer = await Stream(m, prompt, options, progress, ct);
        var draft = DraftSummary.Parse(answer);
        if (draft is not null) return draft;

        var retry = prompt.ToList();
        retry.Add(ChatMessage.Assistant(answer.Length > 4000 ? answer[..4000] : answer));
        retry.Add(ChatMessage.User("That wasn't a valid JSON object. Write the notes again as one JSON object only, exactly in the format asked for, with nothing before or after it."));
        answer = await Stream(m, retry, options, null, ct);
        return DraftSummary.Parse(answer);
    }

    private static async Task<string> Stream(ILanguageModel m, IReadOnlyList<ChatMessage> messages, GenerateOptions options, Action<double>? progress, CancellationToken ct)
    {
        var sb = new StringBuilder();
        // The answer's length is unknown; progress creeps towards the end as text comes.
        var expected = options.MaxTokens * CharsPerToken * 0.45;
        await foreach (var piece in m.StreamAsync(messages, options, ct))
        {
            sb.Append(piece);
            progress?.Invoke(Math.Min(0.95, sb.Length / expected));
        }
        progress?.Invoke(1);
        return sb.ToString();
    }
}
