using System.Text.Json.Serialization;

namespace Mindtalk.Core.Input;

/// <summary>
/// An app-wide shortcut like Ctrl+Alt+M: a regular key with modifiers, either side
/// (registered with Windows' RegisterHotKey). The Mac's ⌃⌥⌘M, ⌃⌥V… — Windows has no
/// Command key, so ⌃⌥ maps to Ctrl+Alt and ⌥⌘ to Ctrl+Shift (Ctrl+Alt+digit is AltGr+digit,
/// which types @, £, $ on many layouts).
/// </summary>
public sealed record Shortcut(int Key, bool Ctrl, bool Alt, bool Shift, bool Win = false)
{
    /// ⌃⌥⌘M: start or end a meeting, from any app.
    public static readonly Shortcut Meeting = new('M', Ctrl: true, Alt: true, Shift: false);
    /// ⌃⌥V: paste the last dictation again.
    public static readonly Shortcut PasteLast = new('V', Ctrl: true, Alt: true, Shift: false);
    /// ⌃⌥T: say a thought, when the dictation key already uses left Alt.
    public static readonly Shortcut Thought = new('T', Ctrl: true, Alt: true, Shift: false);
    /// ⌥⌘P: pause or resume the meeting.
    public static readonly Shortcut Pause = new('P', Ctrl: true, Alt: false, Shift: true);
    /// ⌥⌘1 / 2 / 3: mark Decision / To do / Important.
    public static readonly Shortcut MarkDecision = new('1', Ctrl: true, Alt: false, Shift: true);
    public static readonly Shortcut MarkTodo = new('2', Ctrl: true, Alt: false, Shift: true);
    public static readonly Shortcut MarkImportant = new('3', Ctrl: true, Alt: false, Shift: true);

    /// RegisterHotKey's MOD_ flags (with MOD_NOREPEAT).
    [JsonIgnore]
    public uint NativeModifiers => (Alt ? 0x1u : 0) | (Ctrl ? 0x2u : 0) | (Shift ? 0x4u : 0) | (Win ? 0x8u : 0) | 0x4000u;

    /// The keys as separate chips: ["Ctrl", "Alt", "M"].
    [JsonIgnore]
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add(Loc.T("Skift", "Shift"));
            if (Win) parts.Add("Win");
            parts.Add(new Hotkey(Key, Modifiers.None).Name);
            return parts;
        }
    }

    /// "Ctrl+Alt+M".
    public override string ToString() => string.Join("+", Parts);
}
