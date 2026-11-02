using System.Diagnostics;
using System.Threading.Channels;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// The meeting being recorded: the sound goes to audio.wav as it comes in and is cut into
/// stretches of speech at pauses; each stretch is written by the speech model and given to
/// the voice it sounds like. The words being spoken now show as <see cref="Partial"/>, about a
/// second behind. Events come from background threads.
/// </summary>
public sealed class LiveMeeting : ILiveMeeting
{
    /// Live stretches are at most this long, so the speech model is never held for long.
    public const double MaxChunkSeconds = 15;
    /// The round of names ends by itself after this much recorded time.
    public static readonly TimeSpan RoundOfNamesLength = TimeSpan.FromMinutes(2);
    /// A remembered voice this close is taken without asking; between this and VoiceStore.MatchThreshold, Mindtalk asks.
    public const float CertainMatch = 0.75f;
    /// Stretches at least this long shape the voices; shorter ones are only compared.
    public const double AssignSeconds = 1.2;

    private readonly MeetingEngine _engine;
    private readonly MeetingEngineHost _host;
    private readonly IMeetingAudio _audio;
    private readonly ISpeakerAnalyzer? _analyzer;
    private readonly object _gate = new();          // the meeting's lists
    private readonly object _audioGate = new();     // the wav and the chunker
    private readonly SpeechChunker _chunker = new(MaxChunkSeconds);
    private readonly SpeakerClusters _clusters = new();
    private readonly Channel<SpeechChunk> _chunks = Channel.CreateUnbounded<SpeechChunk>(new() { SingleReader = false });
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _worker;
    private readonly Timer _saver;
    private readonly HashSet<(string Speaker, Guid Voice)> _declined = [];
    private readonly Dictionary<string, double> _checkedAt = [];
    private WavWriter? _wav;
    private long _recorded;
    private volatile MeetingState _state;
    private bool _round;
    private string? _lastSpeaker;
    private int _pendingVersion, _partialVersion;
    private long _lastChangedTicks;
    private Task? _stopping;
    private volatile bool _dirty;

    internal LiveMeeting(MeetingEngine engine, MeetingEngineHost host, Meeting meeting, IMeetingAudio audio, ISpeakerAnalyzer? analyzer, bool roundOfNames)
    {
        _engine = engine;
        _host = host;
        _audio = audio;
        _analyzer = analyzer;
        Meeting = meeting;
        _round = roundOfNames;
        _state = MeetingState.Recording;

        Directory.CreateDirectory(host.Meetings.FolderOf(meeting.Id));
        _wav = new WavWriter(Path.Combine(host.Meetings.FolderOf(meeting.Id), "audio.wav"));
        meeting.AudioFile = "audio.wav";
        meeting.State = MeetingState.Recording;
        host.Meetings.Save(meeting);

        _audio.Samples += OnSamples;
        _audio.Interrupted += OnInterrupted;
        _worker = Task.Run(WorkAsync);
        // Saved regularly, so a crash keeps nearly everything.
        _saver = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        try { _audio.Start(); }
        catch
        {
            Discard();
            throw;
        }
    }

