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
    /// The input went away mid-recording (unplugged, Bluetooth dropped). Raised on any thread.
    event Action? Interrupted;
}

public interface ITranscriber
{
    Readiness Readiness { get; }
    /// The language in use ("Svenska"), for messages.
    string ModelTitle { get; }
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default);
}

public enum InsertOutcome
{
    /// Typed where the cursor is.
    Inserted,
    /// You switched to another app before the text was ready; nothing was typed.
    TargetChanged,
}

public interface ITextOutput
{
    /// Remembers where the text should go (the foreground window when you stop talking).
    nint CaptureTarget();
    /// Is the app you dictated in still the one in front? (Same app, not necessarily the same window.)
    bool IsStillInFront(nint target);
    /// Pastes <paramref name="text"/> where the cursor is, then puts the clipboard back —
    /// or, with <paramref name="keep"/>, leaves that in the clipboard instead.
    Task<InsertOutcome> InsertAsync(string text, nint target, string? keep);
    /// Puts text in the clipboard, as if you had copied it.
    void Copy(string text);
}

/// <summary>Is there somewhere for the text to go? Only a clear "no" counts.</summary>
public enum FieldKind { Text, None, Unknown }

/// <remarks>
/// Asynchronous: the answer comes over UI Automation from the app in front, which may be
/// Mindtalk itself (the "Try it" box) — asking from the UI thread would wait on itself.
/// A hung app mustn't hang a dictation, so each answer takes at most a fraction of a second.
/// </remarks>
public interface IFocusInspector
{
    /// What has keyboard focus in the app that owns <paramref name="target"/>.
    Task<FieldKind> FocusedFieldAsync(nint target);
    /// The cursor is in a password field right now.
    Task<bool> IsSecureFieldFocusedAsync();
}

/// <summary>Music and other sound while you dictate (pause, duck or leave alone).</summary>
public interface IMediaControl
{
    /// Recording started (and is clearly meant).
    void Begin();
    /// Recording stopped or was cancelled.
    void End();
}

/// <summary>Soft start/done sounds.</summary>
public interface ICues
{
    void Start();
    void Done();
    /// The first dictation during the introduction.
    void Celebrate();
}

public enum PolishStatus
{
    Available,
    /// The on-device model is turned off on this PC.
    NotEnabled,
    /// The on-device model is still downloading.
    NotReady,
    /// This PC can't run it.
    Unsupported,
}

/// <summary>Optional on-device AI tidy-up of a dictation (see PolishGuard).</summary>
public interface IPolisher
{
    PolishStatus Status { get; }
    /// Called when recording starts, so the model is loaded by the time you let go.
    void Prepare();
    /// The polished text, or null to keep what we have.
    Task<string?> PolishAsync(string text);
}

/// <summary>Timers on the UI thread (or on the test's fake clock).</summary>
public interface IScheduler
{
    DateTime Now { get; }
    IDisposable After(TimeSpan delay, Action action);
}
