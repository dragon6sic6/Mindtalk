using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mindtalk.Platform;

/// <summary>
/// Is it an ordinary Swedish or English word? Then the vocabulary leaves it alone (doesn't
/// recase or "fix" it). Asks Windows' own spell checkers (the Spell Checking API), which
/// are there for the languages installed with typing support; the answers are cached.
/// Callable from any thread.
/// </summary>
public static class SpellCheck
{
    private static readonly string[] Wanted = ["sv-SE", "en-US"];
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Cache = [];
    private static List<(string Language, ISpellChecker Checker)>? _checkers;

    /// Which of Swedish and English this PC can check.
    public static IReadOnlyList<string> Languages
    {
        get { lock (Gate) return Checkers().Select(c => c.Language).ToList(); }
    }

    public static bool IsDictionaryWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        lock (Gate)
        {
            if (Cache.TryGetValue(word, out var known)) return known;
            known = Checkers().Any(c => IsCorrect(c.Checker, word));
            if (Cache.Count > 5_000) Cache.Clear();
            Cache[word] = known;
            return known;
        }
    }

    private static bool IsCorrect(ISpellChecker checker, string word)
    {
        try
        {
            var errors = checker.Check(word);
            try
            {
                var result = errors.Next(out var error);
                if (error is not null) Marshal.ReleaseComObject(error);
                return result == 1;   // S_FALSE: no spelling error
            }
            finally { Marshal.ReleaseComObject(errors); }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SpellCheck: check failed ({ex.GetType().Name})");
            return false;
        }
    }

    private static List<(string Language, ISpellChecker Checker)> Checkers()
    {
        if (_checkers is not null) return _checkers;
        _checkers = [];
        try
        {
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("7ab36653-1796-484b-bdfa-e74f1db7c1dc"), throwOnError: true)!)!;
            foreach (var language in Wanted)
                if (factory.IsSupported(language)) _checkers.Add((language, factory.CreateSpellChecker(language)));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SpellCheck: no spell checker ({ex.GetType().Name})");
        }
        return _checkers;
    }

    // spellcheck.h — only the slots up to the last one used.

    [ComImport, Guid("8e018a9d-2415-4677-bf08-794ea61f94bb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object SupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("b6fd0b71-e2bc-4653-8d05-f197e412770b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string LanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803e3bd4-2828-4410-8290-418d1d73c762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.IUnknown)] out object? error);
    }
}