    public Meeting Meeting { get; }
    public MeetingState State => _state;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _recorded) / (double)SpeechChunker.SampleRate);
    public string? Partial { get; private set; }
    public string? PartialSpeakerId { get; private set; }
    public float Level => _state == MeetingState.Recording ? _audio.Level : 0;
    public bool HearsCall => _state == MeetingState.Recording && _audio.HearsCall;
    public bool InRoundOfNames => _round;
    public VoiceSuggestion? Suggestion { get; private set; }
    public double FinishProgress { get; internal set; }

    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    // ---- Sound in ------------------------------------------------------------------

    private void OnSamples(float[] samples)
    {
        if (_state != MeetingState.Recording) return;
        List<SpeechChunk> done;
        lock (_audioGate)
        {
            if (_wav is null) return;
            _wav.Write(samples);
            Interlocked.Add(ref _recorded, samples.Length);
            done = _chunker.Push(samples);
            if (_chunker.InSpeech) _pendingVersion++;
        }
        foreach (var chunk in done) _chunks.Writer.TryWrite(chunk);
        if (_round && Elapsed >= RoundOfNamesLength) EndRoundOfNames();
        // The waveform and the clock: about ten times a second is plenty.
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChangedTicks), now) >= TimeSpan.FromMilliseconds(100))
        {
            Interlocked.Exchange(ref _lastChangedTicks, now);
            Changed?.Invoke();
        }
    }

    /// The mic went away: pause, so nothing is lost and you can carry on once it's back.
    private void OnInterrupted()
    {
        Trace.WriteLine("LiveMeeting: the microphone went away; pausing");
        Pause();
    }

    // ---- Writing --------------------------------------------------------------------

    private async Task WorkAsync()
    {
        var reader = _chunks.Reader;
        while (true)
        {
            SpeechChunk? chunk = null;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(0.8));
                if (await reader.WaitToReadAsync(wait.Token)) reader.TryRead(out chunk);
                else break;   // completed and empty
            }
            catch (OperationCanceledException) when (!_cancel.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }

            try
            {
                if (chunk is not null) await WriteAsync(chunk);
                else if (reader.Count == 0) await UpdatePartialAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.WriteLine($"LiveMeeting: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task<string> TranscribeAsync(float[] samples)
    {
        try { return await _host.Transcriber.TranscribeAsync(FinalPass.Padded(samples), _cancel.Token); }
        catch (InvalidOperationException) { return ""; }   // the speech model isn't loaded (yet)
    }

    private string Clean(string text) => _host.VocabularyQuiet(TextCleanup.Apply(text, fillers: true, commands: false)).Trim();

    /// The words being spoken now, written again about once a second.
    private async Task UpdatePartialAsync()
    {
        float[] pending;
        int version;
        lock (_audioGate)
        {
            version = _pendingVersion;
            if (version == _partialVersion || !_chunker.InSpeech || _chunker.PendingSeconds < 0.6) return;
            pending = _chunker.PendingSamples();
        }
        var text = Clean(await TranscribeAsync(pending));
        _partialVersion = version;
        string? speaker = _lastSpeaker;
        if (_analyzer is not null && pending.Length >= 2 * SpeechChunker.SampleRate && _analyzer.Embed(pending) is { } print)
        {
            lock (_gate)
            {
                var (index, score) = _clusters.Closest(VoiceStore.Normalize(print));
                speaker = index >= 0 && score >= SpeakerClusters.DefaultThreshold ? SpeakerId(index) : null;
            }
        }
        lock (_audioGate)
        {
            // Ended meanwhile: the stretch is being written as a segment instead.
            if (!_chunker.InSpeech) return;
        }
        Partial = text.Length > 0 ? text : null;
        PartialSpeakerId = speaker;
        Changed?.Invoke();
    }

    private async Task WriteAsync(SpeechChunk chunk)
    {
        var text = Clean(await TranscribeAsync(chunk.Samples));
        var seconds = chunk.Samples.Length / (double)SpeechChunker.SampleRate;
        string id;
        int? cluster = null;
        var print = _analyzer?.Embed(chunk.Samples);
        lock (_gate)
        {
            if (print is not null && seconds >= AssignSeconds)
            {
                cluster = _clusters.Assign(print, seconds);
                id = SpeakerId(cluster.Value);
            }
            // Short ("Yes."): whoever it sounds most like, without letting so little sound change
            // anyone's voiceprint — or, if it's like nobody yet, the one who spoke last.
            else if (print is not null && _clusters.Closest(VoiceStore.Normalize(print)) is { Index: >= 0 } near && near.Score >= SpeakerClusters.DefaultThreshold)
                id = SpeakerId(near.Index);
            else id = _lastSpeaker ?? "S1";
            if (Meeting.SpeakerById(id) is null) Meeting.Speakers = [.. Meeting.Speakers, new Speaker { Id = id }];
            if (text.Length > 0)
            {
                Meeting.Segments = [.. Meeting.Segments, new Segment(chunk.Start, chunk.End, id, text)];
                _lastSpeaker = id;
            }
            Meeting.Duration = Elapsed.TotalSeconds;
        }
        Partial = null;
        PartialSpeakerId = null;
        _dirty = true;

        if (text.Length > 0 && _round) ListenForName(id, text);
        if (cluster is { } c) RecognizeVoice(id, c);
        Changed?.Invoke();
    }

    private static string SpeakerId(int cluster) => $"S{cluster + 1}";

    // ---- Names ----------------------------------------------------------------------

    /// In the round of names, a speaker's first words: "Hi, my name is Anna."
    private void ListenForName(string id, string text)
    {
        lock (_gate)
        {
            var speaker = Meeting.SpeakerById(id);
            if (speaker is null || speaker.Name is not null || speaker.IsBackground) return;
            // Only the first few things they say.
            if (Meeting.Segments.Count(s => s.SpeakerId == id) > 3) return;
        }
        if (NameIntroductions.Find(text) is { } name)
            NameSpeaker(id, name, remember: _host.Settings.RememberVoices);
    }

    /// With Remember voices on: is this a voice you've named before?
    private void RecognizeVoice(string id, int cluster)
    {
        if (!_host.Settings.RememberVoices) return;
        float[] centroid;
        Guid[] taken;
        lock (_gate)
        {
            var speaker = Meeting.SpeakerById(id);
            if (speaker is null || speaker.Name is not null || speaker.IsBackground) return;
            // Asked again as more of the voice is heard: after 3, 8 and 20 seconds.
            var heard = _clusters.Seconds(cluster);
            var last = _checkedAt.GetValueOrDefault(id);
            var due = new[] { 3.0, 8.0, 20.0 }.FirstOrDefault(t => heard >= t && last < t);
            if (due == 0) return;
            _checkedAt[id] = due;
            centroid = _clusters.Centroid(cluster);
            taken = Meeting.Speakers.Select(s => s.VoiceId).OfType<Guid>().ToArray();
        }
        if (_host.Voices.Match(centroid, VoiceStore.MatchThreshold, taken) is not { } match) return;
        if (match.Score >= CertainMatch)
        {
            lock (_gate)
            {
                if (Meeting.SpeakerById(id) is not { } speaker) return;
                speaker.Name = match.Voice.Name;
                speaker.VoiceId = match.Voice.Id;
                speaker.IsMe = IsMe(match.Voice.Name);
            }
            _dirty = true;
        }
        else if (Suggestion is null && !_declined.Contains((id, match.Voice.Id)))
            Suggestion = new VoiceSuggestion(id, match.Voice.Id, match.Voice.Name, match.Score);
    }

    private bool IsMe(string? name) =>
        name is not null && _host.Settings.UserName is { Length: > 0 } me && string.Equals(name.Trim(), me.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// The speaker's voiceprint: from the whole recording once it's been listened through, else what's been heard so far.
    internal float[]? PrintOf(string id)
    {
        if (_engine.PrintOf(Meeting.Id, id) is { } final) return final;
        lock (_gate)
        {
            if (!id.StartsWith('S') || !int.TryParse(id[1..], out var n) || n < 1 || n > _clusters.Count) return null;
            return _clusters.Centroid(n - 1);
        }
    }

    public void NameSpeaker(string speakerId, string? name, bool remember)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        lock (_gate)
        {
            if (Meeting.SpeakerById(speakerId) is not { } speaker) return;
            speaker.Name = name;
            speaker.IsMe = IsMe(name);
            speaker.IsBackground &= name is null;
            if (name is null) speaker.VoiceId = null;
        }
        if (name is not null && remember && _host.Settings.RememberVoices && PrintOf(speakerId) is { } print)
        {
            var voice = _host.Voices.Remember(name, print);
            lock (_gate) if (Meeting.SpeakerById(speakerId) is { } s) s.VoiceId = voice.Id;
        }
        if (Suggestion?.SpeakerId == speakerId) Suggestion = null;
        Save();
    }

    public void SetBackground(string speakerId, bool background)
    {
        lock (_gate)
        {
            if (Meeting.SpeakerById(speakerId) is not { } speaker) return;
            speaker.IsBackground = background;
            // A TV or a radio is never named.
            if (background) { speaker.Name = null; speaker.VoiceId = null; speaker.IsMe = false; }
        }
        if (background && Suggestion?.SpeakerId == speakerId) Suggestion = null;
        Save();
    }

    public void AcceptSuggestion(bool accept)
    {
        if (Suggestion is not { } suggestion) return;
        Suggestion = null;
        if (accept)
        {
            lock (_gate)
            {
                if (Meeting.SpeakerById(suggestion.SpeakerId) is { } speaker)
                {
                    speaker.Name = suggestion.Name;
                    speaker.VoiceId = suggestion.VoiceId;
                    speaker.IsMe = IsMe(suggestion.Name);
                }
            }
            // It was them: the voiceprint learns this voice too.
            if (PrintOf(suggestion.SpeakerId) is { } print) _host.Voices.Remember(suggestion.Name, print);
        }
        else lock (_gate) _declined.Add((suggestion.SpeakerId, suggestion.VoiceId));
        Save();
    }

    public void EndRoundOfNames()
    {
        if (!_round) return;
        _round = false;
        Changed?.Invoke();
    }

    // ---- Notes and marks ------------------------------------------------------------

    public void AddNote(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (_gate) Meeting.Notes = [.. Meeting.Notes, new MeetingNote(Elapsed.TotalSeconds, text)];
        Save();
    }

    public void AddMark(MarkKind kind)
    {
        lock (_gate) Meeting.Marks = [.. Meeting.Marks, new MeetingMark(Elapsed.TotalSeconds, kind)];
        Save();
    }

    // ---- Pause, stop, discard -------------------------------------------------------

    public void Pause()
    {
        if (_state != MeetingState.Recording) return;
        _state = MeetingState.Paused;
        Meeting.State = MeetingState.Paused;
        // Let go of the mic while paused: nothing is heard, not even in memory.
        try { _audio.Stop(); } catch (Exception ex) { Trace.WriteLine($"LiveMeeting: pause: {ex.Message}"); }
        lock (_audioGate)
        {
            if (_chunker.Flush() is { } chunk) _chunks.Writer.TryWrite(chunk);
        }
        Partial = null;
        Save();
    }

    public void Resume()
    {
        if (_state != MeetingState.Paused) return;
        try { _audio.Start(); }
        catch (Exception ex)
        {
            Trace.WriteLine($"LiveMeeting: resume: {ex.Message}");
            Changed?.Invoke();
            return;
        }
        _state = MeetingState.Recording;
        Meeting.State = MeetingState.Recording;
        Save();
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            if (_state is not (MeetingState.Recording or MeetingState.Paused)) return Task.CompletedTask;
            _stopping = StopCoreAsync();
            return _stopping;
        }
    }

    private async Task StopCoreAsync()
    {
        _state = MeetingState.Finishing;
        Meeting.State = MeetingState.Finishing;
        _round = false;
        Suggestion = null;
        FinishProgress = 0;
        Changed?.Invoke();
        await Task.Run(() =>
        {
            try { _audio.Stop(); } catch (Exception ex) { Trace.WriteLine($"LiveMeeting: stop: {ex.Message}"); }
            _audio.Samples -= OnSamples;
            _audio.Interrupted -= OnInterrupted;
            _audio.Dispose();
        });
        lock (_audioGate)
        {
            if (_chunker.Flush() is { } chunk) _chunks.Writer.TryWrite(chunk);
            _wav?.Dispose();
            _wav = null;
        }
        _chunks.Writer.TryComplete();
        // The last words are written before the whole recording is listened through again.
        await _worker;
        _saver.Dispose();
        Partial = null;
        PartialSpeakerId = null;
        Meeting.Duration = Elapsed.TotalSeconds;
        Save();

        List<Segment> live;
        List<Speaker> speakers;
        lock (_gate)
        {
            live = [.. Meeting.Segments];
            speakers = Meeting.Speakers.Select(s => new Speaker { Id = s.Id, Name = s.Name, IsBackground = s.IsBackground, VoiceId = s.VoiceId, IsMe = s.IsMe }).ToList();
        }
        await _engine.FinishAsync(Meeting, live, speakers, p => { FinishProgress = p; Changed?.Invoke(); },
                                  state => { _state = state; Changed?.Invoke(); }, CancellationToken.None);
    }

    public void Discard()
    {
        _state = MeetingState.Failed;
        _cancel.Cancel();
        _chunks.Writer.TryComplete();
        _saver.Dispose();
        try { _audio.Stop(); } catch { /* going anyway */ }
        _audio.Samples -= OnSamples;
        _audio.Dispose();
        lock (_audioGate)
        {
            _wav?.Dispose();
            _wav = null;
        }
        _engine.Discarded(this);
        Changed?.Invoke();
    }

    private void Save()
    {
        _dirty = false;
        if (_state == MeetingState.Failed && _cancel.IsCancellationRequested) return;   // discarded
        lock (_gate)
        {
            Meeting.Duration = Math.Max(Meeting.Duration, Elapsed.TotalSeconds);
            _host.Meetings.Save(Meeting);
        }
        Changed?.Invoke();
    }

    private void SaveIfDirty()
    {
        if (_state is MeetingState.Recording or MeetingState.Paused && (_dirty || _state == MeetingState.Recording)) Save();
    }
}
