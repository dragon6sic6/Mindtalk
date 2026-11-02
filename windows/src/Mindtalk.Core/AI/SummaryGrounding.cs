using System.Text.Json.Nodes;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.AI;

/// <summary>A line of the transcript as the writer sees it: L12 [2:33] Erik: …</summary>
/// <param name="Number">1-based, over the whole meeting (background sound left out).</param>
public sealed record TranscriptLine(int Number, Segment Segment, string Speaker)
{
    public string Text => Segment.Text;
}

/// <summary>An item as the model wrote it, before it's checked.</summary>
public sealed record DraftItem(string Text, int? Line, string? Quote, string? Owner = null);
public sealed record DraftTopic(string Title, List<DraftItem> Points);

/// <summary>A summary as the model wrote it, before it's checked against the transcript.</summary>
public sealed class DraftSummary
{
    public string Overview { get; set; } = "";
    public List<DraftTopic> Topics { get; set; } = [];
    public List<DraftItem> Decisions { get; set; } = [];
    public List<DraftItem> Todos { get; set; } = [];
    public List<DraftItem> Remember { get; set; } = [];

    public int Count => Topics.Sum(t => t.Points.Count) + Decisions.Count + Todos.Count + Remember.Count;

    /// <summary>The model's JSON, read leniently (names in either language, numbers as strings…). Null if there's no object.</summary>
    public static DraftSummary? Parse(string? modelOutput)
    {
        var root = LenientJson.ParseObject(modelOutput);
        if (root is null) return null;
        var draft = new DraftSummary { Overview = LenientJson.Str(root, "overview", "summary", "översikt") ?? "" };
        foreach (var t in LenientJson.Arr(root, "topics", "what_you_talked_about", "ämnen"))
        {
            var title = LenientJson.Str(t, "title", "heading", "topic", "rubrik");
            var points = Items(LenientJson.Arr(t, "points", "items", "punkter"));
            if (title is not null && points.Count > 0) draft.Topics.Add(new DraftTopic(title, points));
        }
        draft.Decisions = Items(LenientJson.Arr(root, "decisions", "beslut"));
        draft.Todos = Items(LenientJson.Arr(root, "todos", "to_dos", "tasks", "to_do", "att_göra"));
        draft.Remember = Items(LenientJson.Arr(root, "remember", "worth_remembering", "notes", "värt_att_minnas"));
        return draft;
    }

    private static List<DraftItem> Items(JsonArray array)
    {
        var items = new List<DraftItem>();
        foreach (var node in array)
        {
            if (node is JsonValue)
            {
                if (LenientJson.Str(node) is { } bare) items.Add(new DraftItem(bare, null, null));
                continue;
            }
            var text = LenientJson.Str(node, "text", "point", "item", "task", "decision", "title");
            if (text is null) continue;
            var line = LenientJson.Num(node, "line", "l", "line_number", "rad");
            var owner = LenientJson.Str(node, "owner", "who", "ansvarig");
            if (owner is not null && IsNobody(owner)) owner = null;
            items.Add(new DraftItem(text, line is { } l ? (int)l : null, LenientJson.Str(node, "quote", "citat"), owner));
        }
        return items;
    }

    private static bool IsNobody(string owner) =>
        TextMatch.Fold(owner) is "null" or "none" or "unknown" or "ingen" or "okand" or "-" or "n/a" or "";
}

/// <summary>
/// Checks a draft against the transcript, as the Mac does: every item must point to a real line
/// (its quote is found there, give or take a word); decisions and to-dos must come from a sentence
/// where something is decided or promised (or that you marked); owners must be people in the meeting.
/// What doesn't hold up is left out.
/// </summary>
public static class SummaryGrounding
{
    /// The quote's words that must be found in the line, in order.
    public const double QuoteCoverage = 0.7;
    /// Without a quote: the share of the item's own words that must be in the line it points to.
    public const double TextOverlap = 0.5;
    /// A mark this close to a line (seconds) counts for it.
    public const double MarkReach = 30;

