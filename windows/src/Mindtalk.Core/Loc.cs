using System.Globalization;

namespace Mindtalk.Core;

/// <summary>Swedish or English, following Windows' display language (like the Mac app).</summary>
public static class Loc
{
    public static bool IsSwedish { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "sv";

    public static string T(string swedish, string english) => IsSwedish ? swedish : english;
}
