using System.Globalization;

namespace Mindtalk.Core;

/// <summary>
/// Swedish or English (like the Mac app): Windows' display language, unless a language
/// is chosen in Settings. Fixed for the life of the process — switching relaunches.
/// </summary>
public static class Loc
{
    public static bool IsSwedish { get; set; } = SystemIsSwedish;

    /// Windows' own choice: Swedish if that comes first, English otherwise.
    public static bool SystemIsSwedish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "sv";

    /// What a choice means on this PC (System resolves to Swedish or English).
    public static AppLanguage Resolve(AppLanguage language) =>
        language != AppLanguage.System ? language : SystemIsSwedish ? AppLanguage.Swedish : AppLanguage.English;

    public static void Use(AppLanguage language) => IsSwedish = Resolve(language) == AppLanguage.Swedish;

    /// For dates and weekday names in the running language.
    public static CultureInfo Culture => IsSwedish ? CultureInfo.GetCultureInfo("sv-SE") : CultureInfo.GetCultureInfo("en-US");

    public static string T(string swedish, string english) => IsSwedish ? swedish : english;
}
