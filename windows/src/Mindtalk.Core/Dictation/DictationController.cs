using Mindtalk.Core.Input;

namespace Mindtalk.Core.Dictation;

public enum Phase
{
    Idle,
    Recording,
    Transcribing,
    Failed,
    /// Nowhere to type it: it's in the clipboard instead.
    Copied,
    /// A thought was saved on the Thoughts page.
    Saved,
}

/// <param name="Confirmed">The press is clearly meant (held a moment, or locked) — only then
/// does the HUD show a recording: a quick tap or AltGr+2 → @ shouldn't flash it.</param>
/// <param name="ShowsLanguage">The language, shown for a moment in the HUD when it differs from last time.</param>
/// <param name="Thought">It's a thought: saved on the Thoughts page, not typed.</param>
public sealed record DictationState(Phase Phase, bool HandsFree, bool Confirmed, string? Message,
                                    bool ShowsLanguage = false, string? Engine = null, bool Thought = false)
{
    public bool Visible => Phase != Phase.Idle && (Phase != Phase.Recording || Confirmed);
}

/// <summary>How the speech model is doing, as far as a dictation is concerned.</summary>
public enum Readiness { Ready, Loading, Downloading, Missing, Failed }

/// <summary>
/// The dictation itself: key presses in, recording, transcription, cleanup and typing out.
/// Port of the Mac app's Dictation. Call everything on the UI thread.
///
/// Hold mode: a press starts the mic at once (so the first word isn't clipped), release
/// finishes. A quick tap is either the first half of a double-tap (→ locked, hands-free)
/// or, if no second tap follows, dropped silently. Audio stays in memory and is thrown
/// away right after transcription.
/// </summary>
public sealed class DictationController
{
    /// A press shorter than this is a tap, not a hold.
    private static readonly TimeSpan TapThreshold = TimeSpan.FromSeconds(0.3);
    /// How long after a tap a second one counts as a double-tap.
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromSeconds(0.35);
    /// A forgotten hands-free dictation finishes on its own.
    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(10);
    /// No audio at all for this long means the mic is gone (Bluetooth headsets take a
    /// second to switch profile, so not sooner).
    private static readonly TimeSpan DeadMic = TimeSpan.FromSeconds(2.5);
    /// Under a quarter second holds no words.
    private const int MinimumSamples = 4_000;
    /// The last second or so of mic levels — the HUD's waveform.
    public const int LevelCount = 24;

    private readonly IRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ITextOutput _output;
    private readonly IScheduler _scheduler;
    private readonly AppSettings _settings;

    private Phase _phase = Phase.Idle;
    private string? _message;
    private bool _handsFree, _confirmed, _keyIsDown, _awaitingSecondTap, _secondTapDown, _ignoreNextRelease;
    private bool _startCuePlayed, _showsLanguage;
    /// This recording (or the press that's starting one) is a thought, not a dictation.
    private bool _thought;
    private string? _lastLanguage;
    private DateTime? _pressedAt, _recordingStarted, _lastReport;
    private Action? _pendingProblem;
    private IDisposable? _doubleTapTimer, _watchdog, _resetTimer, _languageTimer;
    private volatile bool _isRecording, _spaceLocks;

    // Written on the audio thread, read by the HUD.
    private readonly object _levelGate = new();
    private readonly float[] _levels = new float[LevelCount];
    private long _lastLevelPush;
    private float _peakLevel;