    /// The lines the writer sees: background sound left out, numbered from 1.
    public static List<TranscriptLine> Lines(Meeting meeting)
    {
        var background = meeting.Speakers.Where(s => s.IsBackground).Select(s => s.Id).ToHashSet();
        var lines = new List<TranscriptLine>();
        foreach (var seg in meeting.Segments.OrderBy(s => s.Start))
        {
            if (background.Contains(seg.SpeakerId) || string.IsNullOrWhiteSpace(seg.Text)) continue;
            lines.Add(new TranscriptLine(lines.Count + 1, seg, meeting.SpeakerById(seg.SpeakerId)?.DisplayName ?? seg.SpeakerId));
        }
        return lines;
    }

    /// <summary>The line an item comes from, or null if its quote isn't really there.</summary>
    public static TranscriptLine? Locate(DraftItem item, IReadOnlyList<TranscriptLine> lines)
    {
        if (lines.Count == 0) return null;
        var quote = item.Quote?.Trim().Trim('"', '“', '”', '\'');
        if (!string.IsNullOrWhiteSpace(quote) && TextMatch.Words(quote).Count > 0)
        {
            var single = TextMatch.Words(quote).Count == 1;
            TranscriptLine? best = null;
            var bestScore = 0.0;
            // The line it names and its neighbours first (models are often one off), then anywhere.
            foreach (var line in Near(item.Line, lines, 2).Concat(lines))
            {
                var score = TextMatch.Coverage(quote, line.Text);
                // A quote that runs on into the next line.
                if (score < QuoteCoverage && line.Number < lines.Count)
                    score = Math.Max(score, TextMatch.Coverage(quote, line.Text + " " + lines[line.Number].Text) - 0.01);
                if (score > bestScore + 1e-9) { best = line; bestScore = score; }
                if (bestScore >= 0.999) break;
            }
            if (best is null) return null;
            if (single ? bestScore >= 0.999 && TextMatch.Overlap(item.Text, best.Text) > 0 : bestScore >= QuoteCoverage) return best;
            return null;
        }
        // No quote: the line it names (or one beside it) must say what the item says.
        var near = Near(item.Line, lines, 1).ToList();
        var match = near.Select(l => (Line: l, Score: TextMatch.Overlap(item.Text, l.Text))).OrderByDescending(x => x.Score).FirstOrDefault();
        return match.Line is not null && match.Score >= TextOverlap ? match.Line : null;
    }

    private static IEnumerable<TranscriptLine> Near(int? number, IReadOnlyList<TranscriptLine> lines, int reach)
    {
        if (number is not { } n) yield break;
        // The named line first, then the ones around it.
        for (var d = 0; d <= reach; d++)
            foreach (var i in d == 0 ? [n] : new[] { n - d, n + d })
                if (i >= 1 && i <= lines.Count) yield return lines[i - 1];
    }

    // Cues as folded word sequences; a trailing * matches the start of a word.
    public static readonly IReadOnlyList<string> DecisionCues =
    [
        "decid*", "decision*", "agree*", "we'll", "we will", "let's", "lets", "go with", "going with", "then we",
        "we're going to", "we are going to", "settled", "approve*", "final", "we choose", "we chose", "we pick",
        "on board", "the plan is", "we build", "we do", "we'll do", "we keep", "we drop", "we stop", "we start",
        "bestam*", "beslut*", "overens", "da kor vi", "vi kor", "da gor vi", "vi gor", "da bygger vi", "vi bygger",
        "vi tar", "vi satsar", "vi valjer", "vi ska", "lat oss", "godkan*", "det blir", "da blir det", "vi lagger",
        "vi slutar", "vi borjar", "planen ar", "kor pa", "da sager vi", "vi sager",
    ];

    public static readonly IReadOnlyList<string> PromiseCues =
    [
        "i'll", "i will", "i can", "i'm going to", "i am going to", "i'll take", "we'll", "we will", "we need to",
        "need to", "needs to", "have to", "has to", "must", "should", "promise*", "make sure", "take care", "follow up",
        "send", "sends", "by monday", "by tuesday", "by wednesday", "by thursday", "by friday", "next week", "tomorrow",
        "on monday", "on tuesday", "on wednesday", "on thursday", "on friday", "can you", "could you", "please",
        "jag tar", "jag fixar", "jag skickar", "jag kollar", "jag ska", "jag kan", "jag gor", "jag ordnar", "jag lovar",
        "jag bokar", "jag ringer", "jag mejlar", "jag aterkommer", "vi ska", "vi maste", "behover", "maste", "ska",
        "tar fram", "fixar", "ordnar", "lovar", "skickar", "kollar upp", "aterkommer", "till mandag", "till fredag",
        "pa mandag", "pa tisdag", "pa onsdag", "pa torsdag", "pa fredag", "nasta vecka", "imorgon", "i morgon",
        "kan du", "kan ni", "skulle du",
    ];

