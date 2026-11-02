using System.Numerics;

namespace Mindtalk.Core.Input;

/// <summary>A key going down or up, as the platform's keyboard hook saw it (sided virtual keys).</summary>
public readonly record struct KeyEvent(int Key, bool IsDown);

public abstract record KeySignal
{
    /// The dictation key went down / came up — or, with <c>Thought</c>, the thought key.
    public sealed record Press(bool Thought = false) : KeySignal;
    public sealed record Release(bool Thought = false) : KeySignal;
    /// Another key was pressed while the dictation (or thought) key is down (AltGr+2 for @),
    /// or, for the thought key, the mouse was clicked: a shortcut, not a recording.
    public sealed record Chord(bool Thought = false) : KeySignal;
    /// Space while the dictation key is held: lock hands-free.
    public sealed record SpaceLock : KeySignal;
    /// Escape while recording: cancel.
    public sealed record Escape : KeySignal;
    /// A new shortcut was picked (or Esc kept the old one).
    public sealed record Captured(Hotkey Hotkey) : KeySignal;
    /// Esc while picking the thought key: nothing changes.
    public sealed record CaptureCancelled : KeySignal;
    /// The dictation key includes Alt or Win: send a harmless key so letting go of it
    /// alone doesn't open the app's menu bar or Start.
    public sealed record MaskMenu : KeySignal;
    /// Ctrl+Alt+V: type the last dictation again.
    public sealed record PasteLast : KeySignal;
}

/// <summary>
/// Decides what each key event means for dictation and whether other apps see it.
/// Port of the Mac app's KeyListener routing, without the event tap — so it can be
/// tested with synthetic events. Signals go to <see cref="Post"/>, which must only queue
/// them: the hook has to hand the key back to Windows quickly.
/// </summary>
public sealed class KeyRouter
{
    private readonly object _gate = new();
    private Hotkey _hotkey;
    private Modifiers _held;
    private readonly HashSet<int> _down = [];
    private readonly HashSet<int> _swallowed = [];
    private bool _isDown;
    /// A shortcut typed during a partial modifier combination must not turn into
    /// dictation when the rest of the combination arrives a moment later.
    private bool _shortcutUsed;
    private bool _capturing, _capturingThought;
    private Modifiers _captured, _heldOver;
    /// Say a thought: a second, independent hold key (modifiers only, none shared with dictation).
    private Hotkey? _thought;
    private bool _thoughtDown, _thoughtShortcutUsed;

    public KeyRouter(Hotkey hotkey, Action<KeySignal> post)
    {
        _hotkey = hotkey;
        Post = post;
    }

    public Action<KeySignal> Post { get; }
    /// Is a dictation recording? (Esc is swallowed only then.) Called on the hook's thread.
    public Func<bool> IsRecording { get; set; } = () => false;
    /// Would Space lock the dictation now? Called on the hook's thread.
    public Func<bool> SpaceLocks { get; set; } = () => false;

    public Hotkey Hotkey
    {
        get { lock (_gate) return _hotkey; }
        set { lock (_gate) { _hotkey = value; _isDown = false; _shortcutUsed = false; } }
    }

    public bool IsDown { get { lock (_gate) return _isDown; } }

    /// The thought key (null: no hold key for thoughts). Only modifiers count, and it must
    /// not share a key with <see cref="Hotkey"/> (see <see cref="ThoughtKeys"/>).
    public Hotkey? ThoughtHotkey
    {
        get { lock (_gate) return _thought; }
        set
        {
            lock (_gate)
            {
                _thought = value is { IsModifier: true } ? value : null;
                _thoughtDown = false;
                _thoughtShortcutUsed = false;
            }
        }
    }

    public bool ThoughtIsDown { get { lock (_gate) return _thoughtDown; } }

    /// Start picking a new shortcut: regular keys are taken on press, modifier
    /// combinations once all of them are let go. For the thought key (<paramref name="thought"/>)
    /// only modifiers count, and Esc posts <see cref="KeySignal.CaptureCancelled"/>.
    public void BeginCapture(bool thought = false)
    {
        lock (_gate)
        {
            _capturing = true;
            _capturingThought = thought;
            _captured = Modifiers.None;
            // A modifier already down when picking starts isn't part of the choice.
            _heldOver = _held;
        }
    }

