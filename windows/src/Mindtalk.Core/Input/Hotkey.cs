using System.Text.Json.Serialization;

namespace Mindtalk.Core.Input;

/// <summary>Sided modifier keys (left and right are distinct, as on the Mac).</summary>
[Flags]
public enum Modifiers
{
    None = 0,
    LeftCtrl = 1 << 0,
    RightCtrl = 1 << 1,
    LeftAlt = 1 << 2,
    RightAlt = 1 << 3,
    LeftShift = 1 << 4,
    RightShift = 1 << 5,
    LeftWin = 1 << 6,
    RightWin = 1 << 7,

    AnyCtrl = LeftCtrl | RightCtrl,
    AnyAlt = LeftAlt | RightAlt,
    AnyShift = LeftShift | RightShift,
    AnyWin = LeftWin | RightWin,
}

/// <summary>Windows virtual-key codes used by the key handling.</summary>
public static class VirtualKey
{
    public const int Escape = 0x1B, Space = 0x20, Tab = 0x09, Return = 0x0D, Back = 0x08, CapsLock = 0x14;
    public const int PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24;
    public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E;
    public const int F1 = 0x70, F24 = 0x87;
    public const int Shift = 0x10, Control = 0x11, Menu = 0x12;
    public const int LeftWin = 0x5B, RightWin = 0x5C, Apps = 0x5D;
    public const int LeftShift = 0xA0, RightShift = 0xA1, LeftCtrl = 0xA2, RightCtrl = 0xA3, LeftAlt = 0xA4, RightAlt = 0xA5;
    /// Unassigned; sent to stop a lone Alt or Win release from opening a menu or Start.
    public const int MenuMask = 0xE8;
}

/// <summary>
/// The dictation shortcut: one key, or a combination of modifiers held together.
/// Modifiers pass through to other apps untouched; a regular key is swallowed so it
/// doesn't also type — except with Ctrl/Alt/Win held, so shortcuts using it still work.
/// </summary>
public sealed record Hotkey(int Key, Modifiers Modifiers)
{
    [JsonIgnore] public bool IsModifier => Modifiers != Modifiers.None;

    /// Right Alt, like right ⌥ Option on the Mac.
    public static readonly Hotkey Default = new(VirtualKey.RightAlt, Modifiers.RightAlt);

    private static readonly (int Key, Modifiers Bit)[] ModifierOrder =
    [
        (VirtualKey.LeftCtrl, Modifiers.LeftCtrl), (VirtualKey.RightCtrl, Modifiers.RightCtrl),
        (VirtualKey.LeftAlt, Modifiers.LeftAlt), (VirtualKey.RightAlt, Modifiers.RightAlt),
        (VirtualKey.LeftShift, Modifiers.LeftShift), (VirtualKey.RightShift, Modifiers.RightShift),
        (VirtualKey.LeftWin, Modifiers.LeftWin), (VirtualKey.RightWin, Modifiers.RightWin),
    ];

    /// The modifier bit for a sided virtual key, or None if it isn't a modifier.
    public static Modifiers ModifierFor(int key) =>
        ModifierOrder.FirstOrDefault(m => m.Key == key).Bit;

    public static Hotkey? Combination(Modifiers mask)
    {
        var first = ModifierOrder.FirstOrDefault(m => (mask & m.Bit) != 0);
        return first.Bit == Modifiers.None ? null : new Hotkey(first.Key, mask);
    }

    [JsonIgnore]
    public IReadOnlyList<Hotkey> ModifierKeys =>
        ModifierOrder.Where(m => (Modifiers & m.Bit) != 0).Select(m => new Hotkey(m.Key, m.Bit)).ToList();

    public bool Contains(Modifiers bit) => (Modifiers & bit) != 0;

    /// Holding Alt or Win alone and letting go opens the app's menu bar or Start.
    [JsonIgnore] public bool NeedsMenuMask => (Modifiers & (Modifiers.AnyAlt | Modifiers.AnyWin)) != 0;

    [JsonIgnore]
    public string Name
    {
        get
        {
            var keys = ModifierKeys;
            if (keys.Count > 1) return string.Join(" + ", keys.Select(k => k.Name));
            return Key switch
            {
                VirtualKey.RightAlt => Loc.T("Höger Alt", "Right Alt"),
                VirtualKey.LeftAlt => Loc.T("Vänster Alt", "Left Alt"),
                VirtualKey.RightCtrl => Loc.T("Höger Ctrl", "Right Ctrl"),
                VirtualKey.LeftCtrl => Loc.T("Vänster Ctrl", "Left Ctrl"),
                VirtualKey.RightShift => Loc.T("Höger Skift", "Right Shift"),
                VirtualKey.LeftShift => Loc.T("Vänster Skift", "Left Shift"),
                VirtualKey.RightWin => Loc.T("Höger Windows", "Right Windows"),
                VirtualKey.LeftWin => Loc.T("Vänster Windows", "Left Windows"),
                VirtualKey.Space => Loc.T("Mellanslag", "Space"),
                VirtualKey.Return => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Back => "Backspace",
                VirtualKey.Delete => "Delete",
                VirtualKey.Insert => "Insert",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "Page Up",
                VirtualKey.PageDown => "Page Down",
                VirtualKey.CapsLock => "Caps Lock",
                VirtualKey.Apps => Loc.T("Menytangent", "Menu key"),
                VirtualKey.Left => "←",
                VirtualKey.Right => "→",
                VirtualKey.Up => "↑",
                VirtualKey.Down => "↓",
                >= VirtualKey.F1 and <= VirtualKey.F24 => $"F{Key - VirtualKey.F1 + 1}",
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
                _ => KeyNames?.Invoke(Key) ?? Loc.T($"Tangent {Key}", $"Key {Key}"),
            };
        }
    }

    /// Set by the platform layer: what a key types on the current keyboard layout ("§", "Å").
    public static Func<int, string?>? KeyNames { get; set; }

    public string Note =>
        ModifierKeys.Count > 1
            ? Loc.T("Håll in alla valda tangenter samtidigt. Kortkommandon fungerar som vanligt.",
                    "Hold all the chosen keys together. Shortcuts work as usual.")
            : Key switch
            {
                VirtualKey.RightAlt => Loc.T("AltGr-tecken som @ och { fungerar som vanligt.",
                                             "AltGr characters like @ and { work as usual."),
                VirtualKey.LeftAlt or VirtualKey.LeftCtrl or VirtualKey.RightCtrl
                    or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWin or VirtualKey.RightWin
                    => Loc.T("Kortkommandon fungerar som vanligt.", "Shortcuts work as usual."),
                _ => Loc.T("Skriver inget eget medan Mindtalk körs.", "Doesn't type anything itself while Mindtalk runs."),
            };
}
