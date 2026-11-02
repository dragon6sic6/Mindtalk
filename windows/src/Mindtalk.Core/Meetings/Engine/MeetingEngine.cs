using System.Collections.Concurrent;
using System.Diagnostics;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// Meetings: records one at a time, imports recordings, writes summaries again, and passes on
/// a word when a call app starts using the microphone. Events come from background threads.
/// </summary>
public sealed class MeetingEngine : IMeetingService, IDisposable
{
    /// Audio Mindtalk reads for sure; anything else Windows' Media Foundation can read works too.
    public static readonly string[] ImportExtensions =
        [".mp3", ".m4a", ".wav", ".mp4", ".mov", ".wma", ".aac", ".flac", ".m4v", ".3gp", ".3g2", ".adts", ".wmv", ".asf", ".webm", ".mkv", ".ogg", ".opus", ".amr", ".aiff", ".aif"];

    private readonly MeetingEngineHost _host;
    private readonly ConcurrentDictionary<Guid, double> _working = new();
    /// Voiceprints of the speakers in recent meetings, in memory only — for naming them afterwards.
    private readonly ConcurrentDictionary<Guid, Dictionary<string, float[]>> _prints = new();
    private readonly HashSet<string> _announced = [];
    private readonly object _gate = new();
    private ISpeakerAnalyzer? _analyzer;
    private bool _analyzerTried;
    private LiveMeeting? _current;
    private double? _speakerProgress;
    private string? _speakerError;

    public MeetingEngine(MeetingEngineHost host)
    {
        _host = host;
        _analyzer = host.Analyzer;
        _analyzerTried = host.Analyzer is not null;
        if (host.Calls is { } calls)
        {
            calls.Started += OnCallStarted;
            calls.Ended += OnCallEnded;
        }
    }

    public ILiveMeeting? Current => _current;
    public IReadOnlyDictionary<Guid, double> Working => _working;

    public event Action? Changed;
    public event Action<DetectedCall>? CallStarted;
    public event Action<DetectedCall>? CallEnded;

    // ---- Speaker models ---------------------------------------------------------------

    public bool SpeakerModelReady => _host.Analyzer is not null || _host.SpeakerModels.IsInstalled;
    public double? SpeakerModelProgress => _speakerProgress;
    public string? SpeakerModelError => _speakerError;

    public async Task DownloadSpeakerModelAsync()
    {
        if (_speakerProgress is not null || SpeakerModelReady) return;
        _speakerProgress = 0;
        _speakerError = null;
        Changed?.Invoke();
        var last = -1;
        var progress = new Progress<double>(p =>
        {
            if (_speakerProgress is null || (int)(p * 200) == last) return;
            last = (int)(p * 200);
            _speakerProgress = p;
            Changed?.Invoke();
        });
        try
        {
            await Task.Run(() => _host.SpeakerModels.InstallAsync(progress));
            lock (_gate) _analyzerTried = false;
        }
        catch (Exception ex)
        {
            _speakerError = SpeechService.PlainWords(ex, System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable());
        }
        finally
        {
            _speakerProgress = null;
            Changed?.Invoke();
        }
    }

    /// sherpa-onnx with the downloaded models, loaded on first use.
    private ISpeakerAnalyzer? Analyzer()
    {
        lock (_gate)
        {
            if (_analyzerTried) return _analyzer;
            _analyzerTried = true;
            _analyzer = SherpaSpeakerAnalyzer.TryCreate(_host.SpeakerModels);
            return _analyzer;
        }
    }

    // ---- Recording ----------------------------------------------------------------------

    public ILiveMeeting Start(MeetingStart start)
    {
        lock (_gate)
        {
            if (_current is { State: MeetingState.Recording or MeetingState.Paused } running) return running;
        }
        var meeting = new Meeting
        {
            Started = DateTime.Now,
            Source = start.Source,
            Language = _host.Language(),
            TitleIsAuto = true,
        };
        meeting.Title = FallbackTitle(meeting.Started, start.Source == MeetingSource.Call ? start.CallApp : null);
        // The voice model loads while the microphone opens.
        var analyzer = Analyzer();
        var audio = _host.OpenAudio(start);
        var live = new LiveMeeting(this, _host, meeting, audio, analyzer, start.RoundOfNames);
        lock (_gate) _current = live;
        Changed?.Invoke();
        return live;
    }

