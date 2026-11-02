using Mindtalk.Core.Input;

namespace Mindtalk.Core.Tests;

[Collection("Loc")]
public sealed class ShortcutRulesTests
{
    private static readonly Hotkey RightAlt = Hotkey.Default;
    private static readonly Hotkey LeftAlt = Hotkey.DefaultThought;

    [Fact]
    public void Thought_keys_must_be_modifiers()
    {
        using var _ = Language.English();
        Assert.NotNull(ThoughtKeyRules.Problem(new Hotkey('A', Modifiers.None), RightAlt));
        Assert.Null(ThoughtKeyRules.Problem(LeftAlt, RightAlt));
        Assert.Null(ThoughtKeyRules.Problem(Hotkey.Combination(Modifiers.LeftAlt | Modifiers.LeftCtrl)!, RightAlt));
    }

    [Fact]
    public void Thought_keys_cannot_share_a_key_with_dictation_left_and_right_distinct()
    {
        using var _ = Language.English();
        var dictation = Hotkey.Combination(Modifiers.LeftAlt | Modifiers.RightCtrl)!;
        Assert.Contains("left Alt", ThoughtKeyRules.Problem(LeftAlt, dictation));
        // Right Alt isn't left Alt.
        Assert.Null(ThoughtKeyRules.Problem(RightAlt, dictation));
        // A dictation key that's a regular key shares nothing.
        Assert.Null(ThoughtKeyRules.Problem(LeftAlt, new Hotkey(VirtualKey.F1 + 7, Modifiers.None)));
    }

    [Fact]
    public void The_default_thought_key_steps_aside_for_dictation()
    {
        Assert.Equal(LeftAlt, ThoughtKeyRules.Default(RightAlt));
        Assert.Equal(Modifiers.RightCtrl, ThoughtKeyRules.Default(LeftAlt).Modifiers);
    }

    [Fact]
    public void A_shortcut_needs_ctrl_alt_or_windows_unless_it_is_a_function_key()
    {
        Assert.Equal(new Shortcut('M', true, true, false), ShortcutRules.FromPress('M', true, true, false, false));
        Assert.Null(ShortcutRules.FromPress('M', false, false, true, false));
        Assert.Null(ShortcutRules.FromPress(VirtualKey.LeftCtrl, true, false, false, false));
        Assert.Null(ShortcutRules.FromPress(VirtualKey.Escape, true, false, false, false));
        Assert.NotNull(ShortcutRules.FromPress(VirtualKey.F1 + 8, false, false, false, false));
    }

    [Fact]
    public void Sizes_read_naturally()
    {
        using var _ = Language.Swedish();
        Assert.Equal("0 MB", ShortcutRules.Size(0));
        Assert.Equal("1 MB", ShortcutRules.Size(1200));
        Assert.Equal("400 MB", ShortcutRules.Size(400_000_000));
        Assert.Equal("6,9 GB", ShortcutRules.Size(6_900_000_000));
    }
}
