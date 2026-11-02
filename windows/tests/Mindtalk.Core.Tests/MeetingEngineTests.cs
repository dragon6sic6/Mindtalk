using Mindtalk.Core.Dictation;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Meetings.Engine;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Tests;

public class SpeechChunkerTests
{
    private const int Rate = SpeechChunker.SampleRate;

    /// A tone (as loud as speech) for <paramref name="seconds"/>.
    private static float[] Tone(double seconds, float level = 0.2f) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => level * MathF.Sin(i * 2 * MathF.PI * 220 / Rate)).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static List<SpeechChunk> Run(SpeechChunker chunker, params float[][] parts)
    {
        var chunks = new List<SpeechChunk>();
        foreach (var p in parts) chunks.AddRange(chunker.Push(p));
        if (chunker.Flush() is { } last) chunks.Add(last);
        return chunks;
    }

    [Fact]
    public void Cuts_at_pauses_with_the_right_times()
    {
        var chunks = Run(new SpeechChunker(), Silence(1), Tone(2), Silence(1), Tone(1.5), Silence(1));
        Assert.Equal(2, chunks.Count);
        Assert.InRange(chunks[0].Start, 0.75, 1.0);    // a little silence kept before
        Assert.InRange(chunks[0].End, 3.0, 3.4);
        Assert.InRange(chunks[1].Start, 3.75, 4.0);
        Assert.InRange(chunks[1].End, 5.5, 5.9);
    }

    [Fact]
    public void A_short_breath_inside_a_sentence_does_not_cut()
    {
        var chunks = Run(new SpeechChunker(), Tone(1.5), Silence(0.3), Tone(1.5), Silence(1));
        Assert.Single(chunks);
    }

    [Fact]
    public void Never_longer_than_the_maximum()
    {
        var chunks = Run(new SpeechChunker(maxSeconds: 15), Tone(50), Silence(1));
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.End - c.Start <= 15.01, $"{c.End - c.Start} s"));
        // Back to back, nothing lost.
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].StartSample + chunks[i - 1].Samples.Length, chunks[i].StartSample);
    }

    [Fact]
    public void A_click_is_not_speech()
    {
        Assert.Empty(Run(new SpeechChunker(), Silence(1), Tone(0.06), Silence(1)));
    }

    [Fact]
    public void Steady_quiet_noise_is_not_speech()
    {
        var random = new Random(1);
        var noise = Enumerable.Range(0, 10 * Rate).Select(_ => (float)(random.NextDouble() - 0.5) * 0.004f).ToArray();
        Assert.Empty(Run(new SpeechChunker(), noise));
    }

    [Fact]
    public void Longer_stretches_need_shorter_pauses()
    {
        var chunker = new SpeechChunker(15);
        Assert.True(chunker.PauseNeeded(2) > chunker.PauseNeeded(8));
        Assert.True(chunker.PauseNeeded(8) > chunker.PauseNeeded(13));
    }

    [Fact]
    public void Pending_audio_is_the_stretch_being_spoken()
    {
        var chunker = new SpeechChunker();
        chunker.Push(Silence(1));
        Assert.False(chunker.InSpeech);
        chunker.Push(Tone(1));
        Assert.True(chunker.InSpeech);
        Assert.InRange(chunker.PendingSeconds, 1.0, 1.3);
        Assert.InRange(chunker.PendingStart / (double)Rate, 0.75, 1.0);
    }
}

public class SpeakerClustersTests
{
    private static float[] Voice(int seed, int dim = 192)
    {
        var r = new Random(seed);
        return VoiceStore.Normalize(Enumerable.Range(0, dim).Select(_ => (float)(r.NextDouble() * 2 - 1)).ToArray());
    }

    /// The same voice, a little different each time (as CAM++ gives ~0.7–0.9).
    private static float[] Near(float[] voice, int seed, float noise = 0.04f)
    {
        var r = new Random(seed);
        return voice.Select(v => v + (float)(r.NextDouble() * 2 - 1) * noise).ToArray();
    }

