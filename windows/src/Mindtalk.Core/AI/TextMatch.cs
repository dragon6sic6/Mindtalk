using System.Globalization;
using System.Text;

namespace Mindtalk.Core.AI;

/// <summary>
/// Plain-text tools for the writers: words without case or accents, Swedish and English
/// stop words, which language a text is in, and how well a quote matches a line.
/// </summary>
public static class TextMatch
{
    /// Lower case, accents and å/ä/ö folded (so "möte" finds "mote" and "Möte").
    public static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// The words of a text, folded. Digits count as words ("Q4", "2026").
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var folded = Fold(text.Replace('’', '\''));
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var isWord = i < folded.Length && (char.IsLetterOrDigit(folded[i]) || (folded[i] == '\'' && start >= 0));
            if (isWord) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                words.Add(folded[start..i].Trim('\''));
                start = -1;
            }
        }
        words.RemoveAll(w => w.Length == 0);
        return words;
    }

    // Folded, as Words returns them.
    private static readonly HashSet<string> Stop =
    [
        // English
        "a", "an", "the", "and", "or", "but", "of", "to", "in", "on", "at", "for", "with", "by", "from", "about", "as",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "have", "has", "had", "it", "its",
        "it's", "this", "that", "these", "those", "there", "here", "what", "which", "who", "whom", "whose", "when",
        "where", "why", "how", "i", "me", "my", "we", "us", "our", "you", "your", "he", "him", "his", "she", "her",
        "they", "them", "their", "not", "no", "yes", "so", "if", "then", "than", "too", "very", "can", "could", "would",
        "should", "will", "shall", "may", "might", "must", "just", "any", "all", "some", "into", "out", "up", "down",
        "over", "again", "also", "said", "say", "says", "tell", "anything", "something", "ok", "okay", "i'm", "i'll",
        "we'll", "we're", "don't", "didn't", "let's", "get", "got", "one",
        // Swedish (folded: å/ä → a, ö → o)
        "och", "att", "det", "som", "en", "ett", "i", "pa", "ar", "var", "vi", "jag", "du", "ni", "han", "hon", "de",
        "dem", "den", "dom", "har", "hade", "med", "for", "till", "av", "om", "inte", "men", "sa", "da", "nar", "vad",
        "vem", "vilka", "vilken", "vilket", "hur", "varfor", "kan", "ska", "skulle", "kommer", "blir", "bli", "blev",
        "var", "varit", "vara", "ocksa", "bara", "nu", "mig", "dig", "oss", "er", "sig", "min", "mitt", "mina", "din",
        "ditt", "vår", "vart", "vara", "deras", "hans", "hennes", "sin", "sitt", "sina", "fran", "ut", "upp", "ner",
        "eller", "an", "ju", "nog", "lite", "mycket", "alla", "allt", "nagon", "nagot", "nagra", "ingen", "inget",
        "inga", "har", "dar", "denna", "detta", "dessa", "sagt", "sade", "sa", "ja", "nej", "eh", "oh", "um", "ok",
    ];

    /// The text without its questions: "Shall we start? We start now." → " We start now."
    public static string Statements(string text) =>
        string.Concat(System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+").Where(s => !s.TrimEnd().EndsWith('?')).Select(s => " " + s));

    public static bool IsStopWord(string folded) => Stop.Contains(folded);

    /// The words that carry meaning: no stop words, nothing shorter than two letters.
    public static List<string> ContentWords(string text) =>
        Words(text).Where(w => w.Length > 1 && !Stop.Contains(w)).ToList();

    private static readonly HashSet<string> SwedishMarkers =
        ["och", "att", "det", "ar", "jag", "vi", "inte", "pa", "som", "en", "ett", "med", "har", "for", "ska", "kan",
         "vad", "hur", "nar", "om", "till", "den", "sa", "da", "men", "eller", "vill", "mote", "motet", "beslut", "ocksa"];
    private static readonly HashSet<string> EnglishMarkers =
        ["the", "and", "is", "are", "to", "of", "we", "i", "it", "that", "in", "on", "with", "for", "what", "how",
         "when", "was", "were", "be", "have", "has", "not", "this", "you", "will", "about", "meeting", "decided"];

    /// "sv" or "en" — by the common words in it (and å/ä/ö); English when it can't tell.
    public static string Language(string text)
    {
        int sv = 0, en = 0;
        foreach (var c in text) if (c is 'å' or 'ä' or 'ö' or 'Å' or 'Ä' or 'Ö') sv++;
        foreach (var w in Words(text))
        {
            if (SwedishMarkers.Contains(w)) sv++;
            if (EnglishMarkers.Contains(w)) en++;
        }
        return sv > en ? "sv" : "en";
    }

    /// <summary>
    /// How much of <paramref name="quote"/> is in <paramref name="line"/>, 0…1: the share of the
    /// quote's words found in the line, in order (a model may drop a word or two, or quote only part).
    /// </summary>
    public static double Coverage(string quote, string line)
    {
        var q = Words(quote);
        if (q.Count == 0) return 0;
        var l = Words(line);
        if (l.Count == 0) return 0;
        // Longest common subsequence of words, with near-equal words counting (a missed letter).
        var dp = new int[q.Count + 1, l.Count + 1];
        for (var i = 1; i <= q.Count; i++)
            for (var j = 1; j <= l.Count; j++)
                dp[i, j] = SameWord(q[i - 1], l[j - 1]) ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[q.Count, l.Count] / (double)q.Count;
    }

    /// The same word, give or take an ending or a typo: "numbers"/"number", "möte"/"mötet".
    public static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 4 || b.Length < 4) return false;
        var shorter = a.Length <= b.Length ? a : b;
        var longer = ReferenceEquals(shorter, a) ? b : a;
        if (longer.StartsWith(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 3) return true;
        return a.Length == b.Length ? Distance(a, b) <= 1 : Math.Abs(a.Length - b.Length) == 1 && Distance(a, b) <= 1;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// Content-word overlap between two texts, 0…1 of the smaller one: "says the same thing".
    public static double Overlap(string a, string b)
    {
        var wa = ContentWords(a);
        var wb = ContentWords(b);
        if (wa.Count == 0 || wb.Count == 0) return 0;
        var (small, large) = wa.Count <= wb.Count ? (wa, wb) : (wb, wa);
        var hits = small.Count(w => large.Any(x => SameWord(w, x)));
        return hits / (double)small.Count;
    }
}
