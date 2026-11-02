using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.AI;

/// <summary>
/// Answers a question about one meeting, a folder or all of them. The passages that bear on it
/// are found first (<see cref="Retrieval"/>); the writer then answers only from those, numbered,
/// citing them — and each citation becomes the meeting and moment it came from. When nothing in
/// the meetings bears on the question, it says so plainly, without asking the model at all.
/// </summary>
public sealed partial class MeetingAsk(Func<ILanguageModel?> model, Func<DateTime>? clock = null) : IMeetingAsk
{
    /// What the model says when the excerpts don't answer the question.
    public const string NotFound = "NOT_IN_MEETINGS";

    public static string Nothing(string language, bool oneMeeting) => language == "sv"
        ? oneMeeting ? "Inget i det här mötet nämner det." : "Inget i dina möten nämner det."
        : oneMeeting ? "Nothing in this meeting mentions that." : "Nothing in your meetings mentions that.";

    public async Task<AskExchange> AskAsync(string question, IReadOnlyList<Meeting> scope, IProgress<string>? partial = null, CancellationToken ct = default)
    {
        question = question.Trim();
        var language = TextMatch.Language(question);
        var asked = DateTime.Now;
        var hits = Retrieval.Search(question, scope, (clock ?? (() => DateTime.Now))());
        if (hits.Count == 0)
        {
            var nothing = Nothing(language, scope.Count == 1);
            partial?.Report(nothing);
            return new AskExchange(question, nothing, [], asked);
        }

        var m = model();
        if (m is null) return Extractive(question, hits, language, partial, asked);

        var messages = Prompt(question, hits, language, scope.Count == 1);
        var raw = new StringBuilder();
        await foreach (var piece in m.StreamAsync(messages, new GenerateOptions(MaxTokens: 700, Temperature: 0.2), ct))
        {
            raw.Append(piece);
            var text = raw.ToString();
            if (NotFound.StartsWith(text.TrimStart(), StringComparison.Ordinal) || text.Contains(NotFound, StringComparison.Ordinal)) continue;
            partial?.Report(StripMarkers(text, streaming: true));
        }

        var answer = raw.ToString().Trim();
        if (answer.Contains(NotFound, StringComparison.Ordinal) && StripMarkers(answer.Replace(NotFound, ""), false).Trim().Length < 20)
        {
            var nothing = Nothing(language, scope.Count == 1);
            partial?.Report(nothing);
            return new AskExchange(question, nothing, [], asked);
        }
        var citations = Citations(answer, hits);
        // Answered without saying where from: the passage that matched best is where.
        if (citations.Count == 0) citations.Add(Cite(hits[0].Passage));
        var clean = StripMarkers(answer.Replace(NotFound, ""), streaming: false);
        partial?.Report(clean);
        return new AskExchange(question, clean, citations, asked);
    }