    [Fact]
    public void Keeps_two_voices_apart()
    {
        var a = Voice(1);
        var b = Voice(2);
        var clusters = new SpeakerClusters();
        var order = new[] { a, b, a, a, b, a, b, b };
        var assigned = order.Select((v, i) => clusters.Assign(Near(v, 100 + i), 3)).ToList();
        Assert.Equal(2, clusters.Count);
        Assert.Equal(new[] { 0, 1, 0, 0, 1, 0, 1, 1 }, assigned);
    }

    [Fact]
    public void At_most_eight_speakers()
    {
        var clusters = new SpeakerClusters(maxSpeakers: 8);
        for (var i = 0; i < 12; i++) Assert.InRange(clusters.Assign(Voice(i)), 0, 7);
        Assert.Equal(8, clusters.Count);
    }

    [Fact]
    public void The_centroid_follows_the_voice()
    {
        var a = Voice(5);
        var clusters = new SpeakerClusters();
        for (var i = 0; i < 5; i++) clusters.Assign(Near(a, i), 2);
        Assert.True(VoiceStore.Cosine(clusters.Centroid(0), a) > 0.95f);
        Assert.Equal(10, clusters.Seconds(0), 3);
        Assert.Equal((-1, float.MinValue), new SpeakerClusters().Closest(a));
    }
}

public class NameIntroductionsTests
{
    [Theory]
    [InlineData("Hi, my name is Anna.", "Anna")]
    [InlineData("Hello everyone, my name's Erik and I work on sales.", "Erik")]
    [InlineData("I'm Sara Lind, product manager.", "Sara Lind")]
    [InlineData("Hi, I am Tom. Nice to meet you.", "Tom")]
    [InlineData("Jag heter Örjan.", "Örjan")]
    [InlineData("Hej, jag heter Maria Svensson och jobbar med ekonomi.", "Maria Svensson")]
    [InlineData("Hej allihop, mitt namn är Per.", "Per")]
    [InlineData("Ja, jag heter anna och jag är projektledare.", "Anna")]
    [InlineData("Och jag är Karin.", "Karin")]
    public void Finds_the_name(string text, string name) => Assert.Equal(name, NameIntroductions.Find(text));

    [Theory]
    [InlineData("I'm fine, thanks.")]
    [InlineData("I'm not sure about that.")]
    [InlineData("I am going to take notes.")]
    [InlineData("Jag är inte säker.")]
    [InlineData("Jag är ansvarig för budgeten.")]
    [InlineData("Let's start with the release.")]
    [InlineData("i'm working on it")]
    public void Ignores_what_is_not_a_name(string text) => Assert.Null(NameIntroductions.Find(text));
}

public class SpeakerMappingTests
{
    [Fact]
    public void Names_carry_over_by_overlap()
    {
        // Live: S1 Anna 0–10, S2 Erik 10–20, S1 again 20–25 (live text split S1 wrongly into S3 at 25–30).
        var live = new List<Segment>
        {
            new(0, 10, "S1", "a"), new(10, 20, "S2", "b"), new(20, 25, "S1", "c"), new(25, 30, "S3", "d"),
        };
        var liveSpeakers = new List<Speaker>
        {
            new() { Id = "S1", Name = "Anna", IsMe = true }, new() { Id = "S2", Name = "Erik" }, new() { Id = "S3" },
        };
        // Final: speaker 0 is Anna (0–10, 20–30), speaker 1 is Erik (10–20).
        var turns = new List<SpeakerTurn> { new(0, 10, 0), new(10, 20, 1), new(20, 30, 0) };

        var (speakers, ids) = SpeakerMapping.Carry(turns, live, liveSpeakers);
        Assert.Equal("S1", ids[0]);
        Assert.Equal("S2", ids[1]);
        Assert.Equal(2, speakers.Count);
        Assert.Equal("Anna", speakers.Single(s => s.Id == "S1").Name);
        Assert.True(speakers.Single(s => s.Id == "S1").IsMe);
        Assert.Equal("Erik", speakers.Single(s => s.Id == "S2").Name);
    }

