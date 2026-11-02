using Mindtalk.Core.Dictation;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>A meeting's sound: the microphone, plus the call app's sound on a call, as one 16 kHz mono stream.</summary>
public interface IMeetingAudio : IDisposable
{
    /// 16 kHz mono samples in [-1, 1], raised on an audio thread.
    event Action<float[]>? Samples;
    /// The microphone went away (unplugged, Bluetooth dropped). Raised on any thread.
    event Action? Interrupted;
    /// Microphone level now, 0…1.
    float Level { get; }
    /// The call app's sound is mixed in.
    bool HearsCall { get; }
    /// Opens the microphone (and the call's sound). Throws if the microphone can't be opened.
    void Start();
    /// Lets go of the devices; <see cref="Start"/> opens them again.
    void Stop();
}

/// <summary>Reading recordings and writing the audio Mindtalk keeps.</summary>
public interface IMeetingMedia
{
    /// Audio or video Windows can read.
    bool CanRead(string path);
    /// The file's sound as 16 kHz mono. <paramref name="progress"/> 0…1.
    float[] Decode(string path, IProgress<double>? progress = null, CancellationToken ct = default);
    /// Encodes a WAV to AAC (.m4a). False if it couldn't (the WAV is then kept).
    bool EncodeM4a(string wavPath, string m4aPath);
}

/// <summary>Notices when a call app starts and stops using the microphone.</summary>
public interface ICallWatcher : IDisposable
{
    event Action<DetectedCall>? Started;
    event Action<DetectedCall>? Ended;
    /// The calls going on now.
    IReadOnlyList<DetectedCall> Active { get; }
}

/// <summary>Everything the meeting engine works with. The app fills it in; the CLI and tests make their own.</summary>
public sealed class MeetingEngineHost
{
    public required MeetingStore Meetings { get; init; }
    public required VoiceStore Voices { get; init; }
    public required AppSettings Settings { get; init; }
    /// The speech model (shared with dictation).
    public required ITranscriber Transcriber { get; init; }
    /// The speech model in use ("swedish"), stored with the meeting.
    public Func<string> Language { get; init; } = () => "";
    /// Your vocabulary. The first one counts the fixes (for the final text); the second doesn't (live text).
    public Func<string, string> Vocabulary { get; init; } = t => t;
    public Func<string, string> VocabularyQuiet { get; init; } = t => t;
    public required SpeakerModels SpeakerModels { get; init; }
    /// Opens the sound for a meeting: the chosen microphone, plus the call app's sound when that's wanted.
    public required Func<MeetingStart, IMeetingAudio> OpenAudio { get; init; }
    public required IMeetingMedia Media { get; init; }
    public ICallWatcher? Calls { get; init; }
    /// Who writes summaries and titles; null (or not ready) leaves the summary out.
    public Func<IMeetingSummarizer?> Summarizer { get; init; } = () => null;
    /// Speakers for the CLI and tests (otherwise sherpa-onnx, once the models are downloaded).
    public ISpeakerAnalyzer? Analyzer { get; init; }
}
