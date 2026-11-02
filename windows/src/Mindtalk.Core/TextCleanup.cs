using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mindtalk.Core;

/// <summary>
/// Tidies what the speech model heard before it's typed. Rules only (port of the Mac
/// app's layer 1): hesitation sounds (eh, öh, um) go, and "ny rad" / "nytt stycke"
/// (new line / new paragraph) become line breaks. Only what can't be mistaken for
/// ordinary words — "punkt" and "komma" are words too, so they're left alone.
/// </summary>
public static class TextCleanup
{
    public static string Apply(string text, bool fillers = true, bool commands = true)
    {
        if (fillers) text = RemoveFillers(text);
        if (commands) text = ApplyCommands(text);
        return Tidy(text);
    }

    // ---- Fillers ------------------------------------------------------------

    // Sounds, not words: eh, eeh, ehm, öh, öhm, äh, uh, uhm, um, hm, hmm, erm.
    // ("er", "mm" and "ah" are left alone — they're words or units too.)
    private static readonly Regex Filler = new(
        @",?[ \t]*(?<![\p{L}\p{N}])(?:e+h+m*|e{2,}|ö+h+m*|ö+m+|ä+h+m*|u+h+m*|u+m+|h+m+|e+r+m+)(?![\p{L}\p{N}])(?:[ \t]*,)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string RemoveFillers(string text)
    {
        var sb = new StringBuilder(text);
        foreach (var match in Filler.Matches(text).Reverse())
        {
            var startsSentence = true;
            for (var i = match.Index - 1; i >= 0; i--)
            {
                var c = sb[i];
                if (char.IsWhiteSpace(c) || c == ',') continue;
                startsSentence = ".!?\n".Contains(c);
                break;
            }
            sb.Remove(match.Index, match.Length);
            // "Eh, jag tänkte" → "Jag tänkte".
            if (startsSentence) CapitalizeNextLetter(sb, match.Index);
        }
        var result = sb.ToString();
        // Nothing but a hesitation? Nothing to type.
        return result.Any(char.IsLetterOrDigit) ? result : "";
    }

    // ---- Voice commands -----------------------------------------------------

    private static readonly Regex Paragraph = new(
        @"[ \t]*(?<![\p{L}\p{N}])(?:nytt[ \t]*stycke|new[ \t]*paragraph)(?![\p{L}\p{N}])[.,;:!?]?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Line = new(
        @"[ \t]*(?<![\p{L}\p{N}])(?:ny[ \t]*rad|new[ \t]*line)(?![\p{L}\p{N}])[.,;:!?]?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// "en ny rad", "i ny rad", "a new line": the phrase used as words, not a command.
    private static readonly HashSet<string> Quoting = new()
    {
        "en", "ett", "den", "det", "denna", "varje", "någon", "annan", "sin", "min", "din", "vår", "er",
        "i", "på", "till", "med", "av", "per", "om", "från", "för",
        "a", "an", "the", "each", "every", "one", "this", "that", "another", "in", "on", "of", "to", "with",
    };

    public static string ApplyCommands(string text)
    {
        foreach (var (pattern, breaks) in new[] { (Paragraph, "\n\n"), (Line, "\n") })
        {
            var sb = new StringBuilder(text);
            foreach (var match in pattern.Matches(text).Reverse())
            {
                // The word right before, with nothing but spaces in between.
                var before = sb.ToString(0, match.Index).TrimEnd(' ', '\t');
                var end = before.Length;
                var start = end;
                while (start > 0 && char.IsLetter(before[start - 1])) start--;
                var previousWord = before[start..end].ToLowerInvariant();
                if (Quoting.Contains(previousWord)) continue;
                sb.Remove(match.Index, match.Length).Insert(match.Index, breaks);
                CapitalizeNextLetter(sb, match.Index + breaks.Length);
            }
            text = sb.ToString();
        }
        return text;
    }

    // ---- Tidying ------------------------------------------------------------

    private static readonly Regex Spaces = new(@"[ \t]{2,}");
    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([.,;:!?])");
    private static readonly Regex DoubledComma = new(@",\s*([.!?,])");

    public static string Tidy(string text)
    {
        text = Spaces.Replace(text, " ");
        text = SpaceBeforePunctuation.Replace(text, "$1");
        text = DoubledComma.Replace(text, "$1");
        // Each line trimmed; punctuation left at the start by a removed word goes too.
        var lines = text.Split('\n').Select(line =>
        {
            line = TrimHorizontal(line);
            while (line.Length > 0 && ".,;:!?".Contains(line[0])) line = TrimHorizontal(line[1..]);
            return line;
        });
        // Only spaces trimmed at the ends — a spoken line break at the end stays.
        text = TrimHorizontal(string.Join("\n", lines));
        var first = text.ToList().FindIndex(char.IsLetter);
        if (first >= 0 && !text.Take(first).Any(char.IsLetterOrDigit))
            text = text[..first] + char.ToUpperInvariant(text[first]) + text[(first + 1)..];
        return text;
    }

    /// Swift's `.whitespaces`: spaces and tabs, but not line breaks.
    private static bool IsHorizontalSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private static string TrimHorizontal(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsHorizontalSpace(s[start])) start++;
        while (end > start && IsHorizontalSpace(s[end - 1])) end--;
        return s[start..end];
    }

    private static void CapitalizeNextLetter(StringBuilder sb, int location)
    {
        for (var i = location; i < sb.Length; i++)
        {
            if (!char.IsLetterOrDigit(sb[i])) continue;
            if (char.IsLetter(sb[i])) sb[i] = char.ToUpperInvariant(sb[i]);
            return;
        }
    }
}