    public void CancelCapture() { lock (_gate) _capturing = false; }

    /// <returns>true to swallow the event (no other app sees it).</returns>
    public bool Handle(KeyEvent e)
    {
        lock (_gate)
        {
            var bit = Hotkey.ModifierFor(e.Key);
            var isModifier = bit != Modifiers.None;
            var repeat = e.IsDown && _down.Contains(e.Key);
            if (e.IsDown) { _down.Add(e.Key); _held |= bit; }
            else { _down.Remove(e.Key); _held &= ~bit; }

            // A key-up goes wherever its key-down went.
            if (!e.IsDown && !isModifier && _swallowed.Remove(e.Key))
            {
                if (!_capturing && !_hotkey.IsModifier && e.Key == _hotkey.Key) SetDown(false);
                return true;
            }

            var swallow = Route(e, bit, isModifier, repeat);
            if (swallow && e.IsDown && !isModifier) _swallowed.Add(e.Key);
            return swallow;
        }
    }

    private bool Route(KeyEvent e, Modifiers bit, bool isModifier, bool repeat)
    {
        if (_capturing)
        {
            if (isModifier)
            {
                if (repeat) return false;
                _heldOver &= _held;
                var held = _held & ~_heldOver;
                // Keep the largest set held at once, not a union of presses.
                if (BitOperations.PopCount((uint)held) > BitOperations.PopCount((uint)_captured)) _captured = held;
                if (held == Modifiers.None && Hotkey.Combination(_captured) is { } combination)
                {
                    _capturing = false;
                    Post(new KeySignal.Captured(combination));
                }
                return false;
            }
            if (!e.IsDown) return false;
            if (e.Key != VirtualKey.Escape && (_captured != Modifiers.None || _capturingThought)) return true;
            _capturing = false;
            if (_capturingThought)
            {
                Post(new KeySignal.CaptureCancelled());
                return true;
            }
            // Esc keeps the old one.
            Post(new KeySignal.Captured(e.Key == VirtualKey.Escape ? _hotkey : new Hotkey(e.Key, Modifiers.None)));
            return true;
        }

        // The thought key never swallows anything: it only listens.
        if (_thought is { } thought) RouteThought(e, bit, isModifier, repeat, thought);

        // Ctrl+Alt+V — "paste the last dictation again". (AltGr+V isn't: the hook drops the
        // Ctrl that Windows fakes for AltGr.) Holding Right Alt for it also started a
        // dictation; drop that first.
        if (e.IsDown && e.Key == VirtualKey.V && !repeat
            && (_held & Modifiers.AnyCtrl) != 0 && (_held & Modifiers.AnyAlt) != 0 && (_held & Modifiers.AnyWin) == 0)
        {
            if (_hotkey.ModifierKeys.Count > 1 && (_held & _hotkey.Modifiers) != 0) _shortcutUsed = true;
            if (_isDown) Post(new KeySignal.Chord());
            Post(new KeySignal.PasteLast());
            return true;
        }

        if (_isDown && e.IsDown && e.Key == VirtualKey.Space && _hotkey.Key != VirtualKey.Space && SpaceLocks())
        {
            Post(new KeySignal.SpaceLock());
            return true;
        }

        if (_hotkey.IsModifier)
        {
            if (isModifier && _hotkey.Contains(bit))
            {
                if (repeat) return false;
                var members = _held & _hotkey.Modifiers;
                if (members == Modifiers.None) _shortcutUsed = false;
                if (members != _hotkey.Modifiers) SetDown(false);
                else if (!_shortcutUsed) SetDown(true);
                return false;
            }
            var modifierPressed = isModifier && e.IsDown && !repeat;
            var typed = e.IsDown && !isModifier;
            if (_hotkey.ModifierKeys.Count > 1 && (_held & _hotkey.Modifiers) != 0 && (typed || modifierPressed))
                _shortcutUsed = true;
            if (_isDown && (typed || modifierPressed || (_hotkey.ModifierKeys.Count == 1 && isModifier && !repeat)))
                Post(new KeySignal.Chord());
        }
        else if (e.Key == _hotkey.Key)
        {
            // Its key-down went to the app (a shortcut), so the key-up does too.
            if (!e.IsDown) { SetDown(false); return false; }
            // Ctrl/Alt/Win + the key: a shortcut, not ours.
            if (e.IsDown && !_isDown && (_held & (Modifiers.AnyCtrl | Modifiers.AnyAlt | Modifiers.AnyWin)) != 0)
                return false;
            if (!repeat) SetDown(e.IsDown);
            return true;
        }

        if (e.IsDown && e.Key == VirtualKey.Escape && !repeat && IsRecording())
        {
            Post(new KeySignal.Escape());
            return true;
        }
        return false;
    }

