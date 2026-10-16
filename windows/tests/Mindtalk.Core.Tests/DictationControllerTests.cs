using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Xunit;

namespace Mindtalk.Core.Tests;

public class DictationControllerTests
{
    private sealed class FakeScheduler : IScheduler
    {
        private readonly List<(DateTime Due, Action Action, Handle Handle)> _queue = [];
        public DateTime Now { get; private set; } = new(2026, 1, 1);

        private sealed class Handle : IDisposable
        {
            public bool Cancelled;
            public void Dispose() => Cancelled = true;
        }

        public IDisposable After(TimeSpan delay, Action action)
        {
            var handle = new Handle();
            _queue.Add((Now + delay, action, handle));
            return handle;
        }

        public void Advance(double seconds)
        {
            var end = Now + TimeSpan.FromSeconds(seconds);
            while (true)
            {
                var next = _queue.Where(q => q.Due <= end).OrderBy(q => q.Due).FirstOrDefault();
                if (next.Action is null) break;
                _queue.Remove(next);
                Now = next.Due;
                if (!next.Handle.Cancelled) next.Action();
            }
            Now = end;
        }
    }

    private sealed class FakeRecorder : IRecorder
    {
        public bool Running;
        public int Starts;
        public DateTime StartedAt;
        public float Loudness = 0.5f;
        public required FakeScheduler Clock;
        public void Start(string? deviceId) { Running = true; Starts++; StartedAt = Clock.Now; }
        public float[] Stop()
        {
            if (!Running) return [];
            Running = false;
            Level?.Invoke(Loudness);
            return new float[(int)((Clock.Now - StartedAt).TotalSeconds * 16_000)];
        }
        public TimeSpan SilentFor => TimeSpan.Zero;
        public event Action<float>? Level;
        public event Action? Interrupted;
        public void Interrupt() => Interrupted?.Invoke();
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Readiness Readiness { get; set; } = Readiness.Ready;
        public string ModelTitle => "Svenska";
        public string Heard = "eh hej världen";
        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct) => Task.FromResult(Heard);
    }

    private sealed class FakeOutput : ITextOutput
    {
        public readonly List<string> Typed = [];
        public readonly List<string?> Kept = [];
        public readonly List<string> Copied = [];
        public bool InFront = true;
        public nint CaptureTarget() => 1;
        public bool IsStillInFront(nint target) => InFront;
        public void Copy(string text) => Copied.Add(text);
        public Task<InsertOutcome> InsertAsync(string text, nint target, string? keep)
        {
            Typed.Add(text);
            Kept.Add(keep);
            return Task.FromResult(InsertOutcome.Inserted);
        }
    }

    private sealed class FakeFocus : IFocusInspector
    {
        public FieldKind Field = FieldKind.Text;
        public bool Secure;
        public Task<FieldKind> FocusedFieldAsync(nint target) => Task.FromResult(Field);
        public Task<bool> IsSecureFieldFocusedAsync() => Task.FromResult(Secure);
    }

    private sealed class FakeMedia : IMediaControl
    {
        public int Begins, Ends;
        public void Begin() => Begins++;
        public void End() => Ends++;
    }

    private readonly FakeScheduler _clock = new();
    private readonly FakeRecorder _recorder;
    private readonly FakeTranscriber _transcriber = new();
    private readonly FakeOutput _output = new();
    private readonly FakeFocus _focus = new();
    private readonly FakeMedia _media = new();
    private readonly AppSettings _settings = new();
    private readonly List<string> _recent = [];
    private readonly DictationController _dictation;

    public DictationControllerTests()
    {
        _recorder = new FakeRecorder { Clock = _clock };
        _dictation = new DictationController(_recorder, _transcriber, _output, _clock, _settings)
        {
            Focus = _focus,
            Media = _media,
            AddRecent = _recent.Add,
            LatestRecent = () => _recent.LastOrDefault(),
        };
    }

    private void Press() => _dictation.Handle(new KeySignal.Press());
    private void Release() => _dictation.Handle(new KeySignal.Release());

    private void Dictate(double seconds = 1.5)
    {
        Press();
        _clock.Advance(seconds);
        Release();
    }

    [Fact]
    public void HoldTypesTheCleanedText()
    {
        Press();
        Assert.Equal(Phase.Recording, _dictation.State.Phase);
        _clock.Advance(1.5);
        Assert.True(_dictation.State.Visible);
        Release();
        Assert.Equal(["Hej världen "], _output.Typed);
        Assert.Equal([null], _output.Kept);
        Assert.Equal(["Hej världen"], _recent);
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
    }

    [Fact]
    public void KeepInClipboardLeavesTheTextWithoutTheSpace()
    {
        _settings.KeepInClipboard = true;
        Dictate();
        Assert.Equal(["Hej världen"], _output.Kept);
    }

    [Fact]
    public void QuickTapIsDroppedSilently()
    {
        Press();
        _clock.Advance(0.1);
        Release();
        Assert.False(_dictation.State.Visible);
        _clock.Advance(0.5);
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
        Assert.Empty(_output.Typed);
        Assert.Equal(0, _media.Begins);   // a tap never touches your music
    }

    [Fact]
    public void MediaPausesOnlyOnceTheDictationIsMeant()
    {
        Press();
        Assert.Equal(0, _media.Begins);
        _clock.Advance(0.3);
        Assert.Equal(1, _media.Begins);
        Release();
        Assert.True(_media.Ends >= 1);
    }

    [Fact]
    public void DoubleTapLocksAndTheNextPressFinishes()
    {
        Press(); _clock.Advance(0.1); Release();
        _clock.Advance(0.1);
        Press(); _clock.Advance(0.1); Release();
        Assert.True(_dictation.State.HandsFree);
        Assert.Equal(Phase.Recording, _dictation.State.Phase);

        _clock.Advance(3);
        Press();
        Release();
        Assert.Equal(["Hej världen "], _output.Typed);
    }

    [Fact]
    public void SpaceWhileHoldingLocks()
    {
        Press(); _clock.Advance(0.5);
        Assert.True(_dictation.SpaceLocks);
        _dictation.Handle(new KeySignal.SpaceLock());
        Release();
        Assert.True(_dictation.State.HandsFree);
        Assert.Equal(Phase.Recording, _dictation.State.Phase);
    }

    [Fact]
    public void SpaceDoesNotLockInHoldMode()
    {
        _settings.Mode = DictationMode.Hold;
        Press(); _clock.Advance(0.5);
        Assert.False(_dictation.SpaceLocks);
    }

    [Fact]
    public void ChordCancels()
    {
        Press(); _clock.Advance(0.05);
        _dictation.Handle(new KeySignal.Chord());
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
        Release();
        Assert.Empty(_output.Typed);
    }

    [Fact]
    public void EscapeCancels()
    {
        Press(); _clock.Advance(1);
        _dictation.Handle(new KeySignal.Escape());
        Release();
        Assert.Empty(_output.Typed);
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
    }

    [Fact]
    public void ToggleModeStartsAndStopsWithTaps()
    {
        _settings.Mode = DictationMode.Toggle;
        Press(); _clock.Advance(0.1); Release();
        Assert.Equal(Phase.Recording, _dictation.State.Phase);
        _clock.Advance(2);
        Press(); Release();
        Assert.Equal(["Hej världen "], _output.Typed);
    }

    [Fact]
    public void MissingModelOpensTheWindowOnlyForAHeldPress()
    {
        _transcriber.Readiness = Readiness.Missing;
        var windowShown = 0;
        _dictation.ShowWindowRequested += () => windowShown++;

        Press(); _clock.Advance(0.1); Release();              // a stray tap: silent
        _clock.Advance(1);
        Assert.Equal(0, windowShown);

        Press(); _clock.Advance(0.3);                        // held: say so
        Assert.Equal(1, windowShown);
        Assert.Equal(0, _recorder.Starts);
    }

    [Fact]
    public void MissingModelDuringTheIntroductionIsSaidInTheHud()
    {
        _transcriber.Readiness = Readiness.Missing;
        _dictation.IsOnboarding = () => true;
        Press(); _clock.Advance(0.3);
        Assert.Equal(Phase.Failed, _dictation.State.Phase);
        Assert.Contains("Svenska", _dictation.State.Message);
    }

    [Fact]
    public void NoTextFieldPutsTheTextInTheClipboard()
    {
        _focus.Field = FieldKind.None;
        Dictate();
        Assert.Empty(_output.Typed);
        Assert.Equal(["Hej världen"], _output.Copied);
        Assert.Equal(Phase.Copied, _dictation.State.Phase);
        _clock.Advance(3);
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
    }

    [Fact]
    public void PasswordFieldIsTypedButNeverKept()
    {
        _focus.Secure = true;
        _settings.KeepInClipboard = true;
        Dictate();
        Assert.Equal(["Hej världen "], _output.Typed);
        Assert.Equal([null], _output.Kept);
        Assert.Empty(_recent);
    }

    [Fact]
    public void SwitchingAppMeanwhileTypesNothing()
    {
        _output.InFront = false;
        Dictate();
        Assert.Empty(_output.Typed);
        Assert.Equal(Phase.Failed, _dictation.State.Phase);
        Assert.Equal(["Hej världen"], _recent);   // one Ctrl+Alt+V away
    }

    [Fact]
    public void SilenceSaysHeardNothing()
    {
        _transcriber.Heard = "";
        _recorder.Loudness = 0;
        Dictate();
        Assert.Equal(Phase.Failed, _dictation.State.Phase);
    }

    [Fact]
    public void InterruptedMicKeepsWhatWasSaid()
    {
        Press(); _clock.Advance(1);
        _recorder.Interrupt();
        _clock.Advance(0);
        Assert.Equal(["Hej världen "], _output.Typed);
    }

    [Fact]
    public void PasteLastTypesTheLatestDictationAgain()
    {
        Dictate();
        _dictation.Handle(new KeySignal.PasteLast());
        Assert.Equal(["Hej världen ", "Hej världen "], _output.Typed);
    }

    [Fact]
    public void PasteLastWithNothingToPasteBeeps()
    {
        var beeps = 0;
        _dictation.Beep = () => beeps++;
        _dictation.Handle(new KeySignal.PasteLast());
        Assert.Equal(1, beeps);
    }

    [Fact]
    public void LanguageShowsForAMomentAfterASwitch()
    {
        var engine = "swedish";
        _dictation.Engine = () => engine;
        _dictation.InstalledModels = () => 2;
        Dictate();
        Press(); _clock.Advance(0.3);
        Assert.False(_dictation.State.ShowsLanguage);   // same as last time
        Release();

        engine = "multilingual";
        Press(); _clock.Advance(0.3);
        Assert.True(_dictation.State.ShowsLanguage);
        _clock.Advance(1.6);
        Assert.False(_dictation.State.ShowsLanguage);
        Release();
    }
}
