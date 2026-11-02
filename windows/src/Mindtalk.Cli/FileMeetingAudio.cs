using Mindtalk.Core.Meetings.Engine;

/// <summary>
/// Plays a recording into a live meeting in real time, as the microphone would — to test the
/// live text, speakers and round of names without anyone speaking. Silence after the end.
/// </summary>
internal sealed class FileMeetingAudio(float[] samples) : IMeetingAudio
{
    private const int Block = 1600;   // 100 ms
    private Timer? _timer;
    private int _at;

    public event Action<float[]>? Samples;
    public event Action? Interrupted { add { } remove { } }
    public float Level { get; private set; }
    public bool HearsCall => false;

    public void Start() => _timer = new Timer(_ => Tick(), null, 0, 100);

    private void Tick()
    {
        var block = new float[Block];
        var n = Math.Clamp(samples.Length - _at, 0, Block);
        if (n > 0) Array.Copy(samples, _at, block, 0, n);
        _at += Block;
        Level = (float)Math.Min(1, Math.Sqrt(block.Average(v => v * v)) * 12);
        Samples?.Invoke(block);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();
}