    public DictationController(IRecorder recorder, ITranscriber transcriber, ITextOutput output,
                               IScheduler scheduler, AppSettings settings)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _output = output;
        _scheduler = scheduler;
        _settings = settings;
        _recorder.Level += PushLevel;
        // The mic went away (headset off, device switched): keep what was said.
        _recorder.Interrupted += () => _scheduler.After(TimeSpan.Zero, () => { if (_phase == Phase.Recording) Finish(); });
    }

    // ---- What the rest of the app plugs in -----------------------------------------

    /// The endpoint to record from (null = the system input).
    public Func<string?> SelectedMicrophone { get; set; } = () => null;
    public Func<bool> MicrophoneAllowed { get; set; } = () => true;
    /// The introduction is open: problems are said in the HUD rather than by opening the window.
    public Func<bool> IsOnboarding { get; set; } = () => false;
    /// The language you dictate in (its id), and how many are downloaded.
    public Func<string> Engine { get; set; } = () => "";
    public Func<int> InstalledModels { get; set; } = () => 1;
    public IFocusInspector? Focus { get; set; }
    public IMediaControl? Media { get; set; }
    public ICues? Cues { get; set; }
    public IPolisher? Polisher { get; set; }
    /// Your vocabulary: fixes the text and counts what it fixed.
    public Func<string, string> ApplyVocabulary { get; set; } = text => text;
    /// The vocabulary once more after the polish, without counting — your spellings win.
    public Func<string, string> ReapplyVocabulary { get; set; } = text => text;
    public Action<string, double>? RecordStats { get; set; }
    public Action<string>? AddRecent { get; set; }
    public Func<string?> LatestRecent { get; set; } = () => null;
    /// Ctrl or Alt still held (from Ctrl+Alt+V).
    public Func<bool> PasteModifiersHeld { get; set; } = () => false;
    public Action? Beep { get; set; }
    /// Where a thought goes (the Thoughts page). Without it, thought keys are ignored.
    public Action<string>? AddThought { get; set; }

    public DictationState State => new(_phase, _handsFree, _confirmed, _message, _showsLanguage, Engine(), _thought);
    public event Action<DictationState>? StateChanged;
    /// Something needs the main window (no speech model yet, no microphone access).
    public event Action? ShowWindowRequested;
    /// Raised while a held key is being watched, to catch a missed key-up.
    public event Action? ResyncRequested;

    /// Thread-safe, for the keyboard hook's thread.
    public bool IsRecording => _isRecording;
    /// Thread-safe: would Space while holding the key lock the dictation now?
    public bool SpaceLocks => _spaceLocks;
    /// Recording or writing: a language switch waits until this is over.
    public bool IsBusy => _phase is Phase.Recording or Phase.Transcribing;

    /// The last <paramref name="count"/> mic levels, oldest first. Any thread.
    public float[] Levels(int count = LevelCount)
    {
        lock (_levelGate) return _levels[^Math.Min(count, LevelCount)..];
    }

    private void PushLevel(float value)
    {
        // Late buffers after Stop mustn't refill the wave.
        if (!_isRecording) return;
        lock (_levelGate)
        {
            if (value > _peakLevel) _peakLevel = value;
            var now = Environment.TickCount64;
            if (now - _lastLevelPush < 50) return;
            _lastLevelPush = now;
            Array.Copy(_levels, 1, _levels, 0, LevelCount - 1);
            _levels[^1] = value;
        }
    }

    public void Handle(KeySignal signal)
    {
        switch (signal)
        {
            // The thought key and the dictation key are independent: one recording at a
            // time, and each key only speaks to its own.
            case KeySignal.Press press:
                if (press.Thought && AddThought is null) break;
                if (IsBusy && press.Thought != _thought) break;
                if (!IsBusy) _thought = press.Thought;
                KeyPressed();
                break;
            case KeySignal.Release release: if (release.Thought == _thought) KeyReleased(); break;
            case KeySignal.Chord chord: if (chord.Thought == _thought) Chord(); break;
            case KeySignal.SpaceLock: SpaceWhileHeld(); break;
            case KeySignal.Escape: if (_phase == Phase.Recording) Cancel(); break;
            case KeySignal.PasteLast: PasteLast(); break;
        }
    }

    // ---- Keys -----------------------------------------------------------------

    private void KeyPressed()
    {
        _keyIsDown = true;
        switch (_phase)
        {
            case Phase.Recording when _handsFree:
                _ignoreNextRelease = true;
                Finish();
                break;
            case Phase.Recording when _awaitingSecondTap:
                // Second tap of a double-tap — it locks on release, so a tap and then
                // AltGr+2 (a shortcut) still cancels instead.
                _doubleTapTimer?.Dispose();
                _awaitingSecondTap = false;
                _secondTapDown = true;
                break;
            case Phase.Idle or Phase.Failed or Phase.Copied or Phase.Saved:
                Begin();
                break;
        }
    }

    /// Say a Thought (a button, or Ctrl+Alt+T): starts a thought hands-free, or saves the
    /// one being said. A dictation in progress is left alone.
    public void StartThought()
    {
        if (AddThought is null) return;
        if (_phase == Phase.Recording)
        {
            if (_thought) Finish();
            return;
        }
        if (_phase == Phase.Transcribing) return;
        _thought = true;
        _keyIsDown = false;
        Begin(now: true);
        if (_phase == Phase.Recording) Lock();
    }

    private void KeyReleased()
    {
        _keyIsDown = false;
        if (_pendingProblem is { } problem)
        {
            _pendingProblem = null;
            if (_settings.Mode == DictationMode.Toggle) problem();   // a tap is a real press in this mode
            return;
        }
        if (_ignoreNextRelease) { _ignoreNextRelease = false; return; }
        if (_secondTapDown) { _secondTapDown = false; Lock(); return; }
        if (_phase != Phase.Recording || _handsFree || _pressedAt is not { } pressedAt) return;

        var wasTap = _scheduler.Now - pressedAt < TapThreshold;
        // A thought: hold to talk, or a quick tap keeps listening hands-free.
        if (_thought)
        {
            if (wasTap) Lock(); else Finish();
            return;
        }
        switch (_settings.Mode)
        {
            case DictationMode.Toggle:
                Lock();
                break;
            case DictationMode.Hold:
                if (wasTap) Cancel(); else Finish();
                break;
            case DictationMode.HoldOrDoubleTap:
                if (!wasTap) { Finish(); break; }
                // Keep listening a moment in case this is a double-tap.
                _awaitingSecondTap = true;
                _doubleTapTimer = _scheduler.After(DoubleTapWindow, () =>
                {
                    if (_awaitingSecondTap) Cancel();
                });
                break;
        }
    }

    /// Another key with the dictation key held: it was a shortcut (AltGr+2 → @), not dictation.
    private void Chord()
    {
        _pendingProblem = null;
        if (_phase == Phase.Recording && !_handsFree) Cancel();
    }

    /// Space while holding the key locks the dictation (like Wispr Flow).
    private void SpaceWhileHeld()
    {
        if (_phase != Phase.Recording || _handsFree || _settings.Mode == DictationMode.Hold) return;
        _ignoreNextRelease = true;
        Lock();
    }

    // ---- The HUD's buttons ----------------------------------------------------

    /// ✕: stop without typing anything.
    public void CancelFromHud() { if (_phase == Phase.Recording) Cancel(); }

    /// ✓: type what you said.
    public void FinishFromHud() { if (_phase == Phase.Recording) Finish(); }

    /// The PC is going to sleep: a recording can't go on.
    public void CancelForSleep() { if (_phase == Phase.Recording) Cancel(); }

    // ---- Recording ------------------------------------------------------------

    private void Lock()
    {
        _handsFree = true;
        ConfirmStart();
    }

    /// The press is a real dictation (held, or locked) — show the HUD, play the cue.
    private void ConfirmStart()
    {
        var engine = Engine();
        if (!_startCuePlayed && InstalledModels() > 1 && _lastLanguage is { } last && last != engine)
        {
            _showsLanguage = true;
            _languageTimer?.Dispose();
            _languageTimer = _scheduler.After(TimeSpan.FromSeconds(1.6), () => { _showsLanguage = false; Publish(); });
        }
        if (!_startCuePlayed)
        {
            _lastLanguage = engine;
            // Only now — a quick tap or a shortcut like AltGr+2 → @ never touches your music.
            Media?.Begin();
        }
        _confirmed = true;
        Publish();
        if (!_startCuePlayed) { _startCuePlayed = true; Cues?.Start(); }
    }

    /// <param name="now">Asked for with a click, not a key: a problem is said at once.</param>
    private void Begin(bool now = false)
    {
        _resetTimer?.Dispose();
        void Problem(Action problem)
        {
            if (now) problem(); else Report(problem);
        }
        switch (_transcriber.Readiness)
        {
            case Readiness.Ready: break;
            case Readiness.Loading:
                Problem(() => Fail(Loc.T("Språkmodellen startar – ett ögonblick…", "The speech model is starting – one moment…")));
                return;
            case Readiness.Downloading:
                Problem(() => Fail(Loc.T("Språkmodellen laddas fortfarande ned…", "The speech model is still downloading…")));
                return;
            default:
                Problem(() =>
                {
                    if (IsOnboarding()) Fail(Loc.T($"{_transcriber.ModelTitle} är inte nedladdad än.", $"{_transcriber.ModelTitle} isn’t downloaded yet."));
                    else ShowWindowRequested?.Invoke();
                });
                return;
        }
        if (!MicrophoneAllowed())
        {
            Problem(() =>
            {
                if (IsOnboarding()) Fail(Loc.T("Mindtalk behöver tillgång till mikrofonen.", "Mindtalk needs access to the microphone."));
                else ShowWindowRequested?.Invoke();
            });
            return;
        }
        var chosen = SelectedMicrophone();
        try
        {
            try
            {
                _recorder.Start(chosen);
            }
            catch when (chosen is not null)
            {
                // The chosen mic won't open (just unplugged, reconnecting) — the system
                // input will do rather than nothing.
                _recorder.Start(null);
            }
        }
        catch (Exception ex)
        {
            Problem(() => Fail(MicrophoneProblem(ex)));
            return;
        }

        lock (_levelGate)
        {
            Array.Clear(_levels);
            _peakLevel = 0;
        }
        _phase = Phase.Recording;
        _message = null;
        _handsFree = false;
        _confirmed = false;
        _pressedAt = _recordingStarted = _scheduler.Now;
        StartWatchdog();
        if (_settings.AiPolish) Polisher?.Prepare();
        Publish();
        // Show the HUD once the press is clearly a hold (or it got locked) — a quick tap
        // or a shortcut like AltGr+2 → @ shouldn't flash it.
        _scheduler.After(TimeSpan.FromSeconds(0.2), () =>
        {
            if (_phase == Phase.Recording && !_confirmed
                && (_keyIsDown || _handsFree || _settings.Mode == DictationMode.Toggle)) ConfirmStart();
        });
    }

    public static string MicrophoneProblem(Exception ex) =>
        ex is UnauthorizedAccessException || (uint)ex.HResult == 0x80070005
            ? Loc.T("Mindtalk behöver tillgång till mikrofonen.", "Mindtalk needs access to the microphone.")
            : Loc.T("Hittar ingen fungerande mikrofon.", "Can’t find a working microphone.");

    /// Reports a problem only if the press is held a moment (or in toggle mode, on
    /// release) — a shortcut like AltGr+2 → @ or a stray tap stays silent.
    private void Report(Action problem)
    {
        // Two presses in quick succession (a double-tap) are clearly meant too.
        if (_lastReport is { } last && _scheduler.Now - last < TimeSpan.FromSeconds(0.6))
        {
            _lastReport = null;
            _pendingProblem = null;
            problem();
            return;
        }
        _lastReport = _scheduler.Now;
        _pendingProblem = problem;
        _scheduler.After(TimeSpan.FromSeconds(0.25), () =>
        {
            if (!_keyIsDown || _pendingProblem is not { } pending) return;
            _pendingProblem = null;
            pending();
        });
    }

    /// While recording: catch a missed key release, and end a forgotten dictation.
    private void StartWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = _scheduler.After(TimeSpan.FromSeconds(0.5), () =>
        {
            if (_phase != Phase.Recording) return;
            if (_recordingStarted is { } start && _scheduler.Now - start > MaxRecording) { Finish(); return; }
            // A mic that went quiet without saying so (unplugged, Bluetooth dropped):
            // finish with what was heard instead of recording nothing.
            if (_recorder.SilentFor > DeadMic) { Finish(); return; }
            if (_keyIsDown && !_handsFree && _settings.Mode != DictationMode.Toggle) ResyncRequested?.Invoke();
            if (_phase == Phase.Recording) StartWatchdog();
        });
    }

    private async void Finish()
    {
        var samples = _recorder.Stop();
        var lasted = _recordingStarted is { } started ? _scheduler.Now - started : TimeSpan.Zero;
        _watchdog?.Dispose();
        Media?.End();
        var handsFree = _handsFree;
        _handsFree = false;
        _awaitingSecondTap = false;
        _secondTapDown = false;
        _doubleTapTimer?.Dispose();
        _pressedAt = null;

        // Held a while and not a sound: the mic never delivered — say so.
        if (samples.Length == 0 && lasted > TimeSpan.FromSeconds(1))
        {
            Fail(Loc.T("Hittar ingen fungerande mikrofon.", "Can’t find a working microphone."));
            return;
        }
        if (samples.Length <= MinimumSamples) { Reset(); return; }

        _phase = Phase.Transcribing;
        Publish();
        float heard;
        lock (_levelGate) heard = _peakLevel;
        if (_thought)
        {
            await FinishThought(samples, heard, handsFree);
            return;
        }
        // Where you are when you stop talking is where the text goes — or nowhere, if
        // you switch app while it's being written.
        var target = _output.CaptureTarget();
        try
        {
            var raw = await _transcriber.TranscribeAsync(samples);
            // A password field: typed, never kept or polished.
            var secret = Focus is not null && await Focus.IsSecureFieldFocusedAsync();
            // Rules, then your vocabulary, then (if on) the on-device polish — with the
            // vocabulary once more so your spellings win.
            var cleaned = TextCleanup.Apply(raw, _settings.RemoveFillers, _settings.VoiceCommands);
            var text = ApplyVocabulary(cleaned);
            if (_settings.AiPolish && !secret && Polisher is not null && await Polisher.PolishAsync(text) is { } polished)
                text = ReapplyVocabulary(polished);
            if (text.Length == 0)
            {
                if (heard < 0.03f) Fail(Loc.T("Hörde inget – kontrollera mikrofonen.", "Didn’t hear anything – check the microphone."));
                else Reset();
                return;
            }
            if (!secret)
            {
                RecordStats?.Invoke(text, samples.Length / 16_000.0);
                AddRecent?.Invoke(text);
            }
            // You switched app while it was being written: don't type into the wrong
            // one. It's in Recent, one Ctrl+Alt+V away.
            if (!_output.IsStillInFront(target))
            {
                Fail(secret ? Loc.T("Du bytte app – texten skrevs inte in.", "You switched apps – the text wasn’t typed.")
                            : Loc.T("Du bytte app – tryck Ctrl+Alt+V för att klistra in texten.",
                                    "You switched apps – press Ctrl+Alt+V to paste the text."));
                return;
            }
            if (!secret && Focus is not null && await Focus.FocusedFieldAsync(target) == FieldKind.None)
            {
                Copied(text);
                return;
            }
            // A spoken line break at the end is the separator; otherwise a space. Never a
            // password in the clipboard, whatever the setting.
            await _output.InsertAsync(text.EndsWith('\n') ? text : text + " ", target,
                                      _settings.KeepInClipboard && !secret ? text : null);
            Cues?.Done();
            Reset();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// A thought: cleaned up and with your vocabulary like a dictation, then kept on the
    /// Thoughts page — nothing is typed, counted or put in the history. The sound is gone
    /// once the text is written.
    private async Task FinishThought(float[] samples, float heard, bool handsFree)
    {
        try
        {
            var raw = await _transcriber.TranscribeAsync(samples);
            var cleaned = TextCleanup.Apply(raw, _settings.RemoveFillers, _settings.VoiceCommands);
            var text = ApplyVocabulary(cleaned);
            if (_settings.AiPolish && Polisher is not null && await Polisher.PolishAsync(text) is { } polished)
                text = ReapplyVocabulary(polished);
            text = text.Trim();
            if (text.Length == 0)
            {
                // Alt held a moment for something else (Alt+click) says nothing — only a
                // thought said into a silent mic is worth a word.
                if (heard < 0.03f && handsFree) Fail(Loc.T("Hörde inget – kontrollera mikrofonen.", "Didn’t hear anything – check the microphone."));
                else Reset();
                return;
            }
            AddThought?.Invoke(text);
            Cues?.Done();
            _phase = Phase.Saved;
            _message = null;
            Publish();
            _resetTimer?.Dispose();
            _resetTimer = _scheduler.After(TimeSpan.FromSeconds(1.8), () =>
            {
                if (_phase == Phase.Saved) Reset();
            });
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Cancel()
    {
        _recorder.Stop();
        _ignoreNextRelease = false;
        Reset();
    }

    private void Reset()
    {
        Media?.End();
        _watchdog?.Dispose();
        _doubleTapTimer?.Dispose();
        _resetTimer?.Dispose();
        _phase = Phase.Idle;
        _message = null;
        _handsFree = false;
        _confirmed = false;
        _startCuePlayed = false;
        _awaitingSecondTap = false;
        _secondTapDown = false;
        _pressedAt = null;
        _recordingStarted = null;
        lock (_levelGate) Array.Clear(_levels);
        Publish();
    }

    private void Fail(string message)
    {
        _phase = Phase.Failed;
        _message = message;
        Publish();
        _resetTimer?.Dispose();
        _resetTimer = _scheduler.After(TimeSpan.FromSeconds(3), () =>
        {
            if (_phase == Phase.Failed) Reset();
        });
    }

    /// No text field where you are (the desktop, a web page): the text goes to the
    /// clipboard, and the HUD says so.
    private void Copied(string text)
    {
        _output.Copy(text);
        Cues?.Done();
        _phase = Phase.Copied;
        _message = null;
        Publish();
        _resetTimer?.Dispose();
        _resetTimer = _scheduler.After(TimeSpan.FromSeconds(2.5), () =>
        {
            if (_phase == Phase.Copied) Reset();
        });
    }

    /// Ctrl+Alt+V: the last dictation, typed again where the cursor is now — for when it
    /// landed in the wrong place. Waits for Ctrl and Alt to be let go, so the paste isn't
    /// read as Ctrl+Alt+V again.
    public async void PasteLast()
    {
        if (_phase is not (Phase.Idle or Phase.Recording or Phase.Copied or Phase.Failed or Phase.Saved) || LatestRecent() is not { } last)
        {
            Beep?.Invoke();
            return;
        }
        if (_phase is Phase.Failed or Phase.Copied or Phase.Saved) Reset();
        if (_phase == Phase.Recording) Cancel();
        for (var i = 0; i < 40 && PasteModifiersHeld(); i++) await Task.Delay(25);
        var target = _output.CaptureTarget();
        if (Focus is not null && await Focus.FocusedFieldAsync(target) == FieldKind.None)
        {
            Copied(last);
            return;
        }
        await _output.InsertAsync(last + " ", target, _settings.KeepInClipboard ? last : null);
    }

    private void Publish()
    {
        _isRecording = _phase == Phase.Recording;
        _spaceLocks = _isRecording && (_handsFree || _settings.Mode != DictationMode.Hold);
        StateChanged?.Invoke(State);
    }
}
