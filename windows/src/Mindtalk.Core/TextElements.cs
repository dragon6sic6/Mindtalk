using System.Globalization;
using System.Text;

namespace Mindtalk.Core;

/// <summary>
/// Swift counts and tests <c>Character</c>s — what a reader sees as one letter ("é" may be
/// two code points). The ports go through text elements so lengths and letter tests agree.
/// </summary>
internal static class TextElements
{
    public static IEnumerable<string> Of(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) yield return e.GetTextElement();
    }

    public static int Count(string text) => new StringInfo(text).LengthInTextElements;

    /// Swift's <c>Character.isLetter</c>: judged by its first code point.
    public static bool IsLetter(string element) =>
        element.Length > 0 && Rune.IsLetter(Rune.GetRuneAt(element, 0));

    /// Swift's <c>isLetter || isNumber</c>.
    public static bool IsLetterOrNumber(string element) =>
        element.Length > 0 && Rune.GetRuneAt(element, 0) is var r && (Rune.IsLetter(r) || Rune.IsNumber(r));

    public static bool IsNumber(string element) =>
        element.Length > 0 && Rune.IsNumber(Rune.GetRuneAt(element, 0));

    /// Only the letters (whole elements, accents included).
    public static string Letters(string text) => string.Concat(Of(text).Where(IsLetter));

    /// Swift's <c>.whitespaces</c>: spaces and tabs, not line breaks.
    public static bool IsHorizontalSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    public static string TrimHorizontal(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsHorizontalSpace(s[start])) start++;
        while (end > start && IsHorizontalSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// Swift's <c>uppercased()</c> is the full mapping ("ß" → "SS"); .NET's isn't, so a
    /// lowercase-only letter like ß must not pass as capitals.
    public static bool IsAllUpper(string s) => s == s.ToUpperInvariant() && !s.Any(char.IsLower);

    public static bool IsAllLower(string s) => s == s.ToLowerInvariant();
}

/// <summary>Writes a file so a crash halfway never leaves half a file behind.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
