#if DEBUG
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>
/// Debug self-test (`--test-audio file.wav`): stands in for the microphone, so the whole
/// path — key, HUD, transcription, paste — can be checked without anyone speaking.
/// Each recording "hears" the file.
/// </summary>
public sealed class FileRecorder(float[] samples) : IRecorder
{
    private Timer? _levels;

    public TimeSpan SilentFor => TimeSpan.Zero;
    public event Action<float>? Level;
    public event Action? Interrupted { add { } remove { } }

    public void Start(string? deviceId)
    {
        var tick = 0;
        _levels = new Timer(_ => Level?.Invoke(0.3f + 0.3f * MathF.Sin(tick++ / 2f)), null, 0, 30);
    }

    public float[] Stop()
    {
        var running = _levels is not null;
        _levels?.Dispose();
        _levels = null;
        return running ? samples : [];
    }
}
#endif
