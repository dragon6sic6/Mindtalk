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
        public required FakeScheduler Clock;
        public void Start(string? deviceId) { Running = true; Starts++; StartedAt = Clock.Now; }
        public float[] Stop()
        {
            if (!Running) return [];
            Running = false;
            Level?.Invoke(0.5f);
            return new float[(int)((Clock.Now - StartedAt).TotalSeconds * 16_000)];
        }
        public TimeSpan SilentFor => TimeSpan.Zero;
        public event Action<float>? Level;
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public string? NotReadyReason { get; set; }
        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct) => Task.FromResult("eh hej världen");
    }

    private sealed class FakeOutput : ITextOutput
    {
        public readonly List<string> Typed = [];
        public nint CaptureTarget() => 1;
        public Task<InsertOutcome> InsertAsync(string text, nint target, bool keep)
        {
            Typed.Add(text);
            return Task.FromResult(InsertOutcome.Inserted);
        }
    }

    private readonly FakeScheduler _clock = new();
    private readonly FakeRecorder _recorder;
    private readonly FakeTranscriber _transcriber = new();
    private readonly FakeOutput _output = new();
    private readonly AppSettings _settings = new();
    private readonly DictationController _dictation;

    public DictationControllerTests()
    {
        _recorder = new FakeRecorder { Clock = _clock };
        _dictation = new DictationController(_recorder, _transcriber, _output, _clock, _settings);
    }

    private void Press() => _dictation.Handle(new KeySignal.Press());
    private void Release() => _dictation.Handle(new KeySignal.Release());

    [Fact]
    public void HoldTypesTheCleanedText()
    {
        Press();
        Assert.Equal(Phase.Recording, _dictation.State.Phase);
        _clock.Advance(1.5);
        Assert.True(_dictation.State.Visible);
        Release();
        Assert.Equal(["Hej världen "], _output.Typed);
        Assert.Equal(Phase.Idle, _dictation.State.Phase);
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
    public void MissingModelIsReportedOnlyForAHeldPress()
    {
        _transcriber.NotReadyReason = "not ready";
        var windowShown = 0;
        _dictation.ShowWindowRequested += () => windowShown++;

        Press(); _clock.Advance(0.1); Release();              // a stray tap: silent
        _clock.Advance(1);
        Assert.Equal(0, windowShown);

        Press(); _clock.Advance(0.3);                        // held: say so
        Assert.Equal(Phase.Failed, _dictation.State.Phase);
        Assert.Equal(1, windowShown);
        Assert.Equal(0, _recorder.Starts);
    }
}
