using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.AI;

/// <summary>What a passage is: something said, or part of the summary or your notes.</summary>
public enum PassageKind { Said, Overview, Point, Decision, Todo, Remember, Note }

/// <summary>A piece of a meeting that can answer a question: a line of the transcript, a summary item, a note.</summary>
/// <param name="Quote">The words to show (and to cite): the transcript line, or the summary item's line.</param>
/// <param name="Context">The line that follows, when it belongs with it ("Is everyone on board?" — "Yes, absolutely.").</param>
public sealed record Passage(Meeting Meeting, PassageKind Kind, double Time, string? Speaker, string Text, string Quote, string? Context = null);

public sealed record PassageHit(Passage Passage, double Score);

/// <summary>
/// Finds the passages in some meetings that bear on a question: BM25 over transcript lines,
/// summaries and notes, without regard to case or accents, in Swedish and English. Word endings
/// are trimmed ("beslutade" finds "beslut"), compounds are looked into ("budget" finds
/// "höstbudgeten"), and "what was decided" finds decisions even without the word.
/// "Today", "this week", "i veckan"… narrow it to those meetings.
/// </summary>
public static class Retrieval
{
    private const double K1 = 1.2, B = 0.75;
    private const string DecisionTag = "#decision", TodoTag = "#todo", OverviewTag = "#overview";

