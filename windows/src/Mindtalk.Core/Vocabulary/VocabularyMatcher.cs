using System.Text.RegularExpressions;

namespace Mindtalk.Core.Vocabulary;

/// <summary>A stretch of text: where a fix is, or where it ended up.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>The fixed text, how often each entry (by list index) fixed something, and where the fixes are in it.</summary>
public sealed record VocabularyResult(string Text, IReadOnlyDictionary<int, int> Counts, IReadOnlyList<TextSpan> Ranges);

/// <summary>
/// Finds where the text should be spelled your way, for all entries at once, in one pass
/// over the original text — so one entry can never rewrite another's result ("Bert" heard
/// as "bart" and "Bart" heard as "bert" both work). Port of the Mac's VocabularyMatcher.
///
/// Per entry:
///   • Variants you added ("min dag" → Mindact): always fixed, any case.
///   • The word itself, cased or joined differently ("mindtalk", "Mind-talk"), or — for
///     single words of eight letters or more — split once into parts of at least four
///     ("mind talk"). Short names are never pieced together from bits: "han sen" stays.
///   • Endings: -s, -en, -et, -ens, -ets ("Mindtalks", "Mindtalken").
/// An unsplit match that is an ordinary dictionary word ("per", "klass", "test") is left
/// alone, as is text in capitals ("MINDTALK"). A lowercase entry keeps a sentence's capital
/// ("Mejl skickat."). Nothing matches across a line break.
/// </summary>
public sealed class VocabularyMatcher
{
    /// <summary>Where, in the original text, and to what.</summary>
    public readonly record struct Fix(TextSpan Range, string Replacement, int Entry);

    private sealed record Rule(int Entry, Regex Regex, bool IsVariant);

    private const string Endings = "(s|en|et|ens|ets)?";

    private readonly string[] _words;
    private readonly List<Rule> _rules = [];

    public VocabularyMatcher(IEnumerable<(string Word, IReadOnlyList<string> HeardAs)> entries)
    {
        var list = entries.ToList();
        _words = list.Select(e => e.Word).ToArray();
        for (var i = 0; i < list.Count; i++)
        {
            var (word, heardAs) = list[i];
            foreach (var variant in heardAs)
            {
                var parts = variant.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape).ToList();
                if (parts.Count == 0) continue;
                _rules.Add(new Rule(i, Pattern(string.Join("[ \\t]+", parts), "(s)?"), IsVariant: true));
            }
            // The word as written, its own spaces/hyphens optional…
            var pieces = word.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
            var forms = new List<string> { string.Join("[ \\t-]?", pieces.Select(Regex.Escape)) };
            // … and a single word of eight letters or more split once, parts of four or
            // more — "mind talk" → Mindtalk, but never "han sen" → Hansen.
            if (pieces.Length == 1)
            {
                var letters = TextElements.Of(pieces[0]).ToList();
                if (letters.Count >= 8)
                {
                    for (var cut = 4; cut <= letters.Count - 4; cut++)
                        forms.Add(Regex.Escape(string.Concat(letters.Take(cut))) + "[ \\t-]"
                                  + Regex.Escape(string.Concat(letters.Skip(cut))));
                }
            }
            if (TextElements.Of(word).Count(TextElements.IsLetterOrNumber) >= 3)
                _rules.Add(new Rule(i, Pattern(string.Join("|", forms), Endings), IsVariant: false));
        }
    }

    private static Regex Pattern(string core, string endings) =>
        new($"(?<![\\p{{L}}\\p{{N}}])(?:{core}){endings}(?![\\p{{L}}\\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsSplitter(char c) => c is ' ' or '\t' or '-';

    /// <summary>Every fix for <paramref name="text"/>, non-overlapping, in order.</summary>
    public IReadOnlyList<Fix> Fixes(string text, Func<string, bool> isWord)
    {
        var candidates = new List<(Fix Fix, int Priority)>();
        for (var order = 0; order < _rules.Count; order++)
        {
            var rule = _rules[order];
            foreach (Match match in rule.Regex.Matches(text))
            {
                var matched = match.Value;
                var ending = match.Groups[1].Success ? match.Groups[1].Value.ToLowerInvariant() : "";
                var word = _words[rule.Entry];
                var replacement = word + ending;
                if (!rule.IsVariant)
                {
                    var split = matched.Any(IsSplitter) && !word.Any(c => c is ' ' or '-');
                    var letters = TextElements.Letters(matched);
                    // In capitals on purpose ("MINDTALK").
                    if (TextElements.Count(letters) > 1 && TextElements.IsAllUpper(letters) && !TextElements.IsAllUpper(word)) continue;
                    // An ordinary word that happens to look like the entry: a short one
                    // ("per", "test") or one with an ending ("klas" + s = "klass"). Longer
                    // names are recased even if the dictionary knows them (Google, Notion).
                    var hasEnding = ending.Length > 0;
                    var oneWord = !matched.Any(IsSplitter);
                    if (!split && oneWord && (hasEnding || TextElements.Count(TextElements.Letters(word)) <= 4)
                        && isWord(matched.ToLowerInvariant())) continue;
                }
                // A lowercase entry at the start of a sentence keeps the capital.
                if (TextElements.IsAllLower(word) && matched.Length > 0 && char.IsUpper(matched[0]))
                {
                    var first = TextElements.Of(replacement).FirstOrDefault() ?? "";
                    replacement = first.ToUpperInvariant() + replacement[first.Length..];
                }
                if (replacement == matched) continue;
                candidates.Add((new Fix(new TextSpan(match.Index, match.Length), replacement, rule.Entry),
                                (rule.IsVariant ? 0 : 1_000_000) + order));
            }
        }
        // Earliest first; for the same start, the longest; then variants before words, then list order.
        candidates.Sort((a, b) =>
            a.Fix.Range.Start != b.Fix.Range.Start ? a.Fix.Range.Start.CompareTo(b.Fix.Range.Start)
            : a.Fix.Range.Length != b.Fix.Range.Length ? b.Fix.Range.Length.CompareTo(a.Fix.Range.Length)
            : a.Priority.CompareTo(b.Priority));
        var chosen = new List<Fix>();
        var end = 0;
        foreach (var (fix, _) in candidates)
        {
            if (fix.Range.Start < end) continue;
            chosen.Add(fix);
            end = fix.Range.End;
        }
        return chosen;
    }

    /// <summary>
    /// The fixed text, how often each entry fixed something, and where the fixes ended up
    /// in the new text (for highlighting).
    /// </summary>
    public VocabularyResult Apply(string text, Func<string, bool> isWord)
    {
        var fixes = Fixes(text, isWord);
        if (fixes.Count == 0) return new VocabularyResult(text, new Dictionary<int, int>(), []);
        var output = new System.Text.StringBuilder(text);
        var counts = new Dictionary<int, int>();
        var ranges = new List<TextSpan>();
        var shift = 0;
        foreach (var fix in fixes)
        {
            var location = fix.Range.Start + shift;
            output.Remove(location, fix.Range.Length).Insert(location, fix.Replacement);
            ranges.Add(new TextSpan(location, fix.Replacement.Length));
            shift += fix.Replacement.Length - fix.Range.Length;
            counts[fix.Entry] = counts.GetValueOrDefault(fix.Entry) + 1;
        }
        return new VocabularyResult(output.ToString(), counts, ranges);
    }
}
