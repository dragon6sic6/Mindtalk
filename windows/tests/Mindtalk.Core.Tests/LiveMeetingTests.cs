using Mindtalk.Core.Dictation;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Meetings.Engine;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Tests;

/// <summary>A live meeting with fake sound: two "voices" told apart by loudness.</summary>
public class LiveMeetingTests
{
    private const int Rate = 16000;

    private static float[] Tone(double seconds, float level) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => level * MathF.Sin(i * 2 * MathF.PI * 220 / Rate)).ToArray();

    private static bool Loud(float[] s) => s.Length > 0 && s.Max(Math.Abs) > 0.3f;

    private sealed class FakeAudio : IMeetingAudio
    {
        public event Action<float[]>? Samples;
        public event Action? Interrupted { add { } remove { } }
        public float Level => 0.5f;
        public bool HearsCall => false;
        public bool Running;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Push(float[] samples) { foreach (var block in samples.Chunk(1600)) Samples?.Invoke(block); }
        public void Dispose() => Running = false;
    }

    /// Quiet voice: Anna introduces herself; loud voice: Erik, in Swedish.
    private sealed class FakeSpeech : ITranscriber
    {
        public Readiness Readiness => Readiness.Ready;
        public string ModelTitle => "Test";
        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default) =>
            Task.FromResult(Loud(samples) ? "Hej, jag heter Erik." : "Hi, my name is Anna.");
    }

    private sealed class FakeVoices : ISpeakerAnalyzer
    {
        public float[]? Embed(float[] samples) => Loud(samples) ? [0, 1, 0] : [1, 0, 0];

        public IReadOnlyList<SpeakerTurn> Diarize(float[] samples, Action<double>? progress = null, CancellationToken ct = default)
        {
            // Frames of 0.1 s grouped by loudness (numbered the other way round from the live speakers).
            var turns = new List<SpeakerTurn>();
            for (var f = 0; f < samples.Length / 1600; f++)
            {
                var peak = samples[(f * 1600)..((f + 1) * 1600)].Max(Math.Abs);
                if (peak < 0.05f) continue;
                var speaker = peak > 0.3f ? 0 : 1;
                if (turns.Count > 0 && turns[^1].Speaker == speaker && Math.Abs(turns[^1].End - f * 0.1) < 0.01)
                    turns[^1] = turns[^1] with { End = (f + 1) * 0.1 };
                else turns.Add(new SpeakerTurn(f * 0.1, (f + 1) * 0.1, speaker));
            }
            return turns;
        }
    }

    private sealed class NoMedia : IMeetingMedia
    {
        public bool CanRead(string path) => false;
        public float[] Decode(string path, IProgress<double>? progress = null, CancellationToken ct = default) => [];
        public bool EncodeM4a(string wavPath, string m4aPath) => false;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static MeetingEngine Engine(TempFolder folder, FakeAudio audio, MeetingStore store, VoiceStore voices, AppSettings settings) =>
        new(new MeetingEngineHost
        {
            Meetings = store,
            Voices = voices,
            Settings = settings,
            Transcriber = new FakeSpeech(),
            SpeakerModels = new SpeakerModels(folder.File("speakers")),
            OpenAudio = _ => audio,
            Media = new NoMedia(),
            Analyzer = new FakeVoices(),
        });

    [Fact]
    public async Task Round_of_names_live_speakers_and_the_final_pass()
    {
        using var folder = new TempFolder();
        var audio = new FakeAudio();
        var store = new MeetingStore(folder.File("Meetings"));
        var voices = new VoiceStore(folder.File("voices.json"));
        var engine = Engine(folder, audio, store, voices, new AppSettings { RememberVoices = true, UserName = "Anna" });

        var live = engine.Start(new MeetingStart(MeetingSource.Room, RoundOfNames: true));
        Assert.True(audio.Running);
        Assert.True(live.InRoundOfNames);
        Assert.Same(live, engine.Start(new MeetingStart(MeetingSource.Room, false)));   // one at a time

        audio.Push(new float[Rate / 2]);
        audio.Push(Tone(2, 0.1f));
        audio.Push(new float[Rate]);
        audio.Push(Tone(2, 0.5f));
        audio.Push(new float[Rate]);
        await Until(() => live.Meeting.Segments.Count == 2);

        var anna = live.Meeting.SpeakerById(live.Meeting.Segments[0].SpeakerId)!;
        var erik = live.Meeting.SpeakerById(live.Meeting.Segments[1].SpeakerId)!;
        Assert.NotEqual(anna.Id, erik.Id);
        await Until(() => anna.Name == "Anna" && erik.Name == "Erik");
        Assert.True(anna.IsMe);
        // Remember voices is on: both voices are kept (voiceprints only).
        Assert.Equal(new[] { "Anna", "Erik" }, voices.All.Select(v => v.Name).Order());

        live.AddNote("Ship Friday");
        live.AddMark(MarkKind.Decision);
        live.Pause();
        Assert.Equal(MeetingState.Paused, live.State);
        Assert.False(audio.Running);
        live.Resume();
        Assert.True(audio.Running);

        await live.StopAsync();
        var m = store.Get(live.Meeting.Id)!;
        Assert.Equal(MeetingState.Done, m.State);
        Assert.Equal("audio.wav", m.AudioFile);   // encoding failed: the WAV is kept
        Assert.NotNull(store.AudioPath(m));
        Assert.Equal(2, m.Segments.Count);
        // The final pass found the speakers afresh; the names carried over by time.
        Assert.Equal("Anna", m.SpeakerById(m.Segments[0].SpeakerId)!.Name);
        Assert.Equal("Erik", m.SpeakerById(m.Segments[1].SpeakerId)!.Name);
        Assert.Single(m.Notes);
        Assert.Single(m.Marks);
        Assert.Null(m.Summary);
        Assert.InRange(m.Duration, 6.4, 7);
    }

    [Fact]
    public async Task A_remembered_voice_is_recognized_and_background_is_never_named()
    {
        using var folder = new TempFolder();
        var audio = new FakeAudio();
        var store = new MeetingStore(folder.File("Meetings"));
        var voices = new VoiceStore(folder.File("voices.json"));
        voices.Remember("Sara", [1, 0, 0]);
        // Close to the loud voice, but not certain: Mindtalk asks.
        voices.Remember("Olle", [0, 0.7f, 0.7f]);
        var engine = Engine(folder, audio, store, voices, new AppSettings { RememberVoices = true });

        var live = engine.Start(new MeetingStart(MeetingSource.Room, RoundOfNames: false));
        audio.Push(Tone(3.5, 0.1f));
        audio.Push(new float[Rate]);
        await Until(() => live.Meeting.Segments.Count == 1);
        var sara = live.Meeting.Speakers.Single();
        await Until(() => sara.Name == "Sara");
        Assert.NotNull(sara.VoiceId);

        audio.Push(Tone(3.5, 0.5f));
        audio.Push(new float[Rate]);
        await Until(() => live.Suggestion is not null);
        Assert.Equal("Olle", live.Suggestion!.Name);
        var other = live.Suggestion.SpeakerId;
        live.SetBackground(other, true);
        Assert.Null(live.Suggestion);
        Assert.Null(live.Meeting.SpeakerById(other)!.Name);

        live.Discard();
        Assert.Null(store.Get(live.Meeting.Id));
        Assert.Null(engine.Current);
    }
}