    [Fact]
    public void A_new_voice_gets_a_free_number()
    {
        var live = new List<Segment> { new(0, 10, "S1", "a") };
        var liveSpeakers = new List<Speaker> { new() { Id = "S1", Name = "Anna" } };
        var turns = new List<SpeakerTurn> { new(0, 9, 3), new(9, 15, 7) };
        var (speakers, ids) = SpeakerMapping.Carry(turns, live, liveSpeakers);
        Assert.Equal("S1", ids[3]);
        Assert.Equal("S2", ids[7]);
        Assert.Null(speakers.Single(s => s.Id == "S2").Name);
    }

    [Fact]
    public void Background_stays_background_and_one_live_speaker_maps_once()
    {
        var live = new List<Segment> { new(0, 10, "S1", "tv") };
        var liveSpeakers = new List<Speaker> { new() { Id = "S1", IsBackground = true } };
        var turns = new List<SpeakerTurn> { new(0, 6, 0), new(6, 10, 1) };
        var map = SpeakerMapping.Map(turns, live);
        Assert.Single(map);
        Assert.Equal("S1", map[0]);
        var (speakers, _) = SpeakerMapping.Carry(turns, live, liveSpeakers);
        Assert.True(speakers.Single(s => s.Id == "S1").IsBackground);
    }

    [Fact]
    public void Tidy_joins_and_trims_overlaps()
    {
        var turns = new List<SpeakerTurn> { new(0, 5, 0), new(5.3, 8, 0), new(7.5, 12, 1), new(11.9, 12.1, 0) };
        var tidy = FinalPass.Tidy(turns);
        Assert.Equal(2, tidy.Count);
        Assert.Equal(new SpeakerTurn(0, 8, 0), tidy[0]);
        Assert.Equal(new SpeakerTurn(8, 12, 1), tidy[1]);
    }
}

[Collection("Loc")]
public class MeetingExportTests
{
    private static Meeting Sample()
    {
        var m = new Meeting
        {
            Title = "Release planning",
            Started = new DateTime(2026, 11, 3, 9, 30, 0),
            Duration = 125,
            Speakers = [new() { Id = "S1", Name = "Anna" }, new() { Id = "S2" }, new() { Id = "S3", IsBackground = true }],
            Segments =
            [
                new(1.2, 4.5, "S1", "Let's ship on Friday."),
                new(4.6, 6.0, "S1", "Agreed?"),
                new(7.0, 9.25, "S2", "Yes, I'll test the import."),
                new(10, 12, "S3", "TV noise"),
                new(3661.5, 3663, "S2", "Late line."),
            ],
            Notes = [new(5, "Check the *installer*")],
            Marks = [new(7, MarkKind.Decision)],
            Summary = new MeetingSummary
            {
                Overview = "We planned the release.",
                Topics = [new("Release", [new("Ship Friday", 1.2)])],
                Decisions = [new("Ship on Friday", 1.2)],
                Todos = [new("Test the import", "Erik", 7)],
                Remember = [new("Customer meeting Tuesday")],
            },
        };
        return m;
    }

    [Fact]
    public void Srt_has_numbered_cues_with_speakers_and_no_background()
    {
        using var _ = Language.English();
        var srt = MeetingExport.Srt(Sample());
        Assert.StartsWith("1\n00:00:01,200 --> 00:00:04,500\nAnna: Let's ship on Friday.\n\n2\n", srt);
        Assert.Contains("00:00:07,000 --> 00:00:09,250\nSpeaker 2: Yes, I'll test the import.", srt);
        Assert.Contains("01:01:01,500 --> 01:01:03,000", srt);
        Assert.DoesNotContain("TV noise", srt);
        Assert.Equal(4, srt.Split("-->").Length - 1);
    }