    private static readonly string[] AgreementCues =
    [
        "yes", "yeah", "agreed", "absolutely", "sounds good", "deal", "sure", "exactly", "of course", "let's do it",
        "ja", "absolut", "kor", "okej", "javisst", "precis", "later bra", "overens", "sjalvklart", "deal",
    ];

    private static readonly HashSet<string> Negations =
        ["not", "don't", "dont", "didn't", "doesn't", "haven't", "hasn't", "no", "never", "won't", "isn't", "aren't", "wasn't",
         "inte", "ej", "aldrig", "ingen", "inget", "inga", "behover"];

    /// <summary>Whether the text has one of the cues (word sequences; a trailing * matches a word's start).</summary>
    /// <param name="unlessNegated">Not when "not", "don't", "inte"… comes just before ("we don't need to decide").</param>
    public static bool HasCue(string text, IEnumerable<string> cues, bool unlessNegated = false)
    {
        var words = TextMatch.Words(text);
        foreach (var cue in cues)
        {
            var parts = cue.Split(' ');
            for (var i = 0; i + parts.Length <= words.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < parts.Length && ok; j++)
                {
                    var p = parts[j];
                    ok = p.EndsWith('*') ? words[i + j].StartsWith(p[..^1], StringComparison.Ordinal) : words[i + j] == p;
                }
                if (ok && !(unlessNegated && Enumerable.Range(Math.Max(0, i - 3), Math.Min(3, i)).Any(k => Negations.Contains(words[k])))) return true;
            }
        }
        return false;
    }

    public static bool IsDecision(TranscriptLine line, IReadOnlyList<TranscriptLine> lines, IEnumerable<MeetingMark> marks) =>
        HasCue(line.Text, DecisionCues, unlessNegated: true) || MarkedNear(line, marks, MarkKind.Decision) || AgreedAfter(line, lines);

    public static bool IsPromise(TranscriptLine line, IEnumerable<MeetingMark> marks) =>
        HasCue(line.Text, PromiseCues) || MarkedNear(line, marks, MarkKind.Todo);

    private static bool MarkedNear(TranscriptLine line, IEnumerable<MeetingMark> marks, MarkKind kind) =>
        marks.Any(m => m.Kind == kind && m.Time >= line.Segment.Start - MarkReach && m.Time <= line.Segment.End + MarkReach);

    // A proposal someone else says yes to right after ("Is everyone on board?" — "Yes, absolutely.").
    private static bool AgreedAfter(TranscriptLine line, IReadOnlyList<TranscriptLine> lines)
    {
        if (line.Number >= lines.Count) return false;
        var next = lines[line.Number];
        return next.Segment.SpeakerId != line.Segment.SpeakerId
               && next.Segment.Start - line.Segment.End <= MarkReach
               && TextMatch.Words(next.Text).Count <= 8
               && HasCue(next.Text, AgreementCues);
    }

    private static readonly string[] FirstPersonPromise =
    [
        "i'll", "i will", "i'm going to", "i am going to", "let me", "i can take", "i'll take", "i promise",
        "jag tar", "jag fixar", "jag skickar", "jag ska", "jag kan ta", "jag gor", "jag ordnar", "jag kollar", "jag bokar",
        "jag ringer", "jag mejlar", "jag aterkommer", "jag lovar", "jag tittar",
    ];

    /// <summary>
    /// The owner as a person in the meeting (a named speaker), or null. "I'll send it" makes
    /// whoever said it the owner, whatever the model guessed.
    /// </summary>
    public static string? Owner(string? owner, TranscriptLine line, Meeting meeting)
    {
        var people = meeting.Talkers.Where(s => s.Name is not null).ToList();
        var speaker = people.FirstOrDefault(p => p.Id == line.Segment.SpeakerId);
        if (speaker is not null && HasCue(line.Text, FirstPersonPromise)) return speaker.Name;
        if (owner is null) return null;
        var o = TextMatch.Words(owner);
        if (o.Count == 0) return null;
        foreach (var p in people)
        {
            var name = TextMatch.Words(p.Name!);
            // "Erik" for Erik Svensson, "Erik Svensson" for Erik.
            if (name.Count > 0 && (string.Join(" ", name) == string.Join(" ", o) || name[0] == o[0])) return p.Name;
        }
        // "Me", "I", "jag": you, if you're in the meeting.
        if (o.Count == 1 && o[0] is "me" or "i" or "jag" or "mig" && people.FirstOrDefault(p => p.IsMe) is { } me) return me.Name;
        return null;
    }

    /// <summary>The checked summary: only what holds up against the transcript.</summary>
    public static MeetingSummary Ground(DraftSummary draft, Meeting meeting, IReadOnlyList<TranscriptLine> lines, Func<string, string>? fix = null)
    {
        fix ??= s => s;
        string Clean(string s) => fix(s.Trim().TrimStart('-', '•', '*', ' '));

        SummaryItem? Item(DraftItem d)
        {
            if (string.IsNullOrWhiteSpace(d.Text)) return null;
            var line = Locate(d, lines);
            return line is null || !SaysSomethingOf(d.Text, line, lines) ? null : new SummaryItem(Clean(d.Text), line.Segment.Start, line.Segment.SpeakerId, line.Text);
        }

        var summary = new MeetingSummary { Overview = Clean(draft.Overview) };
        foreach (var topic in draft.Topics)
        {
            var points = Dedupe(topic.Points.Select(Item).OfType<SummaryItem>(), i => i.Text).Take(6).OrderBy(i => i.Time).ToList();
            if (points.Count > 0 && !string.IsNullOrWhiteSpace(topic.Title))
                summary.Topics.Add(new SummaryTopic(Clean(topic.Title).TrimEnd(':', '.'), points));
        }
        summary.Topics = summary.Topics.Take(8).ToList();

        foreach (var d in draft.Decisions)
        {
            if (string.IsNullOrWhiteSpace(d.Text) || Locate(d, lines) is not { } line || !SaysSomethingOf(d.Text, line, lines)) continue;
            if (!IsDecision(line, lines, meeting.Marks)) continue;
            summary.Decisions.Add(new SummaryItem(Clean(d.Text), line.Segment.Start, line.Segment.SpeakerId, line.Text));
        }
        summary.Decisions = Dedupe(summary.Decisions, i => i.Text).Take(10).OrderBy(i => i.Time).ToList();

        foreach (var d in draft.Todos)
        {
            if (string.IsNullOrWhiteSpace(d.Text) || Locate(d, lines) is not { } line || !SaysSomethingOf(d.Text, line, lines)) continue;
            if (!IsPromise(line, meeting.Marks)) continue;
            summary.Todos.Add(new TaskItem(Clean(d.Text), Owner(d.Owner, line, meeting), line.Segment.Start, false, line.Text));
        }
        summary.Todos = Dedupe(summary.Todos, t => t.Text).Take(12).OrderBy(t => t.Time).ToList();

        summary.Remember = Dedupe(draft.Remember.Select(Item).OfType<SummaryItem>(), i => i.Text).Take(8).OrderBy(i => i.Time).ToList();
        return summary;
    }

    /// <summary>
    /// The item says something of the line it points to (a word in common, give or take an ending,
    /// with the line and the one after it): a real quote under an unrelated sentence doesn't count.
    /// </summary>
    public static bool SaysSomethingOf(string text, TranscriptLine line, IReadOnlyList<TranscriptLine> lines)
    {
        var said = line.Number < lines.Count ? line.Text + " " + lines[line.Number].Text : line.Text;
        return TextMatch.Overlap(text, said) > 0;
    }

    private static IEnumerable<T> Dedupe<T>(IEnumerable<T> items, Func<T, string> text)
    {
        var kept = new List<T>();
        foreach (var item in items)
            if (!kept.Any(k => TextMatch.Overlap(text(k), text(item)) >= 0.85 && Math.Abs(TextMatch.ContentWords(text(k)).Count - TextMatch.ContentWords(text(item)).Count) <= 2))
                kept.Add(item);
        return kept;
    }
}