    /// <summary>The best passages for the question, best first; empty when nothing bears on it.</summary>
    public static List<PassageHit> Search(string question, IReadOnlyList<Meeting> scope, DateTime now, int limit = 8)
    {
        var (meetings, timeWords) = Narrow(question, scope, now);
        if (meetings.Count == 0) return [];
        var terms = QueryTerms(question, timeWords, single: meetings.Count == 1);
        if (terms.Count == 0) return [];

        var passages = meetings.SelectMany(Passages).ToList();
        if (passages.Count == 0) return [];
        var docs = passages.Select(p => Terms(p)).ToList();
        var avg = docs.Average(d => d.Count);

        // Document frequency of each query term (with compounds and endings counted as matches).
        var weights = new Dictionary<string, double>();
        foreach (var term in terms.Distinct())
        {
            var df = docs.Count(d => d.Any(t => Match(term, t) > 0));
            weights[term] = df == 0 ? 0 : Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
        }

        // With a real subject in the question ("the budget"), only passages about it count;
        // with only vague words ("what happened?"), the summary answers.
        var vague = terms.All(t => t == OverviewTag);
        var hits = new List<PassageHit>();
        for (var i = 0; i < passages.Count; i++)
        {
            double score = 0;
            var meaningful = false;
            foreach (var term in terms.Distinct())
            {
                if (weights[term] == 0) continue;
                var tf = docs[i].Sum(t => Match(term, t));
                if (tf == 0) continue;
                if (term != OverviewTag) meaningful = true;
                score += weights[term] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * docs[i].Count / avg));
            }
            if (!meaningful && !(vague && passages[i].Kind is PassageKind.Overview or PassageKind.Point)) continue;
            if (score <= 0) continue;
            // The summary and your notes already say what mattered.
            score *= passages[i].Kind switch
            {
                PassageKind.Decision or PassageKind.Todo => 1.25,
                PassageKind.Note or PassageKind.Remember or PassageKind.Point => 1.1,
                _ => 1,
            };
            hits.Add(new PassageHit(passages[i], score));
        }

        // The best, without saying the same line twice, and not all from one meeting when there are several.
        var perMeeting = meetings.Count == 1 ? limit : Math.Max(3, limit / 2);
        var picked = new List<PassageHit>();
        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            if (picked.Count >= limit) break;
            if (picked.Count(p => p.Passage.Meeting.Id == hit.Passage.Meeting.Id) >= perMeeting) continue;
            if (picked.Any(p => p.Passage.Meeting.Id == hit.Passage.Meeting.Id && Math.Abs(p.Passage.Time - hit.Passage.Time) < 0.5
                                && TextMatch.Overlap(p.Passage.Quote, hit.Passage.Quote) > 0.8)) continue;
            picked.Add(hit);
        }
        return picked;
    }

    /// <summary>Everything in a meeting that can be found: what was said (not background sound), the summary, your notes.</summary>
    public static IEnumerable<Passage> Passages(Meeting meeting)
    {
        var lines = SummaryGrounding.Lines(meeting);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string? context = null;
            if (i + 1 < lines.Count && lines[i + 1].Segment.Start - line.Segment.End < 15 && lines[i + 1].Text.Length < 80)
                context = $"{lines[i + 1].Speaker}: {lines[i + 1].Text}";
            yield return new Passage(meeting, PassageKind.Said, line.Segment.Start, line.Speaker, line.Text, line.Text, context);
        }
        // A summary item carries the line it came from, which often says more ("… on Thursday").
        string? Said(double? time) =>
            time is { } t && lines.FirstOrDefault(l => Math.Abs(l.Segment.Start - t) < 0.5) is { } l ? $"{l.Speaker}: {l.Text}" : null;
        if (meeting.Summary is { } s)
        {
            var start = lines.Count > 0 ? lines[0].Segment.Start : 0;
            if (s.Overview.Length > 0) yield return new Passage(meeting, PassageKind.Overview, start, null, s.Overview, s.Overview);
            foreach (var t in s.Topics)
                foreach (var item in t.Items)
                    yield return new Passage(meeting, PassageKind.Point, item.Time ?? start, Name(meeting, item.SpeakerId), $"{t.Title}: {item.Text}", item.Quote ?? item.Text, Said(item.Time));
            foreach (var d in s.Decisions)
                yield return new Passage(meeting, PassageKind.Decision, d.Time ?? start, Name(meeting, d.SpeakerId), d.Text, d.Quote ?? d.Text, Said(d.Time));
            foreach (var t in s.Todos)
                yield return new Passage(meeting, PassageKind.Todo, t.Time ?? start, t.Owner, t.Owner is null ? t.Text : $"{t.Text} ({t.Owner})", t.Quote ?? t.Text, Said(t.Time));
            foreach (var r in s.Remember)
                yield return new Passage(meeting, PassageKind.Remember, r.Time ?? start, Name(meeting, r.SpeakerId), r.Text, r.Quote ?? r.Text, Said(r.Time));
        }
        foreach (var n in meeting.Notes)
            yield return new Passage(meeting, PassageKind.Note, n.Time, null, n.Text, n.Text);
    }

    private static string? Name(Meeting meeting, string? speakerId) =>
        speakerId is null ? null : meeting.SpeakerById(speakerId)?.DisplayName;

    // ---- Terms -------------------------------------------------------------------------

    private static readonly string[] DecisionWords = ["decid", "decision", "beslut", "bestam", "agree", "overens", "conclu", "settl"];
    private static readonly string[] TodoWords = ["todo", "task", "uppgift", "action", "promis", "lova", "ansvar", "assign", "deadline", "followup", "gora"];
    private static readonly string[] VagueWords =
        ["happen", "discuss", "talk", "summar", "meeting", "mote", "hande", "prata", "sammanfatt", "diskut", "overview", "about", "go", "went", "gick", "sag", "say", "said", "ask", "fraga"];

    /// The question's terms: content words, trimmed, plus the tags for decisions, to-dos and "what happened".
    public static List<string> QueryTerms(string question, ISet<string>? skip = null, bool single = false)
    {
        var terms = new List<string>();
        foreach (var word in TextMatch.ContentWords(question))
        {
            if (skip?.Contains(word) == true) continue;
            var stem = Stem(word);
            if (DecisionWords.Any(w => stem.StartsWith(w, StringComparison.Ordinal))) { terms.Add(DecisionTag); continue; }
            if (TodoWords.Any(w => stem.StartsWith(w, StringComparison.Ordinal)) || word is "to-do" or "todos") { terms.Add(TodoTag); continue; }
            if (VagueWords.Any(w => stem.StartsWith(w, StringComparison.Ordinal) || word.StartsWith(w, StringComparison.Ordinal))) { terms.Add(OverviewTag); continue; }
            terms.Add(stem);
        }
        if (TextMatch.Fold(question).Contains("att gora", StringComparison.Ordinal) || TextMatch.Fold(question).Contains("to do", StringComparison.Ordinal))
            terms.Add(TodoTag);
        // "What did we talk about?" about a single meeting: its overview.
        if (terms.Count == 0 && single) terms.Add(OverviewTag);
        return terms;
    }

    private static List<string> Terms(Passage p)
    {
        var terms = TextMatch.ContentWords(p.Text + " " + p.Context).Select(Stem).ToList();
        if (p.Speaker is not null) terms.AddRange(TextMatch.ContentWords(p.Speaker));
        if (p.Kind == PassageKind.Decision || (p.Kind == PassageKind.Said && SummaryGrounding.HasCue(TextMatch.Statements(p.Text), SummaryGrounding.DecisionCues, unlessNegated: true)))
            terms.Add(DecisionTag);
        if (p.Kind == PassageKind.Todo) terms.Add(TodoTag);
        if (p.Kind == PassageKind.Overview || p.Kind == PassageKind.Point) terms.Add(OverviewTag);
        return terms;
    }

    /// How well a query term matches a passage's term: 1 the same, 0.7 inside a compound.
    public static double Match(string query, string term)
    {
        if (query == term) return 1;
        if (query.StartsWith('#') || term.StartsWith('#')) return 0;
        if (query.Length >= 4 && term.Length > query.Length && term.Contains(query, StringComparison.Ordinal)) return 0.7;
        if (term.Length >= 4 && query.Length > term.Length && query.StartsWith(term, StringComparison.Ordinal)) return 0.7;
        return 0;
    }

    private static readonly string[] Suffixes =
    [
        // Swedish (folded)
        "arnas", "ernas", "ornas", "arna", "erna", "orna", "andet", "ande", "ende", "heten", "het", "ade", "are", "ens", "ets",
        "en", "et", "er", "ar", "or", "na", "de", "te",
        // English
        "ings", "ing", "ies", "ied", "ed", "es", "ly", "s",
    ];

    /// The word without its ending: "nedladdningar" → "nedladdning", "decided" → "decid".
    public static string Stem(string word)
    {
        foreach (var suffix in Suffixes)
            if (word.Length - suffix.Length >= 4 && word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        return word;
    }

    // ---- Time --------------------------------------------------------------------------

    /// <summary>The meetings the question's time words allow ("this week" …), and those words, to leave out of the search.</summary>
    public static (List<Meeting> Meetings, HashSet<string> Words) Narrow(string question, IReadOnlyList<Meeting> scope, DateTime now)
    {
        var q = " " + string.Join(" ", TextMatch.Words(question)) + " ";
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
        (DateTime From, DateTime To, string[] Words)? range = null;
        bool Has(params string[] phrases) => phrases.Any(p => q.Contains(" " + p + " ", StringComparison.Ordinal));

        if (Has("today", "idag", "i dag")) range = (today, today.AddDays(1), ["today", "idag", "dag"]);
        else if (Has("yesterday", "igar", "i gar")) range = (today.AddDays(-1), today, ["yesterday", "igar", "gar"]);
        else if (Has("last week", "forra veckan")) range = (weekStart.AddDays(-7), weekStart, ["last", "week", "forra", "veckan"]);
        else if (Has("this week", "i veckan", "denna vecka", "den har veckan", "veckan")) range = (weekStart, weekStart.AddDays(7), ["this", "week", "veckan", "vecka", "denna"]);
        else if (Has("last month", "forra manaden")) range = (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1), ["last", "month", "forra", "manaden"]);
        else if (Has("this month", "i manaden", "den har manaden", "denna manad")) range = (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1), ["this", "month", "manaden", "manad", "denna"]);

        if (range is not { } r) return (scope.ToList(), []);
        return (scope.Where(m => m.Started >= r.From && m.Started < r.To).ToList(), r.Words.ToHashSet());
    }
}