    [Fact]
    public void Long_segments_are_split_into_shorter_cues()
    {
        using var _ = Language.English();
        var words = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"word{i}"));
        var m = new Meeting { Speakers = [new() { Id = "S1" }], Segments = [new(0, 20, "S1", words)] };
        var srt = MeetingExport.Srt(m);
        Assert.Equal(3, srt.Split("-->").Length - 1);
        Assert.Contains("word40", srt);
        Assert.EndsWith("00:00:20,000\n", srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[^1].Split('\n')[1] + "\n");
    }

    [Fact]
    public void Srt_time_format() => Assert.Equal("01:02:03,045", MeetingExport.SrtTime(3723.045));

    [Fact]
    public void Markdown_in_english()
    {
        using var _ = Language.English();
        var md = MeetingExport.Markdown(Sample());
        Assert.StartsWith("# Release planning\n\n*Tuesday, November 3, 2026 · 9:30–9:32 · 2 min · ", md);
        Assert.Contains("## Overview\n\nWe planned the release.", md);
        Assert.Contains("## What you talked about\n\n### Release (0:01)\n\n- Ship Friday (0:01)", md);
        Assert.Contains("## Decisions\n\n- Ship on Friday (0:01)", md);
        Assert.Contains("## To do\n\n- **Erik:** Test the import (0:07)", md);
        Assert.Contains("## Worth remembering\n\n- Customer meeting Tuesday\n", md);
        Assert.Contains("## Notes\n\n- Check the \\*installer\\* (0:05)\n- Decision (0:07)", md);
        // A speaker's lines in a row are one paragraph.
        Assert.Contains("## Transcript\n\n**Anna** · 0:01\nLet's ship on Friday. Agreed?\n\n**Speaker 2** · 0:07\n", md);
        Assert.DoesNotContain("TV noise", md);
    }

    [Fact]
    public void Markdown_in_swedish()
    {
        using var _ = Language.Swedish();
        var md = MeetingExport.Markdown(Sample());
        Assert.Contains("*Tisdag 3 november 2026 · 9:30–9:32 · 2 min · ", md);
        foreach (var heading in new[] { "## Översikt", "## Det här pratade ni om", "## Beslut", "## Att göra", "## Värt att komma ihåg", "## Anteckningar", "## Utskrift" })
            Assert.Contains(heading, md);
        Assert.Contains("**Talare 2** · 0:07", md);
    }

    [Fact]
    public void Plain_text_and_word_and_pdf_are_written()
    {
        using var _ = Language.English();
        var text = MeetingExport.Text(Sample());
        Assert.Contains("[0:01] Anna: Let's ship on Friday. Agreed?", text);
        Assert.Contains("• Erik: Test the import (0:07)", text);
        var docx = MeetingExport.Word(Sample());
        Assert.True(docx.Length > 1000);
        Assert.Equal((byte)'P', docx[0]);   // a zip
        if (OperatingSystem.IsWindows())
        {
            var pdf = MeetingExport.Pdf(Sample());
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        }
    }

    [Fact]
    public void File_names_are_safe()
    {
        var m = new Meeting { Title = "Q3: plan/review?" };
        Assert.Equal("Q3- plan-review-.docx", MeetingExport.FileName(m, ExportFormat.Word));
    }

    [Fact]
    public void Fallback_titles()
    {
        var tuesday = new DateTime(2026, 11, 3, 9, 30, 0);
        using (Language.Swedish()) Assert.Equal("Möte, tisdag 9:30", MeetingEngine.FallbackTitle(tuesday));
        using (Language.English()) Assert.Equal("Meeting, Tuesday 9:30", MeetingEngine.FallbackTitle(tuesday));
        using (Language.English()) Assert.Equal("Teams call, Tuesday 9:30", MeetingEngine.FallbackTitle(tuesday, "Teams"));
    }
}

