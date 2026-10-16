using Mindtalk.Core.Input;

namespace Mindtalk.Core.Dictation;

public enum Phase { Idle, Recording, Transcribing, Failed }

public sealed record DictationState(Phase Phase, bool HandsFree, bool Confirmed, string? Message)
{
    /// The HUD shows a recording only once the press is clearly meant (held, or locked) —
    /// a quick tap or AltGr+2 → @ shouldn't flash it.
    public bool Visible => Phase != Phase.Idle && (Phase != Phase.Recording || Confirmed);
}

/// <summary>
/// The dictation itself: key presses in, recording, transcription, cleanup and typing out.
/// Port of the Mac app's Dictation key handling. Call everything on the UI thread.
///
/// Hold mode: a press starts the mic at once (so the first word isn't clipped), release
/// finishes. A quick tap is either the first half of a double-tap (→ locked, hands-free)
/// or, if no second tap follows, dropped silently.
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

    private readonly IRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ITextOutput _output;
    private readonly IScheduler _scheduler;
    private readonly AppSettings _settings;

    private Phase _phase = Phase.Idle;
    private string? _message;
    private bool _handsFree, _confirmed, _keyIsDown, _awaitingSecondTap, _secondTapDown, _ignoreNextRelease;
    private DateTime? _pressedAt, _recordingStarted, _lastReport;
    private Action? _pendingProblem;
    private IDisposable? _doubleTapTimer, _watchdog, _failTimer;
    private float _peakLevel;
    private volatile bool _isRecording, _spaceLocks;

    public DictationController(IRecorder recorder, ITranscriber transcriber, ITextOutput output,
                               IScheduler scheduler, AppSettings settings)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _output = output;
        _scheduler = scheduler;
        _settings = settings;
        _recorder.Level += level =>
        {
            if (level > _peakLevel) _peakLevel = level;
            LevelChanged?.Invoke(level);
        };
    }

    public DictationState State => new(_phase, _handsFree, _confirmed, _message);
    public event Action<DictationState>? StateChanged;
    /// Input level 0…1 while recording, on the audio thread.
    public event Action<float>? LevelChanged;
    /// Something needs the main window (no speech model yet).
    public event Action? ShowWindowRequested;
    /// Raised while a held key is being watched, to catch a missed key-up.
    public event Action? ResyncRequested;

    /// Thread-safe, for the keyboard hook's thread.
    public bool IsRecording => _isRecording;
    /// Thread-safe: would Space while holding the key lock the dictation now?
    public bool SpaceLocks => _spaceLocks;

    public void Handle(KeySignal signal)
    {
        switch (signal)
        {
            case KeySignal.Press: KeyPressed(); break;
            case KeySignal.Release: KeyReleased(); break;
            case KeySignal.Chord: Chord(); break;
            case KeySignal.SpaceLock: SpaceWhileHeld(); break;
            case KeySignal.Escape: if (_phase == Phase.Recording) Cancel(); break;
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
            case Phase.Idle or Phase.Failed:
                Begin();
                break;
        }
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

    /// Space while holding the key locks the dictation.
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

    // ---- Recording ------------------------------------------------------------

    private void Lock()
    {
        _handsFree = true;
        ConfirmStart();
    }

    private void ConfirmStart()
    {
        _confirmed = true;
        Publish();
    }

    private void Begin()
    {
        _failTimer?.Dispose();
        if (_transcriber.NotReadyReason is { } reason)
        {
            Report(() => { Fail(reason); ShowWindowRequested?.Invoke(); });
            return;
        }
        try
        {
            try
            {
                _recorder.Start(_settings.MicrophoneId);
            }
            catch when (_settings.MicrophoneId is not null)
            {
                // The chosen mic won't open (just unplugged): the system input will do.
                _recorder.Start(null);
            }
        }
        catch (Exception ex)
        {
            Report(() => Fail(MicrophoneProblem(ex)));
            return;
        }

        _phase = Phase.Recording;
        _message = null;
        _peakLevel = 0;
        _handsFree = false;
        _confirmed = false;
        _pressedAt = _recordingStarted = _scheduler.Now;
        StartWatchdog();
        Publish();
        _scheduler.After(TimeSpan.FromSeconds(0.2), () =>
        {
            if (_phase == Phase.Recording && !_confirmed
                && (_keyIsDown || _handsFree || _settings.Mode == DictationMode.Toggle)) ConfirmStart();
        });
    }

    public static string MicrophoneProblem(Exception ex) =>
        ex is UnauthorizedAccessException || (uint)ex.HResult == 0x80070005
            ? Loc.T("Mindtalk får inte använda mikrofonen. Slå på det under Inställningar → Sekretess → Mikrofon.",
                    "Mindtalk isn't allowed to use the microphone. Turn it on in Settings → Privacy → Microphone.")
            : Loc.T("Mikrofonen startade inte: ", "The microphone didn't start: ") + ex.Message;

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
        _handsFree = false;
        _awaitingSecondTap = false;
        _secondTapDown = false;
        _doubleTapTimer?.Dispose();
        _pressedAt = null;

        // Held a while and not a sound: the mic never delivered — say so.
        if (samples.Length == 0 && lasted > TimeSpan.FromSeconds(1))
        {
            Fail(Loc.T("Hittar ingen fungerande mikrofon.", "Can't find a working microphone."));
            return;
        }
        if (samples.Length <= MinimumSamples) { Reset(); return; }

        _phase = Phase.Transcribing;
        Publish();
        var heard = _peakLevel;
        // Where you are when you stop talking is where the text goes.
        var target = _output.CaptureTarget();
        try
        {
            var raw = await _transcriber.TranscribeAsync(samples);
            var text = TextCleanup.Apply(raw, _settings.RemoveFillers, _settings.VoiceCommands);
            if (text.Length == 0)
            {
                if (heard < 0.03f) Fail(Loc.T("Hörde inget – kontrollera mikrofonen.", "Heard nothing — check the microphone."));
                else Reset();
                return;
            }
            // A spoken line break at the end is the separator; otherwise a space.
            var outcome = await _output.InsertAsync(text.EndsWith('\n') ? text : text + " ", target, _settings.KeepInClipboard);
            if (outcome == InsertOutcome.TargetChanged)
            {
                Fail(Loc.T("Du bytte fönster – texten ligger i urklipp.", "You switched windows — the text is on the clipboard."));
                return;
            }
            Reset();
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
        _watchdog?.Dispose();
        _doubleTapTimer?.Dispose();
        _failTimer?.Dispose();
        _phase = Phase.Idle;
        _message = null;
        _handsFree = false;
        _confirmed = false;
        _awaitingSecondTap = false;
        _secondTapDown = false;
        _pressedAt = null;
        _recordingStarted = null;
        Publish();
    }

    private void Fail(string message)
    {
        _phase = Phase.Failed;
        _message = message;
        Publish();
        _failTimer?.Dispose();
        _failTimer = _scheduler.After(TimeSpan.FromSeconds(3), () =>
        {
            if (_phase == Phase.Failed) Reset();
        });
    }

    private void Publish()
    {
        _isRecording = _phase == Phase.Recording;
        _spaceLocks = _isRecording && (_handsFree || _settings.Mode != DictationMode.Hold);
        StateChanged?.Invoke(State);
    }
}
