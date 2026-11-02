using SherpaOnnx;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>Who spoke from when to when, in seconds; speakers numbered from 0.</summary>
public readonly record struct SpeakerTurn(double Start, double End, int Speaker)
{
    public double Length => End - Start;
}

/// <summary>Keeps speakers apart: voiceprints for the live text, and the whole recording at the end.</summary>
public interface ISpeakerAnalyzer
{
    /// A voiceprint of the voice in these 16 kHz samples; null if it's too short to tell.
    float[]? Embed(float[] samples);
    /// Who speaks when, across the whole recording. <paramref name="progress"/> 0…1.
    IReadOnlyList<SpeakerTurn> Diarize(float[] samples, Action<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// sherpa-onnx's speaker diarization (pyannote segmentation 3.0 + CAM++ voiceprints +
/// clustering) and its embedding extractor. Thread-safe: one call at a time per model.
/// </summary>
public sealed class SherpaSpeakerAnalyzer : ISpeakerAnalyzer, IDisposable
{
    public const int SampleRate = 16_000;
    /// Under this, a voiceprint says more about the noise than the voice.
    public const double MinEmbedSeconds = 0.5;
    /// Cosine distance where the final clustering keeps two voices apart (tuned on two-speaker recordings).
    public const float ClusterThreshold = 0.5f;

    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly OfflineSpeakerDiarization _diarization;
    private readonly object _embedGate = new(), _diarizeGate = new();

    public SherpaSpeakerAnalyzer(string segmentationModel, string embeddingModel, float clusterThreshold = ClusterThreshold)
    {
        var threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var embedding = new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = threads, Provider = "cpu" };
        _extractor = new SpeakerEmbeddingExtractor(embedding);

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModel;
        config.Segmentation.NumThreads = threads;
        config.Segmentation.Provider = "cpu";
        config.Embedding = embedding;
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = clusterThreshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        _diarization = new OfflineSpeakerDiarization(config);
    }

    public static SherpaSpeakerAnalyzer? TryCreate(SpeakerModels models)
    {
        if (!models.IsInstalled) return null;
        try { return new SherpaSpeakerAnalyzer(models.SegmentationPath, models.EmbeddingPath); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"Speakers: couldn't load the models ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public int Dimension => _extractor.Dim;

    public float[]? Embed(float[] samples)
    {
        if (samples.Length < MinEmbedSeconds * SampleRate) return null;
        lock (_embedGate)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            stream.InputFinished();
            return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
        }
    }

    public IReadOnlyList<SpeakerTurn> Diarize(float[] samples, Action<double>? progress = null, CancellationToken ct = default)
    {
        if (samples.Length < SampleRate) return [];
        lock (_diarizeGate)
        {
            // The callback runs on this thread; returning non-zero stops the work.
            OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
            {
                if (total > 0) progress?.Invoke(done / (double)total);
                return ct.IsCancellationRequested ? 1 : 0;
            };
            var segments = _diarization.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            ct.ThrowIfCancellationRequested();
            return segments.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker)).OrderBy(t => t.Start).ToList();
        }
    }

    public void Dispose()
    {
        lock (_embedGate) _extractor.Dispose();
        lock (_diarizeGate) _diarization.Dispose();
    }
}
