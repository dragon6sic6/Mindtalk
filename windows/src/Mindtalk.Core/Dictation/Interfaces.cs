namespace Mindtalk.Core.Dictation;

/// <summary>Captures the microphone as 16 kHz mono, in memory only.</summary>
public interface IRecorder
{
    /// Starts capturing from the given endpoint (null = the system default). Throws if it can't.
    void Start(string? deviceId);
    /// Stops and hands over everything recorded since Start.
    float[] Stop();
    /// How long since audio last arrived (a dead mic delivers nothing).
    TimeSpan SilentFor { get; }
    /// Input level 0…1, raised on the audio thread.
    event Action<float>? Level;
}

public interface ITranscriber
{
    /// Null when ready; otherwise what to tell the user ("still downloading…").
    string? NotReadyReason { get; }
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default);
}

public enum InsertOutcome
{
    /// Typed where the cursor is.
    Inserted,
    /// The window you dictated in lost focus before the text was ready; it's on the clipboard instead.
    TargetChanged,
}

public interface ITextOutput
{
    /// Remembers where the text should go (the foreground window when you stop talking).
    nint CaptureTarget();
    Task<InsertOutcome> InsertAsync(string text, nint target, bool keepInClipboard);
}

/// <summary>Timers on the UI thread (or on the test's fake clock).</summary>
public interface IScheduler
{
    DateTime Now { get; }
    IDisposable After(TimeSpan delay, Action action);
}