public class WavWriterTests
{
    [Fact]
    public void Writes_a_wav_that_reads_back_even_before_it_is_closed()
    {
        using var folder = new TempFolder();
        var path = folder.File("audio.wav");
        var samples = Enumerable.Range(0, 40_000).Select(i => MathF.Sin(i / 10f) * 0.5f).ToArray();
        var writer = new WavWriter(path);
        writer.Write(samples);
        // The header is kept up to date every second: a crash now still leaves a readable file.
        File.Copy(path, folder.File("copy.wav"));
        var midway = WavFile.Read16kMono(folder.File("copy.wav"));
        Assert.True(midway.Length >= 32_000);
        writer.Dispose();
        var back = WavFile.Read16kMono(path);
        Assert.Equal(samples.Length, back.Length);
        Assert.InRange(back[1234] - samples[1234], -0.001f, 0.001f);
    }
}

/// <summary>The engine with everything faked: no models, no devices.</summary>
public class MeetingEngineImportTests
{
    private sealed class FakeTranscriber : ITranscriber
    {
        public int Calls;
        public double LongestSeconds;
        public Readiness Readiness => Readiness.Ready;
        public string ModelTitle => "Test";
        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LongestSeconds = Math.Max(LongestSeconds, samples.Length / 16000.0);
            return Task.FromResult("eh, hello mind talk");
        }
    }

    /// Two "voices": loud stretches are speaker 0 before 10 s, speaker 1 after.
    private sealed class FakeAnalyzer : ISpeakerAnalyzer
    {
        public float[]? Embed(float[] samples) => [1, 0, 0];
        public IReadOnlyList<SpeakerTurn> Diarize(float[] samples, Action<double>? progress = null, CancellationToken ct = default)
        {
            progress?.Invoke(1);
            return [new(1, 4, 0), new(5, 8, 0), new(11, 14, 1), new(14.2, 50, 1)];
        }
    }

    private sealed class FakeMedia(float[] audio) : IMeetingMedia
    {
        public bool CanRead(string path) => path.EndsWith(".xyz");
        public float[] Decode(string path, IProgress<double>? progress = null, CancellationToken ct = default) => audio;
        public bool EncodeM4a(string wavPath, string m4aPath) { File.WriteAllBytes(m4aPath, [1, 2, 3]); return true; }
    }

    private sealed class FakeSummarizer : IMeetingSummarizer
    {
        public string WriterName => "Fake";
        public bool IsReady => true;
        public Task<MeetingSummary> SummarizeAsync(Meeting meeting, IProgress<double>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new MeetingSummary { Overview = $"{meeting.Segments.Count} lines", Writer = WriterName });
        public Task<string> TitleAsync(Meeting meeting, CancellationToken ct = default) => Task.FromResult("Planning");
    }

    private static (MeetingEngine Engine, MeetingStore Store, FakeTranscriber Speech) Make(TempFolder folder, IMeetingSummarizer? summarizer, ISpeakerAnalyzer? analyzer)
    {
        var audio = new float[50 * 16000];
        var speech = new FakeTranscriber();
        var store = new MeetingStore(folder.File("Meetings"));
        var engine = new MeetingEngine(new MeetingEngineHost
        {
            Meetings = store,
            Voices = new VoiceStore(folder.File("voices.json")),
            Settings = new AppSettings(),
            Transcriber = speech,
            Vocabulary = t => t.Replace("mind talk", "Mindtalk"),
            SpeakerModels = new SpeakerModels(folder.File("speakers")),
            OpenAudio = _ => throw new InvalidOperationException("no devices in tests"),
            Media = new FakeMedia(audio),
            Summarizer = () => summarizer,
            Analyzer = analyzer,
        });
        return (engine, store, speech);
    }

    [Fact]
    public async Task An_import_becomes_a_meeting_with_speakers_text_and_summary()
    {
        using var folder = new TempFolder();
        var file = folder.File("standup.mp3");
        File.WriteAllBytes(file, [0]);
        var (engine, store, speech) = Make(folder, new FakeSummarizer(), new FakeAnalyzer());

        Assert.True(engine.CanImport(file));
        Assert.False(engine.CanImport(folder.File("missing.mp3")));
        var meeting = await engine.ImportAsync(file);

        Assert.Equal(MeetingState.Done, meeting.State);
        Assert.Equal(MeetingSource.Import, meeting.Source);
        Assert.Equal("standup.mp3", meeting.ImportedFrom);
        Assert.Equal("Planning", meeting.Title);
        Assert.Equal("audio.m4a", meeting.AudioFile);
        Assert.False(File.Exists(Path.Combine(store.FolderOf(meeting.Id), "audio.wav")));
        Assert.Equal(50, meeting.Duration, 1);
        Assert.Equal(2, meeting.Speakers.Count);
        Assert.All(meeting.Segments, s => Assert.Equal("Hello Mindtalk", s.Text));   // fillers and vocabulary
        Assert.Equal(new[] { "S1", "S2" }, meeting.Segments.Select(s => s.SpeakerId).Distinct());
        // Speaker 0's two turns are joined; speaker 1's 36 s turn is cut into pieces of at most 20 s.
        Assert.True(speech.LongestSeconds <= FinalPass.MaxPieceSeconds + 2 * FinalPass.Margin + 1.01);
        Assert.Equal($"{meeting.Segments.Count} lines", meeting.Summary?.Overview);
        Assert.Empty(engine.Working);
        Assert.Same(meeting, store.Get(meeting.Id));
    }

    [Fact]
    public async Task Without_a_writer_or_speaker_models_it_is_one_speaker_and_no_summary()
    {
        using var folder = new TempFolder();
        var file = folder.File("call.wav");
        File.WriteAllBytes(file, [0]);
        var (engine, _, _) = Make(folder, summarizer: null, analyzer: null);
        Assert.False(engine.SpeakerModelReady);

        var meeting = await engine.ImportAsync(file);
        Assert.Equal(MeetingState.Done, meeting.State);
        Assert.Null(meeting.Summary);
        Assert.Equal("call", meeting.Title);
        // Silence: nothing to write, still a meeting.
        Assert.Empty(meeting.Segments);
    }

    [Fact]
    public async Task Resummarize_writes_the_summary_again()
    {
        using var folder = new TempFolder();
        var summarizer = new FakeSummarizer();
        var (engine, store, _) = Make(folder, summarizer, new FakeAnalyzer());
        var meeting = new Meeting { Title = "Old", TitleIsAuto = false, State = MeetingState.Done, Speakers = [new() { Id = "S1" }], Segments = [new(0, 1, "S1", "Hi")] };
        store.Save(meeting);
        await engine.ResummarizeAsync(meeting.Id);
        Assert.Equal("1 lines", store.Get(meeting.Id)!.Summary!.Overview);
        Assert.Equal("Old", store.Get(meeting.Id)!.Title);   // you named it: kept
        Assert.Equal(MeetingState.Done, store.Get(meeting.Id)!.State);
    }

    [Fact]
    public void Naming_a_speaker_afterwards_remembers_the_voice_when_asked()
    {
        using var folder = new TempFolder();
        var (engine, store, _) = Make(folder, null, null);
        var meeting = new Meeting { Speakers = [new() { Id = "S1" }], Segments = [new(0, 1, "S1", "Hi")], State = MeetingState.Done };
        store.Save(meeting);
        engine.NameSpeaker(meeting, "S1", " Anna ", remember: true);
        Assert.Equal("Anna", meeting.SpeakerById("S1")!.Name);
        Assert.Null(meeting.SpeakerById("S1")!.VoiceId);   // no voiceprint kept for it, and Remember voices is off
        engine.SetBackground(meeting, "S1", true);
        Assert.Null(meeting.SpeakerById("S1")!.Name);
        Assert.True(meeting.SpeakerById("S1")!.IsBackground);
    }
}
