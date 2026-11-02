namespace Mindtalk.Core.Meetings.Engine;

/// <summary>A stretch of speech, cut at a pause; <see cref="StartSample"/> counts from the start of the recording.</summary>
public sealed record SpeechChunk(long StartSample, float[] Samples)
{
    public double Start => StartSample / (double)SpeechChunker.SampleRate;
    public double End => (StartSample + Samples.Length) / (double)SpeechChunker.SampleRate;
}

/// <summary>
/// Cuts a stream of 16 kHz audio into stretches of speech, at pauses (energy-based voice
/// activity with a noise floor that follows the room). Short stretches wait for a real pause;
/// the longer one gets, the shorter a pause is enough, and nothing grows past
/// <see cref="MaxSeconds"/> — so the speech model is never held for long, and dictation in the
/// middle of a meeting still gets its turn.
/// </summary>
public sealed class SpeechChunker
{
    public const int SampleRate = 16_000;
    /// 30 ms frames.
    public const int FrameSize = 480;

    private readonly double _maxSeconds;
    private readonly List<float> _speech = new(SampleRate * 20);
    private readonly Queue<float[]> _preRoll = new();
    private readonly float[] _frame = new float[FrameSize];
    private int _frameFill;
    private long _position;          // samples seen, at the end of the last whole frame
    private long _speechStart;
    private bool _inSpeech;
    private int _silentFrames, _voicedFrames;
    private float _floor = 0.002f;

    public SpeechChunker(double maxSeconds = 15) => _maxSeconds = maxSeconds;

    public double MaxSeconds => _maxSeconds;
    /// Below this, a frame is never speech (a very quiet room).
    public float MinLevel { get; init; } = 0.004f;
    /// Stretches with less voiced time than this are dropped (a click, a cough).
    public double MinSpeechSeconds { get; init; } = 0.25;
    /// Silence kept before and after the speech, so first and last sounds aren't clipped.
    public int PreRollFrames { get; init; } = 7;

    /// Being spoken now: in a stretch that hasn't ended yet.
    public bool InSpeech => _inSpeech;
    /// Where the stretch being spoken started, and its audio so far (for the words in progress).
    public long PendingStart => _speechStart;
    public float[] PendingSamples() => _inSpeech ? _speech.ToArray() : [];
    public double PendingSeconds => _inSpeech ? _speech.Count / (double)SampleRate : 0;

    /// <summary>Feeds audio; returns the stretches that ended in it.</summary>
    public List<SpeechChunk> Push(ReadOnlySpan<float> samples)
    {
        var done = new List<SpeechChunk>();
        foreach (var s in samples)
        {
            _frame[_frameFill++] = s;
            if (_frameFill < FrameSize) continue;
            _frameFill = 0;
            if (Frame(_frame) is { } chunk) done.Add(chunk);
        }
        return done;
    }

    /// <summary>Ends the stretch being spoken (a pause button, the end of the meeting).</summary>
    public SpeechChunk? Flush()
    {
        if (_frameFill > 0 && _inSpeech) for (var i = 0; i < _frameFill; i++) _speech.Add(_frame[i]);
        _position += _frameFill;
        _frameFill = 0;
        var chunk = _inSpeech ? Cut(_speech.Count) : null;
        Reset();
        return chunk;
    }

    /// <summary>Skips ahead without audio (time that isn't recorded, say).</summary>
    public void Reset()
    {
        _inSpeech = false;
        _speech.Clear();
        _preRoll.Clear();
        _silentFrames = _voicedFrames = 0;
    }

    private SpeechChunk? Frame(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var rms = (float)Math.Sqrt(sum / frame.Length);
        var voiced = rms > Math.Max(MinLevel, _floor * 3.2f);
        // The floor follows quiet stretches quickly and creeps up slowly, so steady noise
        // (a fan) stops counting as speech while speech itself doesn't raise it much.
        _floor = rms < _floor ? _floor * 0.9f + rms * 0.1f : Math.Min(_floor * 1.002f + 1e-6f, 0.05f);
        var start = _position;
        _position += frame.Length;

        if (!_inSpeech)
        {
            _preRoll.Enqueue((float[])frame.Clone());
            if (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            if (!voiced) return null;
            _inSpeech = true;
            _speechStart = start - (_preRoll.Count - 1) * (long)FrameSize;
            foreach (var f in _preRoll) _speech.AddRange(f);
            _preRoll.Clear();
            _voicedFrames = 1;
            _silentFrames = 0;
            return null;
        }

        _speech.AddRange(frame);
        if (voiced) { _voicedFrames++; _silentFrames = 0; }
        else _silentFrames++;

        var seconds = _speech.Count / (double)SampleRate;
        if (_silentFrames * FrameSize / (double)SampleRate >= PauseNeeded(seconds))
        {
            // Keep a little of the silence after the last word, not all of it.
            var keep = _speech.Count - Math.Max(0, _silentFrames - PreRollFrames) * FrameSize;
            var chunk = Cut(keep);
            Reset();
            return chunk;
        }
        if (seconds >= _maxSeconds) return CutAtQuietest();
        return null;
    }

    /// The pause that ends a stretch: long while it's short, shorter as it grows.
    public double PauseNeeded(double seconds) =>
        seconds < _maxSeconds * 0.4 ? 0.6 : seconds < _maxSeconds * 0.75 ? 0.35 : 0.2;

    private SpeechChunk? Cut(int length)
    {
        var voiced = _voicedFrames * FrameSize / (double)SampleRate;
        if (voiced < MinSpeechSeconds || length <= 0) return null;
        return new SpeechChunk(_speechStart, _speech.GetRange(0, Math.Min(length, _speech.Count)).ToArray());
    }

    /// Too long without a pause: cut at the quietest moment of the last few seconds and go on from there.
    private SpeechChunk? CutAtQuietest()
    {
        var frames = _speech.Count / FrameSize;
        var from = Math.Max(1, frames - 3 * SampleRate / FrameSize);
        var best = frames;
        var bestEnergy = double.MaxValue;
        for (var f = from; f < frames; f++)
        {
            double e = 0;
            for (var i = f * FrameSize; i < (f + 1) * FrameSize; i++) e += _speech[i] * _speech[i];
            if (e < bestEnergy) { bestEnergy = e; best = f; }
        }
        var cut = best * FrameSize;
        var chunk = new SpeechChunk(_speechStart, _speech.GetRange(0, cut).ToArray());
        var rest = _speech.GetRange(cut, _speech.Count - cut);
        _speechStart += cut;
        _speech.Clear();
        _speech.AddRange(rest);
        _voicedFrames = Math.Max(1, rest.Count / FrameSize);
        _silentFrames = 0;
        return chunk;
    }
}
