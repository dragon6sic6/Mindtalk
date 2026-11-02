using System.Diagnostics;
using Mindtalk.Core.Meetings.Engine;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Mindtalk.Platform.Meetings;

/// <summary>
/// Recordings in and out through Windows' Media Foundation: reads mp3, m4a, wav, mp4, mov,
/// wma, aac, flac (and whatever else Windows has a decoder for) as 16 kHz mono, and keeps a
/// meeting's audio as AAC in .m4a.
/// </summary>
public sealed class MeetingMedia : IMeetingMedia
{
    public const int SampleRate = 16_000;
    /// The AAC encoder takes 44.1 or 48 kHz; speech at 96 kbit/s mono is plenty.
    private const int AacRate = 48_000, AacBitrate = 96_000;

    static MeetingMedia() => MediaFoundationApi.Startup();

    public bool CanRead(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            return reader.WaveFormat.Channels > 0 && reader.TotalTime > TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MeetingMedia: can't read {Path.GetExtension(path)} ({ex.GetType().Name})");
            return false;
        }
    }

    public float[] Decode(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var reader = new MediaFoundationReader(path);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1) samples = new MonoMix(samples);
        if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);

        var total = Math.Max(1, reader.TotalTime.TotalSeconds * SampleRate);
        var result = new List<float>((int)Math.Min(int.MaxValue / 2, total + SampleRate));
        var buffer = new float[SampleRate];
        int n;
        var lastReport = 0;
        while ((n = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < n; i++) result.Add(buffer[i]);
            if (result.Count - lastReport >= SampleRate * 30)
            {
                lastReport = result.Count;
                progress?.Report(Math.Min(1, result.Count / total));
            }
        }
        progress?.Report(1);
        return [.. result];
    }

    public bool EncodeM4a(string wavPath, string m4aPath)
    {
        var temp = m4aPath + ".tmp.m4a";
        try
        {
            using (var reader = new WaveFileReader(wavPath))
            {
                // 16 kHz mono → 48 kHz mono 16-bit for the AAC encoder.
                var resampled = new WdlResamplingSampleProvider(reader.ToSampleProvider(), AacRate).ToWaveProvider16();
                MediaFoundationEncoder.EncodeToAac(resampled, temp, AacBitrate);
            }
            File.Move(temp, m4aPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MeetingMedia: AAC encoding failed ({ex.GetType().Name}: {ex.Message})");
            try { File.Delete(temp); } catch (IOException) { }
            return false;
        }
    }

    /// Any number of channels down to one (NAudio's StereoToMono only takes two).
    private sealed class MonoMix(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer, 0, needed) / _channels;
            for (var i = 0; i < read; i++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _buffer[i * _channels + c];
                buffer[offset + i] = sum / _channels;
            }
            return read;
        }
    }
}
