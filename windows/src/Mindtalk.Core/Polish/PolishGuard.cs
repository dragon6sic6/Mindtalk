using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mindtalk.Core.Polish;

/// <summary>
/// The AI polish guard. The on-device model may tidy a dictation, never change what was
/// said. Its answer is accepted only if, word by word, it can be read back onto the
/// original with nothing but:
///   • words kept (any case),
///   • words split or joined ("köpmjöl" → "köp mjöl"),
///   • deletions of hesitation sounds, words said twice, spoken punctuation ("punkt",
///     "comma"), or what a self-correction replaces ("tisdag, nej jag menar onsdag" → "onsdag").
/// Nothing may be added, and a negation (inte, aldrig, not, don't…) is never dropped —
/// "jag godkänner inte" can't become "jag godkänner". Only ordinary punctuation may
/// appear; control and bidi characters are refused. Port of the Mac's PolishGuard.
/// </summary>
public static class PolishGuard
{
    public static bool Accepts(string polished, string original)
    {
        if (!OnlyOrdinaryCharacters(polished) || GluesWords(polished, original)) return false;
        var before = Words(original);
        var after = Words(polished);
        if (after.Count == 0 || after.Count < before.Count * 0.5) return false;

        int i = 0, j = 0;
        while (j < after.Count)
        {
            if (i < before.Count && Match(before, i, after, j) is { } step)
            {
                i += step.Before;
                j += step.After;
                continue;
            }
            // Not the next original word: the words up to its next occurrence were dropped.
            var k = -1;
            for (var c = i; c < Math.Min(before.Count, i + 9); c++)
                if (Match(before, c, after, j) is not null) { k = c; break; }
            if (k < 0 || !Deletable(before, i, k)) return false;
            i = k;
        }
        return i == before.Count || Deletable(before, i, before.Count);
    }

    // ---- Words --------------------------------------------------------------------

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+(?:'[\p{L}\p{N}]+)*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Words(string text)
    {
        var normalized = text.ToLowerInvariant().Replace('’', '\'');
        return Word.Matches(normalized).Select(m => m.Value).ToList();
    }

    /// The same word, or one split into two/three, or two/three joined into one.
    private static (int Before, int After)? Match(IReadOnlyList<string> before, int i, IReadOnlyList<string> after, int j)
    {
        if (before[i] == after[j]) return (1, 1);
        // Numbers are never joined or split: "1 2" isn't "12".
        if (HasNumber(before[i]) || HasNumber(after[j])) return null;
        for (var n = 2; n <= 3; n++)
        {
            if (j + n <= after.Count && string.Concat(after.Skip(j).Take(n)) == before[i]) return (1, n);
            if (i + n <= before.Count && string.Concat(before.Skip(i).Take(n)) == after[j]) return (n, 1);
        }
        return null;
    }

    private static bool HasNumber(string word) => TextElements.Of(word).Any(TextElements.IsNumber);

    // ---- What may be dropped --------------------------------------------------------

    private static readonly HashSet<string> Fillers =
        ["eh", "ehm", "eeh", "öh", "öhm", "äh", "ähm", "um", "umm", "uh", "uhm", "hm", "hmm", "mm", "erm"];

    private static readonly HashSet<string> SpokenPunctuation =
    [
        "punkt", "komma", "frågetecken", "utropstecken", "kolon", "semikolon", "tankstreck", "period", "comma",
        "colon", "semicolon", "question", "exclamation", "mark", "point", "dash", "full", "stop",
    ];

    /// Never dropped: they flip the meaning.
    private static readonly HashSet<string> Negations =
    [
        "inte", "ej", "icke", "aldrig", "ingen", "inget", "inga", "ingenting",
        "not", "never", "nothing", "nobody", "none", "neither", "nor", "cannot",
    ];

    private static readonly string[][] CorrectionCues =
    [
        ["jag", "menar"], ["menar", "jag"], ["förlåt"], ["ursäkta"], ["rättelse"], ["i", "mean"], ["sorry"],
        ["correction"], ["or", "rather"], ["eller", "snarare"],
    ];

    /// May the words before[start..end) go?
    private static bool Deletable(IReadOnlyList<string> before, int start, int end)
    {
        if (start >= end) return true;
        var dropped = before.Skip(start).Take(end - start).ToList();
        if (dropped.Any(w => Negations.Contains(w) || w.EndsWith("n't", StringComparison.Ordinal))) return false;
        // A self-correction: what's replaced and the cue go, the correction stays —
        // "tisdag, nej jag menar | onsdag". The cue must end the dropped run, or be
        // followed only by the words just before the run said again ("till Per,
        // förlåt, till | Anna").
        if (dropped.Count <= 8 && CorrectionCues.Any(cue => EndsCorrection(dropped, cue, before, start))) return true;
        bool InRun(int idx) => idx >= start && idx < end;
        for (var idx = start; idx < end; idx++)
        {
            var w = before[idx];
            if (Fillers.Contains(w) || SpokenPunctuation.Contains(w)) continue;
            // Said twice in a row — one copy may go, never both.
            var repeated = (idx > 0 && !InRun(idx - 1) && before[idx - 1] == w)
                           || (idx + 1 < before.Count && !InRun(idx + 1) && before[idx + 1] == w);
            if (!repeated) return false;
        }
        return true;
    }

    private static bool EndsCorrection(List<string> dropped, string[] cue, IReadOnlyList<string> before, int runStart)
    {
        if (dropped.Count < cue.Length) return false;
        for (var start = 0; start <= dropped.Count - cue.Length; start++)
        {
            if (!dropped.Skip(start).Take(cue.Length).SequenceEqual(cue)) continue;
            var after = dropped.Skip(start + cue.Length).ToList();
            if (after.Count == 0) return true;
            // What follows the cue repeats the words right before the run.
            var lead = runStart - after.Count;
            if (lead >= 0 && before.Skip(lead).Take(after.Count).SequenceEqual(after)) return true;
        }
        return false;
    }

    /// Punctuation squeezed between two letters ("mind.act") that wasn't in the original.
    private static readonly Regex Glued = new(@"\p{L}[.,;:!?/&+@#*]\p{L}", RegexOptions.CultureInvariant);

    private static bool GluesWords(string polished, string original) =>
        Glued.Matches(polished).Count > Glued.Matches(original).Count;

    // ---- Characters ---------------------------------------------------------------

    private const string AllowedPunctuation = ".,;:!?'\"’‘“”«»()-–—…%/&+@#*";

    private static bool OnlyOrdinaryCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or ' ') continue;
            switch (Rune.GetUnicodeCategory(rune))
            {
                // Letters (Apple's letter set includes marks), accents, decimal digits.
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.DecimalDigitNumber:
                    continue;
            }
            if (rune.IsBmp && AllowedPunctuation.Contains((char)rune.Value)) continue;
            return false;
        }
        return true;
    }
}
