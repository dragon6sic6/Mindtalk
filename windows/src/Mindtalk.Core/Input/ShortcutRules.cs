namespace Mindtalk.Core.Input;

/// <summary>
/// What Settings accepts as the Say a thought keys: modifiers only (left and right distinct),
/// none of them shared with the dictation key — as on the Mac.
/// </summary>
public static class ThoughtKeyRules
{
    /// Why <paramref name="key"/> can't be the thought keys, or null if it can.
    public static string? Problem(Hotkey key, Hotkey dictation)
    {
        if (!key.IsModifier)
            return Loc.T("Välj en eller flera modifierartangenter, som vänster Alt. En vanlig tangent skulle sluta skriva.",
                         "Choose one or more modifier keys, like left Alt. A regular key would stop typing.");
        var shared = key.Modifiers & dictation.Modifiers;
        if (shared == Modifiers.None) return null;
        var names = string.Join(" + ", key.ModifierKeys.Where(k => (k.Modifiers & shared) != 0).Select(k => k.InlineName));
        return Loc.T($"Dikteringen använder redan {names}. Välj andra tangenter.",
                     $"Dictation already uses {names}. Choose other keys.");
    }

    /// Left Alt, or right Ctrl when the dictation key already uses left Alt.
    public static Hotkey Default(Hotkey dictation) =>
        Problem(Hotkey.DefaultThought, dictation) is null ? Hotkey.DefaultThought : new Hotkey(VirtualKey.RightCtrl, Modifiers.RightCtrl);
}

/// <summary>An app-wide shortcut from a key press in a shortcut field.</summary>
public static class ShortcutRules
{
    private static readonly int[] ModifierKeys =
    [
        VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu, VirtualKey.LeftWin, VirtualKey.RightWin,
        VirtualKey.LeftShift, VirtualKey.RightShift, VirtualKey.LeftCtrl, VirtualKey.RightCtrl, VirtualKey.LeftAlt, VirtualKey.RightAlt,
    ];

    public static bool IsModifier(int key) => ModifierKeys.Contains(key);

    /// The shortcut for a press, or null while only modifiers are held. A regular key needs
    /// Ctrl, Alt or Windows with it (Shift alone would stop typing capitals); F1–F24 may stand alone.
    public static Shortcut? FromPress(int key, bool ctrl, bool alt, bool shift, bool win)
    {
        if (IsModifier(key) || key == VirtualKey.Escape) return null;
        var function = key is >= VirtualKey.F1 and <= VirtualKey.F24;
        if (!ctrl && !alt && !win && !function) return null;
        return new Shortcut(key, ctrl, alt, shift, win);
    }

    /// "12 MB", "1.4 GB" — sizes on disk, in the app's language.
    public static string Size(long bytes)
    {
        if (bytes >= 1_000_000_000) return (bytes / 1e9).ToString("0.0", Loc.Culture) + " GB";
        return Math.Max(bytes > 0 ? 1 : 0, (long)Math.Round(bytes / 1e6)).ToString(Loc.Culture) + " MB";
    }
}
