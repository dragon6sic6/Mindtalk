namespace Mindtalk.Core.Meetings;

// The seams between the meeting engine (audio, speech, speakers), the writers (summaries,
// titles, answers) and the windows. The windows only talk to these.

/// <summary>How a meeting is started.</summary>
/// <param name="RoundOfNames">Start with a round of names: everyone says "Hi, my name is …".</param>
/// <param name="CallApp">For a call: the call app's name ("Teams"), shown in the meeting.</param>
/// <param name="CallProcessId">For a call: the process whose sound is taken in.</param>
public sealed record MeetingStart(MeetingSource Source, bool RoundOfNames, string? CallApp = null, int? CallProcessId = null);

/// <summary>A call app started using the microphone (the "take notes?" box).</summary>
public sealed record DetectedCall(string App, int ProcessId, DateTime Started);

/// <summary>A voice heard in a meeting that might belong to someone you've named before.</summary>
public sealed record VoiceSuggestion(string SpeakerId, Guid VoiceId, string Name, float Score);

/// <summary>
/// The meeting being recorded. Events may come from any thread; read the properties again
/// when <see cref="Changed"/> is raised.
/// </summary>
public interface ILiveMeeting
{
    Meeting Meeting { get; }
    MeetingState State { get; }
    /// Recorded time, pauses not counted.
    TimeSpan Elapsed { get; }
    /// The words being written right now, not yet a segment (about a second behind the voice).
    string? Partial { get; }
    /// The speaker of <see cref="Partial"/>, if known yet.
    string? PartialSpeakerId { get; }
    /// Microphone level now, 0…1 (for the waveform).
    float Level { get; }
    /// Taking in the call app's sound too.
    bool HearsCall { get; }
    /// In the round of names at the start.
    bool InRoundOfNames { get; }
    /// A new voice it may know: "Is this Anna?"
    VoiceSuggestion? Suggestion { get; }
    /// How far the final listen-through and the summary have come (0…1), while finishing.
    double FinishProgress { get; }

    event Action? Changed;

    void Pause();
    void Resume();
    /// Ends the recording: the whole recording is listened through again, then summarized.
    Task StopAsync();
    /// Throws the meeting away, audio and all.
    void Discard();
    void AddNote(string text);
    void AddMark(MarkKind kind);
    void EndRoundOfNames();
    /// Names a speaker (null: unnamed). With <paramref name="remember"/> and Remember voices on, the voice is kept.
    void NameSpeaker(string speakerId, string? name, bool remember);
    void SetBackground(string speakerId, bool background);
    void AcceptSuggestion(bool accept);
}

/// <summary>Meetings: start, import, redo — and a word when a call starts.</summary>
public interface IMeetingService
{
    /// The meeting being recorded, if any.
    ILiveMeeting? Current { get; }
    /// Meetings being imported or summarized in the background: id → progress 0…1.
    IReadOnlyDictionary<Guid, double> Working { get; }

    /// The speaker models are on this PC (downloaded once).
    bool SpeakerModelReady { get; }
    /// Download progress 0…1, null when not downloading.
    double? SpeakerModelProgress { get; }
    string? SpeakerModelError { get; }
    Task DownloadSpeakerModelAsync();

    event Action? Changed;
    /// A call app started using the microphone, and no meeting is being recorded.
    event Action<DetectedCall>? CallStarted;
    /// The call ended (the box goes away if it's still showing).
    event Action<DetectedCall>? CallEnded;

    ILiveMeeting Start(MeetingStart start);

    /// Audio and video Mindtalk can read: mp3, m4a, wav, mp4, mov, wma, aac, flac…
    bool CanImport(string path);
    /// Becomes a meeting like any other: text, speakers, summary.
    Task<Meeting> ImportAsync(string path, CancellationToken ct = default);

    /// Writes the summary (and title) again, with the writer chosen now.
    Task ResummarizeAsync(Guid meetingId, CancellationToken ct = default);
}

/// <summary>Writes summaries and titles.</summary>
public interface IMeetingSummarizer
{
    /// The writer in use, for "Summarized by Gemma 4 E4B".
    string WriterName { get; }
    /// Ready to write (Gemma downloaded, Ollama reachable…).
    bool IsReady { get; }
    /// Every item is checked against the line in the transcript it came from; owners must be people in the meeting.
    Task<MeetingSummary> SummarizeAsync(Meeting meeting, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<string> TitleAsync(Meeting meeting, CancellationToken ct = default);
}

/// <summary>Answers questions about one meeting, a folder, or all of them.</summary>
public interface IMeetingAsk
{
    /// <paramref name="partial"/> receives the answer as it's written. Says so when nothing matches instead of guessing.
    Task<AskExchange> AskAsync(string question, IReadOnlyList<Meeting> scope, IProgress<string>? partial = null, CancellationToken ct = default);
}
