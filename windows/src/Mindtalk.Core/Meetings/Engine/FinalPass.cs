using Mindtalk.Core.Dictation;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>What listening through the whole recording again gives: speakers, text, and each speaker's voiceprint.</summary>
public sealed record FinalResult(List<Speaker> Speakers, List<Segment> Segments, Dictionary<string, float[]> Prints);

/// <summary>
/// When a meeting ends (or a recording is imported): finds who speaks when across the whole
/// recording, writes the text again turn by turn, and carries the names over from the live
/// speakers. Without the speaker models, everything is one speaker.
/// </summary>
public sealed class FinalPass(ITranscriber transcriber, ISpeakerAnalyzer? speakers, Func<string, string> clean)
{
    public const int SampleRate = 16_000;
    /// Pieces sent to the speech model are at most this long, so dictation never waits long.
    public const double MaxPieceSeconds = 20;
    /// Of the progress: listening for speakers, then writing the text.
    private const double SpeakerShare = 0.35;
    /// Seconds of audio added around each piece sent to the speech model.
    public const double Margin = 0.3;

    public async Task<FinalResult> RunAsync(float[] audio, IReadOnlyList<Segment> live, IReadOnlyList<Speaker> liveSpeakers,
                                            IProgress<double>? progress = null, CancellationToken ct = default)
    {
        IReadOnlyList<SpeakerTurn> found = [];
        if (speakers is not null)
            found = await Task.Run(() => speakers.Diarize(audio, p => progress?.Report(SpeakerShare * p), ct), ct);
        var turns = Tidy(found);
        if (turns.Count == 0)
        {
            // Nobody told apart (no models, or nothing the segmentation took for speech):
            // one speaker, cut at pauses.
            var chunker = new SpeechChunker(MaxPieceSeconds);
            var chunks = chunker.Push(audio);
            if (chunker.Flush() is { } last) chunks.Add(last);
            turns = chunks.Select(c => new SpeakerTurn(c.Start, c.End, 0)).ToList();
        }
        progress?.Report(SpeakerShare);

        var (finalSpeakers, ids) = SpeakerMapping.Carry(turns, live, liveSpeakers);
        var pieces = turns.SelectMany(t => Split(audio, t)).ToList();
        var segments = new List<Segment>();
        var total = Math.Max(1, pieces.Sum(p => p.Length));
        double done = 0;
        foreach (var piece in pieces)
        {
            ct.ThrowIfCancellationRequested();
            // A little sound on either side: the speech model drops words cut off right at the edge.
            var raw = await transcriber.TranscribeAsync(Padded(Slice(audio, piece.Start - Margin, piece.End + Margin)), ct);
            var text = clean(raw).Trim();
            if (text.Length > 0) segments.Add(new Segment(piece.Start, piece.End, ids[piece.Speaker], text));
            done += piece.Length;
            progress?.Report(SpeakerShare + (1 - SpeakerShare) * done / total);
        }

        // Speakers who said nothing the speech model could write go.
        var talking = segments.Select(s => s.SpeakerId).ToHashSet();
        finalSpeakers.RemoveAll(s => !talking.Contains(s.Id));

        var prints = new Dictionary<string, float[]>();
        if (speakers is not null)
            foreach (var group in turns.GroupBy(t => t.Speaker))
            {
                // Up to ~20 s of the speaker's longest turns: a steady voiceprint.
                var longest = group.OrderByDescending(t => t.Length).ToList();
                var picked = new List<float>();
                foreach (var t in longest)
                {
                    if (picked.Count >= 20 * SampleRate) break;
                    picked.AddRange(Slice(audio, t.Start, t.End));
                }
                if (speakers.Embed(picked.ToArray()) is { } print && talking.Contains(ids[group.Key])) prints[ids[group.Key]] = print;
            }
        progress?.Report(1);
        return new FinalResult(finalSpeakers, segments, prints);
    }

    /// <summary>
    /// Overlapping turns are trimmed (overlapping speech would be written twice), and turns
    /// by the same speaker with only a short pause between are joined.
    /// </summary>
    public static List<SpeakerTurn> Tidy(IReadOnlyList<SpeakerTurn> turns, double join = 0.8, double minLength = 0.3)
    {
        var result = new List<SpeakerTurn>();
        foreach (var t in turns.OrderBy(t => t.Start))
        {
            var turn = t;
            if (result.Count > 0)
            {
                var prev = result[^1];
                if (turn.Speaker == prev.Speaker && turn.Start - prev.End < join)
                {
                    result[^1] = prev with { End = Math.Max(prev.End, turn.End) };
                    continue;
                }
                if (turn.Start < prev.End) turn = turn with { Start = prev.End };
            }
            if (turn.Length >= minLength) result.Add(turn);
        }
        return result;
    }

    /// Turns longer than <see cref="MaxPieceSeconds"/> are cut at pauses.
    public static IEnumerable<SpeakerTurn> Split(float[] audio, SpeakerTurn turn)
    {
        if (turn.Length <= MaxPieceSeconds) { yield return turn; yield break; }
        var chunker = new SpeechChunker(MaxPieceSeconds) { MinSpeechSeconds = 0.1 };
        var start = (long)(turn.Start * SampleRate);
        var chunks = chunker.Push(Slice(audio, turn.Start, turn.End));
        if (chunker.Flush() is { } last) chunks.Add(last);
        if (chunks.Count == 0)
        {
            // Speech too quiet for the pause finder (the segmentation model hears better): even pieces.
            var count = (int)Math.Ceiling(turn.Length / MaxPieceSeconds);
            for (var i = 0; i < count; i++)
                yield return new SpeakerTurn(turn.Start + i * turn.Length / count, turn.Start + (i + 1) * turn.Length / count, turn.Speaker);
            yield break;
        }
        foreach (var c in chunks)
            yield return new SpeakerTurn((start + c.StartSample) / (double)SampleRate, (start + c.StartSample + c.Samples.Length) / (double)SampleRate, turn.Speaker);
    }

    /// Half a second of silence on either side, as the speech model was trained on.
    public static float[] Padded(float[] samples)
    {
        var pad = SampleRate / 2;
        var result = new float[samples.Length + 2 * pad];
        samples.CopyTo(result, pad);
        return result;
    }

    public static float[] Slice(float[] audio, double start, double end)
    {
        var from = Math.Clamp((int)(start * SampleRate), 0, audio.Length);
        var to = Math.Clamp((int)(end * SampleRate), from, audio.Length);
        return audio[from..to];
    }
}