    /// "Möte, tisdag 9:30" / "Meeting, Tuesday 9:30" — until the writer has a better title.
    public static string FallbackTitle(DateTime started, string? callApp = null)
    {
        var day = started.ToString("dddd", Loc.Culture);
        var time = started.ToString("H:mm", Loc.Culture);
        if (callApp is not null) return Loc.T($"Samtal i {callApp}, {day} {time}", $"{callApp} call, {Capitalize(day)} {time}");
        return Loc.T($"Möte, {day} {time}", $"Meeting, {Capitalize(day)} {time}");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Loc.Culture) + s[1..];

    internal void Discarded(LiveMeeting live)
    {
        lock (_gate) if (_current == live) _current = null;
        _host.Meetings.Delete(live.Meeting.Id);
        Changed?.Invoke();
    }

    internal float[]? PrintOf(Guid meeting, string speakerId) =>
        _prints.TryGetValue(meeting, out var prints) && prints.TryGetValue(speakerId, out var print) ? print : null;

    // ---- After the meeting --------------------------------------------------------------

    /// <summary>
    /// The end of a meeting or an import: the whole recording listened through again, the
    /// audio kept as .m4a, then the summary and the title. <paramref name="progress"/> 0…1.
    /// </summary>
    internal async Task FinishAsync(Meeting meeting, IReadOnlyList<Segment> live, IReadOnlyList<Speaker> liveSpeakers,
                                    Action<double> progress, Action<MeetingState> setState, CancellationToken ct,
                                    float[]? audio = null, double from = 0)
    {
        void Report(double p)
        {
            var overall = from + (1 - from) * p;
            _working[meeting.Id] = overall;
            progress(overall);
            Changed?.Invoke();
        }
        void Set(MeetingState state)
        {
            meeting.State = state;
            setState(state);
            _host.Meetings.Save(meeting);
        }

        try
        {
            Report(0);
            Set(MeetingState.Finishing);
            var wav = Path.Combine(_host.Meetings.FolderOf(meeting.Id), "audio.wav");
            audio ??= File.Exists(wav) ? await Task.Run(() => WavFile.Read16kMono(wav), ct) : [];
            meeting.Duration = Math.Max(meeting.Duration, audio.Length / (double)FinalPass.SampleRate);

            if (_host.Transcriber.Readiness != Readiness.Ready && live.Count == 0)
            {
                // Nothing written live and no speech model to write it now: keep the audio, say why.
                meeting.Error = Loc.T($"Språkmodellen ({_host.Transcriber.ModelTitle}) var inte redo, så ingen text skrevs.",
                                      $"The speech model ({_host.Transcriber.ModelTitle}) wasn’t ready, so no text was written.");
            }
            else if (_host.Transcriber.Readiness == Readiness.Ready && audio.Length > 0)
            {
                var pass = new FinalPass(_host.Transcriber, Analyzer(), Clean);
                try
                {
                    var result = await pass.RunAsync(audio, live, liveSpeakers, new Progress<double>(p => Report(0.7 * p)), ct);
                    // A final pass that heard nothing doesn't throw away what was written live.
                    if (result.Segments.Count > 0 || live.Count == 0)
                    {
                        // Names given while this ran (the speaker ids carry over) win over the snapshot.
                        foreach (var speaker in result.Speakers)
                            if (meeting.SpeakerById(speaker.Id) is { } now && liveSpeakers.Any(s => s.Id == speaker.Id))
                            {
                                speaker.Name = now.Name;
                                speaker.IsBackground = now.IsBackground;
                                speaker.VoiceId = now.VoiceId;
                                speaker.IsMe = now.IsMe;
                            }
                        meeting.Speakers = result.Speakers;
                        meeting.Segments = result.Segments;
                        _prints[meeting.Id] = result.Prints;
                        RecognizeVoices(meeting, result.Prints);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.WriteLine($"Meetings: the final pass failed ({ex.GetType().Name}: {ex.Message}); keeping the live text");
                }
            }
            Report(0.7);
            _host.Meetings.Save(meeting);

            await Task.Run(() => KeepAudioAsM4a(meeting), ct);
            Report(0.75);
            await SummarizeAsync(meeting, p => Report(0.75 + 0.25 * p), Set, ct);
            Set(MeetingState.Done);
            Report(1);
        }
        catch (OperationCanceledException)
        {
            Set(MeetingState.Failed);
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Meetings: finishing failed ({ex})");
            meeting.Error ??= Loc.T("Något gick fel när mötet avslutades. Texten som skrevs finns kvar.",
                                    "Something went wrong when the meeting ended. The text that was written is kept.");
            Set(meeting.Segments.Count > 0 ? MeetingState.Done : MeetingState.Failed);
        }
        finally
        {
            _working.TryRemove(meeting.Id, out _);
            Changed?.Invoke();
        }
    }

    private string Clean(string text) => _host.Vocabulary(TextCleanup.Apply(text, fillers: true, commands: false));

    /// Unnamed speakers whose voice you've named before (Remember voices on).
    private void RecognizeVoices(Meeting meeting, Dictionary<string, float[]> prints)
    {
        if (!_host.Settings.RememberVoices) return;
        foreach (var speaker in meeting.Speakers.Where(s => s.Name is null && !s.IsBackground))
        {
            if (!prints.TryGetValue(speaker.Id, out var print)) continue;
            var taken = meeting.Speakers.Select(s => s.VoiceId).OfType<Guid>();
            if (_host.Voices.Match(print, LiveMeeting.CertainMatch, taken) is { } match)
            {
                speaker.Name = match.Voice.Name;
                speaker.VoiceId = match.Voice.Id;
                speaker.IsMe = IsMe(match.Voice.Name);
            }
        }
    }

    private bool IsMe(string? name) =>
        name is not null && _host.Settings.UserName is { Length: > 0 } me && string.Equals(name.Trim(), me.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// audio.wav → audio.m4a (AAC); the WAV stays if encoding fails.
    private void KeepAudioAsM4a(Meeting meeting)
    {
        var folder = _host.Meetings.FolderOf(meeting.Id);
        var wav = Path.Combine(folder, "audio.wav");
        if (!File.Exists(wav)) return;
        var m4a = Path.Combine(folder, "audio.m4a");
        if (_host.Media.EncodeM4a(wav, m4a) && File.Exists(m4a) && new FileInfo(m4a).Length > 0)
        {
            meeting.AudioFile = "audio.m4a";
            try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        else meeting.AudioFile = "audio.wav";
        _host.Meetings.Save(meeting);
    }

    private async Task SummarizeAsync(Meeting meeting, Action<double> progress, Action<MeetingState> set, CancellationToken ct)
    {
        var writer = _host.Summarizer();
        if (writer is null || !writer.IsReady || meeting.Segments.Count == 0) return;
        set(MeetingState.Summarizing);
        try
        {
            meeting.Summary = await writer.SummarizeAsync(meeting, new Progress<double>(p => progress(0.9 * p)), ct);
            _host.Meetings.Save(meeting);
            if (meeting.TitleIsAuto)
            {
                var title = (await writer.TitleAsync(meeting, ct)).Trim();
                if (title.Length > 0) meeting.Title = title;
            }
            meeting.Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The text is kept; the summary can be written again later. The meeting says why.
            Trace.WriteLine($"Meetings: the summary failed ({ex.GetType().Name}: {ex.Message})");
            meeting.Error = ex is InvalidOperationException ? ex.Message
                : Loc.T("Sammanfattningen kunde inte skrivas.", "The summary couldn't be written.");
        }
        progress(1);
    }

    // ---- Naming after the meeting -------------------------------------------------------

    /// <summary>
    /// Names a speaker in a finished meeting (or the one being recorded). With
    /// <paramref name="remember"/> and Remember voices on, the voice is kept — while Mindtalk
    /// still has its voiceprint in memory (meetings recorded or imported since it started).
    /// </summary>
    public void NameSpeaker(Meeting meeting, string speakerId, string? name, bool remember)
    {
        if (_current is { } live && live.Meeting.Id == meeting.Id) { live.NameSpeaker(speakerId, name, remember); return; }
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (meeting.SpeakerById(speakerId) is not { } speaker) return;
        speaker.Name = name;
        speaker.IsMe = IsMe(name);
        speaker.IsBackground &= name is null;
        speaker.VoiceId = null;
        if (name is not null && remember && _host.Settings.RememberVoices && PrintOf(meeting.Id, speakerId) is { } print)
            speaker.VoiceId = _host.Voices.Remember(name, print).Id;
        _host.Meetings.Save(meeting);
    }

    /// Marks a speaker in a finished meeting as background sound (never named), or not.
    public void SetBackground(Meeting meeting, string speakerId, bool background)
    {
        if (_current is { } live && live.Meeting.Id == meeting.Id) { live.SetBackground(speakerId, background); return; }
        if (meeting.SpeakerById(speakerId) is not { } speaker) return;
        speaker.IsBackground = background;
        if (background) { speaker.Name = null; speaker.VoiceId = null; speaker.IsMe = false; }
        _host.Meetings.Save(meeting);
    }

    /// Whether naming this speaker can remember the voice now.
    public bool CanRemember(Meeting meeting, string speakerId) => PrintOf(meeting.Id, speakerId) is not null
        || (_current is { } live && live.Meeting.Id == meeting.Id && live.PrintOf(speakerId) is not null);

    // ---- Import -------------------------------------------------------------------------

    public bool CanImport(string path)
    {
        if (!File.Exists(path)) return false;
        if (ImportExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return true;
        try { return _host.Media.CanRead(path); }
        catch (Exception) { return false; }
    }

    public async Task<Meeting> ImportAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var meeting = new Meeting
        {
            Source = MeetingSource.Import,
            ImportedFrom = info.Name,
            Title = Path.GetFileNameWithoutExtension(info.Name),
            TitleIsAuto = true,
            Language = _host.Language(),
            Started = info.Exists ? info.LastWriteTime : DateTime.Now,
            State = MeetingState.Finishing,
        };
        _working[meeting.Id] = 0;
        _host.Meetings.Save(meeting);
        Changed?.Invoke();
        try
        {
            var audio = await Task.Run(() => _host.Media.Decode(path, new Progress<double>(p => { _working[meeting.Id] = 0.1 * p; Changed?.Invoke(); }), ct), ct);
            meeting.Duration = audio.Length / (double)FinalPass.SampleRate;
            // The recording's own time, if the file says when it ended.
            if (info.Exists) meeting.Started = info.LastWriteTime.AddSeconds(-meeting.Duration);
            // The sound is kept in the meeting's folder like a recorded one's.
            var wav = Path.Combine(_host.Meetings.FolderOf(meeting.Id), "audio.wav");
            await Task.Run(() => { using var writer = new WavWriter(wav); writer.Write(audio); }, ct);
            meeting.AudioFile = "audio.wav";
            _host.Meetings.Save(meeting);
            await FinishAsync(meeting, [], [], _ => { }, _ => { }, ct, audio, from: 0.1);
        }
        catch (OperationCanceledException)
        {
            _working.TryRemove(meeting.Id, out _);
            _host.Meetings.Delete(meeting.Id);
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Meetings: import failed ({ex})");
            meeting.State = MeetingState.Failed;
            meeting.Error = Loc.T("Mindtalk kunde inte läsa ljudet i filen.", "Mindtalk couldn’t read the sound in the file.");
            _working.TryRemove(meeting.Id, out _);
            _host.Meetings.Save(meeting);
            Changed?.Invoke();
        }
        return meeting;
    }

    // ---- Summarize again ----------------------------------------------------------------

    public async Task ResummarizeAsync(Guid meetingId, CancellationToken ct = default)
    {
        if (_host.Meetings.Get(meetingId) is not { } meeting || _working.ContainsKey(meetingId)) return;
        _working[meetingId] = 0;
        Changed?.Invoke();
        var was = meeting.State;
        try
        {
            await SummarizeAsync(meeting, p => { _working[meetingId] = p; Changed?.Invoke(); }, s => { meeting.State = s; _host.Meetings.Save(meeting); }, ct);
        }
        finally
        {
            meeting.State = was is MeetingState.Failed && meeting.Segments.Count == 0 ? MeetingState.Failed : MeetingState.Done;
            _host.Meetings.Save(meeting);
            _working.TryRemove(meetingId, out _);
            Changed?.Invoke();
        }
    }

    // ---- Calls --------------------------------------------------------------------------

    private static string Key(DetectedCall call) => $"{call.App}:{call.ProcessId}";

    private void OnCallStarted(DetectedCall call)
    {
        if (!_host.Settings.AskWhenCallStarts || _current is { State: MeetingState.Recording or MeetingState.Paused }) return;
        lock (_gate) if (!_announced.Add(Key(call))) return;
        CallStarted?.Invoke(call);
    }

    private void OnCallEnded(DetectedCall call)
    {
        bool announced;
        lock (_gate) announced = _announced.Remove(Key(call));
        if (announced) CallEnded?.Invoke(call);
    }

    /// The call going on now, if any (a meeting started by hand during a call hears it too).
    public DetectedCall? ActiveCall => _host.Calls?.Active.FirstOrDefault();

    // ---- Keeping audio --------------------------------------------------------------------

    /// Deletes audio older than Settings → Meetings → Keep audio allows. The text stays.
    public int ApplyRetention() => _host.Meetings.ApplyRetention(_host.Settings.KeepAudio, DateTime.Now);

    public void Dispose()
    {
        if (_host.Calls is { } calls)
        {
            calls.Started -= OnCallStarted;
            calls.Ended -= OnCallEnded;
        }
        (_analyzer as IDisposable)?.Dispose();
    }
}
