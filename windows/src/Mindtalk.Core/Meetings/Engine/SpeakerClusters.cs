using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// Keeps voices apart as they're heard (the live text's "who is speaking"): each voiceprint
/// joins the most alike speaker so far, or starts a new one if none is alike enough — up to
/// <see cref="MaxSpeakers"/>, after which it joins the closest. Not thread-safe.
/// </summary>
public sealed class SpeakerClusters(float threshold = SpeakerClusters.DefaultThreshold, int maxSpeakers = SpeakerClusters.DefaultMax)
{
    /// Cosine similarity a voice needs to count as someone already heard (CAM++ gives 0.7–0.9
    /// for the same voice, under 0.4 for different ones).
    public const float DefaultThreshold = 0.5f;
    public const int DefaultMax = 8;

    private readonly List<Cluster> _clusters = [];

    public int MaxSpeakers => maxSpeakers;
    public int Count => _clusters.Count;

    private sealed class Cluster(float[] sum, double seconds)
    {
        public float[] Sum = sum;
        public double Seconds = seconds;
        public float[] Centroid => VoiceStore.Normalize(Sum);
    }

    /// <summary>The speaker (0…) this voiceprint belongs to; <paramref name="seconds"/> weighs it.</summary>
    public int Assign(float[] print, double seconds = 1)
    {
        var unit = VoiceStore.Normalize(print);
        var (best, score) = Closest(unit);
        if (best < 0 || (score < threshold && _clusters.Count < maxSpeakers))
        {
            _clusters.Add(new Cluster(Scale(unit, seconds), seconds));
            return _clusters.Count - 1;
        }
        var c = _clusters[best];
        // A running mean, weighted by how long each piece is.
        for (var i = 0; i < c.Sum.Length && i < unit.Length; i++) c.Sum[i] += (float)(unit[i] * seconds);
        c.Seconds += seconds;
        return best;
    }

    /// <summary>The speaker most alike, without changing anything (-1 when there's none yet).</summary>
    public (int Index, float Score) Closest(float[] print)
    {
        var best = -1;
        var score = float.MinValue;
        for (var i = 0; i < _clusters.Count; i++)
        {
            var s = VoiceStore.Cosine(_clusters[i].Sum, print);
            if (s > score) { score = s; best = i; }
        }
        return (best, score);
    }

    /// The speaker's voiceprint so far (unit length).
    public float[] Centroid(int index) => _clusters[index].Centroid;
    /// Seconds of speech heard from the speaker.
    public double Seconds(int index) => _clusters[index].Seconds;

    private static float[] Scale(float[] v, double by) => v.Select(x => (float)(x * by)).ToArray();
}
