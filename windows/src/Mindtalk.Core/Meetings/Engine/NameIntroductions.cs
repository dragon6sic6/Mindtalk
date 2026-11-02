using System.Globalization;
using System.Text.RegularExpressions;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// The round of names: finds the name in "Hi, my name is Anna", "I'm Erik", "Jag heter Sara",
/// "Hej, jag heter Sara", "Mitt namn är Per". Only the first words someone says count, and only
/// something that looks like a name (not "I'm fine", "jag heter inte").
/// </summary>
public static partial class NameIntroductions
{
    // The introduction, then one or two name words (a first name, maybe a last name).
    [GeneratedRegex(@"(?:\bmy\s+name\s+is|\bmy\s+name's|\bi\s+am|\bi'm|\bthis\s+is|\bjag\s+heter|\bmitt\s+namn\s+är|\bjag\s+är|\bhär\s+är)\s+(?<name>\p{L}[\p{L}'\-]*(?:\s+\p{Lu}[\p{L}'\-]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Introduction();

    /// Words after "I'm" / "jag är" that aren't names.
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "a", "an", "the", "not", "so", "very", "just", "fine", "good", "here", "sorry", "glad", "happy", "going", "gonna",
        "from", "in", "at", "on", "with", "working", "responsible", "also", "still", "sure", "okay", "ok", "back", "new",
        "really", "pretty", "quite", "looking", "trying", "doing", "taking", "the", "your", "our", "one", "and", "but",
        // Swedish
        "inte", "en", "ett", "också", "här", "glad", "så", "bra", "ansvarig", "med", "från", "på", "i", "ny", "nya",
        "lite", "väldigt", "mycket", "jätte", "själv", "tillbaka", "och", "men", "det", "den", "du", "ni", "vi", "han", "hon",
    };

    /// <summary>The name someone introduces themselves with, if any. Capitalized as a name.</summary>
    public static string? Find(string text)
    {
        foreach (Match match in Introduction().Matches(text))
        {
            var words = match.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0 || NotNames.Contains(words[0])) continue;
            var intro = match.Value[..(match.Groups["name"].Index - match.Index)].Trim().ToLowerInvariant();
            var explicitName = intro.Contains("name") || intro.Contains("namn") || intro.Contains("heter");
            // "I'm" and "jag är" are followed by plenty that isn't a name: take it only if it's capitalized.
            if (!explicitName && !char.IsUpper(words[0][0])) continue;
            // A last name only if it's capitalized; Parakeet writes names that way.
            if (words.Count > 1 && (NotNames.Contains(words[1]) || !char.IsUpper(words[1][0]))) words.RemoveAt(1);
            return string.Join(' ', words.Select(Capitalize));
        }
        return null;
    }

    private static string Capitalize(string word)
    {
        word = word.Trim('\'', '-');
        return word.Length == 0 ? word : char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..];
    }
}