    /// <summary>The prompt: the question and the numbered excerpts, each with its meeting, date, time and speaker.</summary>
    public static IReadOnlyList<ChatMessage> Prompt(string question, IReadOnlyList<PassageHit> hits, string language, bool oneMeeting)
    {
        var culture = CultureInfo.GetCultureInfo(language == "sv" ? "sv-SE" : "en-GB");
        var sb = new StringBuilder();
        sb.Append("Excerpts from ").Append(oneMeeting ? "the meeting" : "the user's meetings").Append(":\n\n");
        for (var i = 0; i < hits.Count; i++)
        {
            var p = hits[i].Passage;
            var kind = p.Kind switch
            {
                PassageKind.Overview => "summary",
                PassageKind.Point => "summary point",
                PassageKind.Decision => "decision in the summary",
                PassageKind.Todo => "to-do in the summary",
                PassageKind.Remember => "worth remembering, from the summary",
                PassageKind.Note => "the user's own note",
                _ => null,
            };
            sb.Append('[').Append(i + 1).Append("] ").Append(p.Meeting.Title.Length > 0 ? p.Meeting.Title : "Untitled meeting")
              .Append(", ").Append(p.Meeting.Started.ToString("dddd d MMMM yyyy", culture))
              .Append(", at ").Append(Meeting.Clock(p.Time));
            if (kind is not null) sb.Append(" (").Append(kind).Append(')');
            sb.Append('\n');
            sb.Append(p.Kind == PassageKind.Said && p.Speaker is not null ? $"{p.Speaker}: {p.Text}" : p.Text).Append('\n');
            if (p.Context is not null) sb.Append(p.Kind == PassageKind.Said ? "" : "Said: ").Append(p.Context).Append('\n');
            sb.Append('\n');
        }
        sb.Append($"""
            Question: {question}

            Answer the question using only these excerpts.
            - Answer in {(language == "sv" ? "Swedish" : "English")}, in a few plain sentences (a short list if there are several things).
            - After each statement, cite the excerpt it comes from with its number in brackets, like [2].
            - Say who said or decided something when the excerpt shows it.
            - Don't use anything that isn't in the excerpts, and don't guess.
            - Answer directly, as a colleague who was there would. Don't talk about "excerpts" or "the summary".
            - If the excerpts don't answer the question, reply with exactly {NotFound} and nothing else.
            """);
        return
        [
            ChatMessage.System($"You answer questions about the user's meetings, only from the excerpts given, citing them. Today is {DateTime.Now.ToString("dddd d MMMM yyyy", culture)}."),
            ChatMessage.User(sb.ToString()),
        ];
    }

    [GeneratedRegex(@"\s?[\[【]\s*(\d+(?:\s*[,;–-]\s*\d+)*)\s*[\]】]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\s?[\[【][\d,;\s–-]*$")]
    private static partial Regex OpenMarker();

    /// <summary>The excerpts the answer cites, in the order it cites them, as meeting + moment + quote.</summary>
    public static List<AskCitation> Citations(string answer, IReadOnlyList<PassageHit> hits)
    {
        var result = new List<AskCitation>();
        foreach (Match match in Marker().Matches(answer))
            foreach (var number in Numbers(match.Groups[1].Value))
            {
                if (number < 1 || number > hits.Count) continue;
                var citation = Cite(hits[number - 1].Passage);
                if (!result.Contains(citation)) result.Add(citation);
            }
        return result;
    }

    // "1, 3", "2-4".
    private static IEnumerable<int> Numbers(string list)
    {
        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split(['-', '–'], StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b) && b >= a && b - a < 10)
                for (var n = a; n <= b; n++) yield return n;
            else if (int.TryParse(part, out var n)) yield return n;
        }
    }

    public static AskCitation Cite(Passage p) => new(p.Meeting.Id, p.Time, p.Quote);

    /// <summary>The answer without its [n] markers (the citations are shown on their own); while streaming, a half-written "[1" is held back too.</summary>
    public static string StripMarkers(string text, bool streaming)
    {
        var clean = Marker().Replace(text, "");
        if (streaming) clean = OpenMarker().Replace(clean, "");
        // " ." left behind where a marker stood before the full stop.
        clean = Regex.Replace(clean, @"\s+([.,;:!?])", "$1");
        return streaming ? clean : clean.Trim();
    }

    /// <summary>No writer ready: the passages themselves, as the answer.</summary>
    private static AskExchange Extractive(string question, IReadOnlyList<PassageHit> hits, string language, IProgress<string>? partial, DateTime asked)
    {
        var top = hits.Take(3).ToList();
        var sb = new StringBuilder(language == "sv" ? "Det här säger dina möten:\n" : "Here's what your meetings say:\n");
        foreach (var h in top)
        {
            var p = h.Passage;
            sb.Append("• ").Append(p.Speaker is not null && p.Kind == PassageKind.Said ? $"{p.Speaker}: " : "").Append(p.Text)
              .Append(" (").Append(p.Meeting.Title).Append(", ").Append(Meeting.Clock(p.Time)).Append(")\n");
        }
        var answer = sb.ToString().TrimEnd();
        partial?.Report(answer);
        return new AskExchange(question, answer, top.Select(h => Cite(h.Passage)).Distinct().ToList(), asked);
    }
}
