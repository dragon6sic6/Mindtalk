using System.Diagnostics;
using Mindtalk.Core.Dictation;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mindtalk.Platform;

/// <summary>
/// Soft start/done sounds. The Mac plays its system sounds (Tink, Pop, Glass) at a quarter
/// volume; those can't ship here, so three small sounds in the same spirit are made in code —
/// rendered once, in the output's own format, and played without waiting.
/// </summary>
public sealed class Cues(Func<bool> enabled) : ICues
{
    public enum Sound { Tink, Pop, Glass }

    private const float Volume = 0.25f;
    private static readonly object Gate = new();
    private static readonly Dictionary<(Sound, int, int), byte[]> Rendered = [];

    /// Dictation started.
    public void Start() => Play(Sound.Tink);
    /// The text was pasted.
    public void Done() => Play(Sound.Pop);
    /// The first dictation during the introduction.
    public void Celebrate() => Play(Sound.Glass);

    public void Play(Sound sound)
    {
        if (!enabled()) return;
        Task.Run(() => PlayNow(sound));
    }

    private static void PlayNow(Sound sound)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return;
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            // The device's own rate and channels: shared mode then takes it as it is.
            var mix = device.AudioClient.MixFormat;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
            var data = Render(sound, format.SampleRate, format.Channels);

            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
            void Release()
            {
                output.Dispose();
                device.Dispose();
            }
            // Raised on the playback thread, which disposing waits for — so let go from elsewhere.
            output.PlaybackStopped += (_, _) => Task.Run(Release);
            try
            {
                output.Init(new RawSourceWaveStream(data, 0, data.Length, format));
                output.Play();
            }
            catch
            {
                Release();
                throw;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Cues: couldn't play {sound} ({ex.GetType().Name})");
        }
    }

    private static byte[] Render(Sound sound, int rate, int channels)
    {
        lock (Gate)
        {
            if (Rendered.TryGetValue((sound, rate, channels), out var cached)) return cached;
            var mono = Synthesize(sound, rate);
            var bytes = new byte[mono.Length * channels * sizeof(float)];
            for (var i = 0; i < mono.Length; i++)
                for (var c = 0; c < channels; c++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((i * channels + c) * sizeof(float)), mono[i] * Volume);
            Rendered[(sound, rate, channels)] = bytes;
            return bytes;
        }
    }

    /// <summary>The sound itself, peaking a little under full scale, with a short tail of silence
    /// so the end isn't clipped when playback stops.</summary>
    internal static float[] Synthesize(Sound sound, int rate)
    {
        // (seconds, partials as (frequency, amplitude, decay time constant), pitch drop)
        var (length, partials, drop) = sound switch
        {
            // A small high tap: a bright fundamental and a faint inharmonic overtone.
            Sound.Tink => (0.12, new[] { (2093.0, 1.0, 0.030), (5440.0, 0.18, 0.012) }, 0.0),
            // A round, low "pop": a sine that falls in pitch and dies away quickly.
            Sound.Pop => (0.08, new[] { (520.0, 1.0, 0.022), (1040.0, 0.12, 0.010) }, 0.45),
            // A glass bell: two partials, the upper one fading sooner.
            _ => (0.6, new[] { (1318.5, 1.0, 0.200), (3296.3, 0.35, 0.090) }, 0.0),
        };
        var count = (int)(length * rate);
        var tail = (int)(0.05 * rate);
        var samples = new float[count + tail];
        var phases = new double[partials.Length];
        var attack = 0.004 * rate;
        var release = 0.010 * rate;
        var peak = 0f;
        for (var n = 0; n < count; n++)
        {
            var t = n / (double)rate;
            // The pitch drop glides down over the sound's length.
            var glide = 1 - drop * Math.Min(1, t / length);
            double value = 0;
            for (var p = 0; p < partials.Length; p++)
            {
                var (frequency, amplitude, decay) = partials[p];
                phases[p] += 2 * Math.PI * frequency * glide / rate;
                value += amplitude * Math.Exp(-t / decay) * Math.Sin(phases[p]);
            }
            // A few milliseconds in and out: no clicks.
            value *= Math.Min(1, n / attack) * Math.Min(1, (count - n) / release);
            samples[n] = (float)value;
            peak = Math.Max(peak, Math.Abs(samples[n]));
        }
        if (peak > 0)
            for (var n = 0; n < count; n++) samples[n] *= 0.8f / peak;
        return samples;
    }
}
