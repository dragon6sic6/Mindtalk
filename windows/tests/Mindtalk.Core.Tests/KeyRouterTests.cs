using Mindtalk.Core.Input;
using Xunit;

namespace Mindtalk.Core.Tests;

public class KeyRouterTests
{
    private readonly List<KeySignal> _signals = [];

    private KeyRouter Router(Hotkey hotkey) => new(hotkey, _signals.Add);

    private static KeyEvent Down(int key) => new(key, true);
    private static KeyEvent Up(int key) => new(key, false);

    private IEnumerable<Type> Kinds => _signals.Select(s => s.GetType());

    [Fact]
    public void RightAltAloneStartsAndStops()
    {
        var router = Router(Hotkey.Default);
        Assert.False(router.Handle(Down(VirtualKey.RightAlt)));   // modifiers pass through
        Assert.True(router.IsDown);
        Assert.False(router.Handle(Up(VirtualKey.RightAlt)));
        Assert.Equal([typeof(KeySignal.MaskMenu), typeof(KeySignal.Press), typeof(KeySignal.Release)], Kinds);
    }

    [Fact]
    public void AutoRepeatOfTheHeldModifierIsIgnored()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.RightAlt));
        router.Handle(Down(VirtualKey.RightAlt));
        router.Handle(Down(VirtualKey.RightAlt));
        Assert.Single(_signals, s => s is KeySignal.Press);
        Assert.DoesNotContain(_signals, s => s is KeySignal.Chord);
    }

    [Fact]
    public void AltGrCharacterIsAChordAndPassesThrough()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.RightAlt));
        Assert.False(router.Handle(Down('2')));                    // AltGr+2 → @ still types
        Assert.Contains(_signals, s => s is KeySignal.Chord);
    }

    [Fact]
    public void CtrlAltVPastesTheLastDictationAndIsSwallowed()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.LeftCtrl));
        router.Handle(Down(VirtualKey.LeftAlt));
        Assert.True(router.Handle(Down(VirtualKey.V)));
        Assert.True(router.Handle(Up(VirtualKey.V)));
        Assert.Contains(_signals, s => s is KeySignal.PasteLast);
    }

    [Fact]
    public void CtrlAndRightAltVCancelsTheDictationItStarted()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.LeftCtrl));
        router.Handle(Down(VirtualKey.RightAlt));
        Assert.True(router.Handle(Down(VirtualKey.V)));
        var paste = _signals.FindIndex(s => s is KeySignal.PasteLast);
        Assert.True(paste > 0 && _signals[paste - 1] is KeySignal.Chord);
    }

    [Fact]
    public void AltGrVIsNotPasteLast()
    {
        // The hook drops the Ctrl that Windows fakes for AltGr, so only Right Alt is held.
        var router = Router(new Hotkey(VirtualKey.F1 + 7, Modifiers.None));
        router.Handle(Down(VirtualKey.RightAlt));
        Assert.False(router.Handle(Down(VirtualKey.V)));
        Assert.DoesNotContain(_signals, s => s is KeySignal.PasteLast);
    }

    [Fact]
    public void CombinationNeedsAllKeysAndEndsWhenAnyIsReleased()
    {
        var router = Router(new Hotkey(VirtualKey.RightCtrl, Modifiers.RightCtrl | Modifiers.RightAlt));
        router.Handle(Down(VirtualKey.RightCtrl));
        Assert.False(router.IsDown);
        router.Handle(Down(VirtualKey.RightAlt));
        Assert.True(router.IsDown);
        router.Handle(Up(VirtualKey.RightCtrl));
        Assert.False(router.IsDown);
    }

    [Fact]
    public void LeftSideDoesNotSatisfyTheRightSide()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.LeftAlt));
        Assert.False(router.IsDown);
    }

    [Fact]
    public void ShortcutDuringPartialCombinationBlocksIt()
    {
        var router = Router(new Hotkey(VirtualKey.LeftCtrl, Modifiers.LeftCtrl | Modifiers.LeftShift));
        router.Handle(Down(VirtualKey.LeftCtrl));
        router.Handle(Down('C'));                                 // Ctrl+C
        router.Handle(Up('C'));
        router.Handle(Down(VirtualKey.LeftShift));
        Assert.False(router.IsDown);
        // Let go of everything and it works again.
        router.Handle(Up(VirtualKey.LeftShift));
        router.Handle(Up(VirtualKey.LeftCtrl));
        router.Handle(Down(VirtualKey.LeftCtrl));
        router.Handle(Down(VirtualKey.LeftShift));
        Assert.True(router.IsDown);
    }

    [Fact]
    public void RegularKeyIsSwallowedButNotAsAShortcut()
    {
        var router = Router(new Hotkey(VirtualKey.F1 + 7, Modifiers.None));   // F8
        Assert.True(router.Handle(Down(VirtualKey.F1 + 7)));
        Assert.True(router.IsDown);
        Assert.True(router.Handle(Up(VirtualKey.F1 + 7)));
        Assert.False(router.IsDown);

        router.Handle(Down(VirtualKey.LeftCtrl));
        Assert.False(router.Handle(Down(VirtualKey.F1 + 7)));     // Ctrl+F8 belongs to the app
        Assert.False(router.Handle(Up(VirtualKey.F1 + 7)));
        Assert.False(router.IsDown);
    }

    [Fact]
    public void SpaceLocksOnlyWhenAllowed()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.RightAlt));
        router.SpaceLocks = () => true;
        Assert.True(router.Handle(Down(VirtualKey.Space)));
        Assert.True(router.Handle(Up(VirtualKey.Space)));         // its key-up goes too
        Assert.Contains(_signals, s => s is KeySignal.SpaceLock);

        _signals.Clear();
        router.SpaceLocks = () => false;
        Assert.False(router.Handle(Down(VirtualKey.Space)));
        Assert.Contains(_signals, s => s is KeySignal.Chord);     // hold mode: Space cancels
    }

    [Fact]
    public void EscapeIsSwallowedOnlyWhileRecording()
    {
        var router = Router(Hotkey.Default);
        Assert.False(router.Handle(Down(VirtualKey.Escape)));
        router.Handle(Up(VirtualKey.Escape));
        router.IsRecording = () => true;
        Assert.True(router.Handle(Down(VirtualKey.Escape)));
        Assert.True(router.Handle(Up(VirtualKey.Escape)));
        Assert.Contains(_signals, s => s is KeySignal.Escape);
    }

    [Fact]
    public void CapturesACombinationOnRelease()
    {
        var router = Router(Hotkey.Default);
        router.BeginCapture();
        router.Handle(Down(VirtualKey.RightAlt));
        router.Handle(Down(VirtualKey.RightCtrl));
        router.Handle(Up(VirtualKey.RightAlt));
        Assert.Empty(_signals);
        router.Handle(Up(VirtualKey.RightCtrl));
        var captured = Assert.IsType<KeySignal.Captured>(Assert.Single(_signals));
        Assert.Equal(Modifiers.RightCtrl | Modifiers.RightAlt, captured.Hotkey.Modifiers);
        Assert.False(router.IsDown);                               // capturing never starts a dictation
    }

    [Fact]
    public void CapturesARegularKeyOnPressAndEscKeepsTheOld()
    {
        var router = Router(Hotkey.Default);
        router.BeginCapture();
        Assert.True(router.Handle(Down(VirtualKey.F1 + 4)));
        Assert.Equal(new Hotkey(VirtualKey.F1 + 4, Modifiers.None), Assert.IsType<KeySignal.Captured>(_signals[0]).Hotkey);

        _signals.Clear();
        router.BeginCapture();
        router.Handle(Down(VirtualKey.Escape));
        Assert.Equal(Hotkey.Default, Assert.IsType<KeySignal.Captured>(_signals[0]).Hotkey);
    }

    [Fact]
    public void ResyncReleasesAMissedKeyUp()
    {
        var router = Router(Hotkey.Default);
        router.Handle(Down(VirtualKey.RightAlt));
        router.Resync(_ => false);
        Assert.False(router.IsDown);
        Assert.IsType<KeySignal.Release>(_signals[^1]);
    }
}
