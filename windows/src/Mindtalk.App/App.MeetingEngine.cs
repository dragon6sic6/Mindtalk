using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Meetings.Engine;
using Mindtalk.Platform.Meetings;

namespace Mindtalk.App;

/// <summary>
/// The meeting engine: the microphone (and a call app's sound), live text with the speech
/// model dictation uses, speakers, the final listen-through, imports, exports' audio, the call
/// box's word that a call started, and how long audio is kept.
/// </summary>
public partial class App
{
    private MeetingEngine? _meetingEngine;
    private CallWatcher? _calls;
    private Timer? _retention;

    /// The engine itself, for what the contract doesn't cover (naming speakers in a finished
    /// meeting, the audio for an export). Null until the services start.
    public MeetingEngine? MeetingEngine => _meetingEngine;

    /// Reads and writes recordings (imports, the .m4a audio export).
    public IMeetingMedia MeetingMedia { get; } = new MeetingMedia();

    partial void StartMeetingServices()
    {
        _calls = new CallWatcher();
        var speakers = new SpeakerModels(Demo.ModelsDirectory is { } models ? Path.Combine(models, "speakers") : null);
        _meetingEngine = new MeetingEngine(new MeetingEngineHost
        {
            Meetings = Meetings,
            Voices = Voices,
            Settings = Settings,
            Transcriber = Speech,
            Language = () => Speech.Status.Model.Id,
            // The final text counts the vocabulary's fixes; the live text (written again at the end) doesn't.
            Vocabulary = text => Vocabulary.Apply(text),
            VocabularyQuiet = text => Vocabulary.Reapply(text),
            SpeakerModels = speakers,
            // Microphones is created after the stores: read it when a meeting starts.
            OpenAudio = start => new MeetingAudio(() => Microphones.SelectedDeviceId(Settings.Microphone), () => CallSoundFrom(start)),
            Media = MeetingMedia,
            Calls = _calls,
            Summarizer = () => Summarizer,
        });
        MeetingService = _meetingEngine;
        MeetingService.Changed += () => _ui.TryEnqueue(Refresh);

        // Audio older than Settings → Meetings → Keep audio allows goes: now, and once a day.
        _retention = new Timer(_ =>
        {
            try { _meetingEngine.ApplyRetention(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Meetings: retention failed ({ex.Message})"); }
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromDays(1));

        // Quitting mid-meeting: stop listening and save what's there (audio.wav stays playable).
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (MeetingService?.Current is { State: MeetingState.Recording } live) live.Pause();
        };
    }

    /// <summary>
    /// The call app whose sound is mixed in, if any: the call the meeting was started for, or the
    /// call going on now — with headphones only, unless Settings say always or never.
    /// </summary>
    private int? CallSoundFrom(MeetingStart start)
    {
        var take = Settings.CallAudio switch
        {
            CallAudio.Always => true,
            CallAudio.Never => false,
            _ => OutputDevices.DefaultIsHeadphones(),
        };
        if (!take) return null;
        return start.CallProcessId ?? _meetingEngine?.ActiveCall?.ProcessId;
    }
}