    /// The thought key, as a modifier dictation key: down once every one of its modifiers is
    /// held, up when any is let go; anything typed (or another modifier) while it's down is a
    /// shortcut, not a thought.
    private void RouteThought(KeyEvent e, Modifiers bit, bool isModifier, bool repeat, Hotkey thought)
    {
        if (isModifier && thought.Contains(bit))
        {
            if (repeat) return;
            var members = _held & thought.Modifiers;
            if (members == Modifiers.None) _thoughtShortcutUsed = false;
            if (members != thought.Modifiers) SetThoughtDown(false);
            else if (!_thoughtShortcutUsed) SetThoughtDown(true);
            return;
        }
        var modifierPressed = isModifier && e.IsDown && !repeat;
        var typed = e.IsDown && !isModifier;
        var single = thought.ModifierKeys.Count == 1;
        if (!single && (_held & thought.Modifiers) != 0 && (typed || modifierPressed)) _thoughtShortcutUsed = true;
        if (_thoughtDown && (typed || modifierPressed || (single && isModifier && !repeat)))
            Post(new KeySignal.Chord(Thought: true));
    }

    private void SetThoughtDown(bool down)
    {
        if (down == _thoughtDown) return;
        _thoughtDown = down;
        if (down && _thought!.NeedsMenuMask) Post(new KeySignal.MaskMenu());
        Post(down ? new KeySignal.Press(Thought: true) : new KeySignal.Release(Thought: true));
    }

    /// A mouse button went down: Alt+click (any click with the thought key held) is a click,
    /// not a thought.
    public void MouseDown()
    {
        lock (_gate)
        {
            if (!_thoughtDown) return;
            Post(new KeySignal.Chord(Thought: true));
        }
    }

    private void SetDown(bool down)
    {
        if (down == _isDown) return;
        _isDown = down;
        if (down && _hotkey.NeedsMenuMask) Post(new KeySignal.MaskMenu());
        Post(down ? new KeySignal.Press() : new KeySignal.Release());
    }

    /// <summary>
    /// Brings the state in line with the hardware, after a key-up may have been missed
    /// (the hook timed out, the secure desktop took the keyboard, sleep).
    /// </summary>
    public void Resync(Func<int, bool> isPhysicallyDown)
    {
        lock (_gate)
        {
            foreach (var key in _down.Where(k => !isPhysicallyDown(k)).ToList())
            {
                _down.Remove(key);
                _swallowed.Remove(key);
                _held &= ~Hotkey.ModifierFor(key);
            }
            var stillDown = _hotkey.IsModifier
                ? _hotkey.ModifierKeys.All(k => isPhysicallyDown(k.Key))
                : isPhysicallyDown(_hotkey.Key);
            if (_isDown && !stillDown) SetDown(false);
            if ((_held & _hotkey.Modifiers) == 0) _shortcutUsed = false;
            if (_thought is { } thought)
            {
                if (_thoughtDown && !thought.ModifierKeys.All(k => isPhysicallyDown(k.Key))) SetThoughtDown(false);
                if ((_held & thought.Modifiers) == 0) _thoughtShortcutUsed = false;
            }
        }
    }

    /// Forget a half-finished press (e.g. after changing the key).
    public void Reset()
    {
        lock (_gate)
        {
            _isDown = false;
            _shortcutUsed = false;
            _thoughtDown = false;
            _thoughtShortcutUsed = false;
            _captured = _heldOver = Modifiers.None;
        }
    }
}
