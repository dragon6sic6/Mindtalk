namespace Mindtalk.Core.Input;

/// <summary>
/// Say a thought's key: one or more modifiers held together (left Alt by default), never
/// sharing a key with the dictation key. When they would share one — your dictation key uses
/// left Alt — thoughts move to Ctrl+Alt+T (the Mac's ⌃⌥T), so neither steals the other.
/// </summary>
public static class ThoughtKeys
{
    /// Do the two keys share a key? Left and right are different keys.
    public static bool Overlap(Hotkey dictation, Hotkey thought) =>
        dictation.IsModifier ? (dictation.Modifiers & thought.Modifiers) != 0 : !thought.IsModifier && dictation.Key == thought.Key;

    /// What saying a thought uses now: a key to hold, or (<c>Fallback</c>) Ctrl+Alt+T.
    /// Off (null) is neither.
    public static (Hotkey? Hold, bool Fallback) Resolve(Hotkey dictation, Hotkey? thought) => thought switch
    {
        null => (null, false),
        { IsModifier: false } => (null, true),
        _ when Overlap(dictation, thought) => (null, true),
        _ => (thought, false),
    };

    /// Can <paramref name="picked"/> be the thought key? Null if so, else why not.
    public static string? Problem(Hotkey dictation, Hotkey picked)
    {
        if (!picked.IsModifier)
            return Loc.T("Välj en eller flera modifierartangenter, till exempel vänster Alt.",
                         "Choose one or more modifier keys, for example left Alt.");
        if (Overlap(dictation, picked))
            return Loc.T($"{dictation.Name} används redan för diktering. Tankar och diktering kan inte dela tangent.",
                         $"{dictation.Name} is already used for dictation. Thoughts and dictation can’t share a key.");
        return null;
    }

    /// "Hold left Alt" / "Ctrl+Alt+T" — for the tray panel's trailing edge.
    public static string Hint(Hotkey dictation, Hotkey? thought)
    {
        var (hold, fallback) = Resolve(dictation, thought);
        if (hold is not null) return Loc.T($"Håll {hold.InlineName}", $"Hold {hold.InlineName}");
        return fallback ? Shortcut.Thought.ToString() : "";
    }
}
