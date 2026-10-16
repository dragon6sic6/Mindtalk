using Mindtalk.Core.Dictation;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Mindtalk.Platform;

public sealed record Microphone(string Id, string Name);

/// <summary>
/// Records from a WASAPI capture endpoint, only while asked to, and converts to 16 kHz
/// mono in memory. Nothing is written to disk.
/// </summary>
public sealed class WasapiRecorder : IRecorder, IDisposable
{
    public const int SampleRate = 16_000;
    /// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _gate = new();
    private readonly List<float> _samples = new(SampleRate * 30);
    private WasapiCapture? _capture;
    private WdlResampler? _resampler;
    private WaveFormat? _format;
    private bool _isFloat;
    private long _lastAudioTicks;
    private float[] _mono = [];
    private float[] _resampled = [];

    public event Action<float>? Level;
    public event Action? Interrupted;

    public TimeSpan SilentFor =>
        _capture is null ? TimeSpan.Zero : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAudioTicks));

    public static IReadOnlyList<Microphone> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new Microphone(d.ID, d.FriendlyName)).ToList();
    }

    public void Start(string? deviceId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);

        var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        var format = capture.WaveFormat;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        if (!_isFloat && format.BitsPerSample is not (16 or 24 or 32))
        {
            capture.Dispose();
            throw new NotSupportedException($"Unsupported microphone format: {format}.");
        }

        lock (_gate)
        {
            _samples.Clear();
            _format = format;
            _resampler = null;
            if (format.SampleRate != SampleRate)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(true);
                _resampler.SetRates(format.SampleRate, SampleRate);
            }
        }
        Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
        capture.DataAvailable += OnData;
        _capture = capture;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            _capture = null;
            capture.Dispose();
            throw;
        }
    }

    public float[] Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            try { capture.StopRecording(); } catch { /* already stopped (device gone) */ }
            capture.Dispose();
        }
        lock (_gate)
        {
            var result = _samples.ToArray();
            _samples.Clear();
            return result;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
        float peak;
        lock (_gate)
        {
            if (_format is null) return;
            var channels = _format.Channels;
            var bytesPerSample = _format.BitsPerSample / 8;
            var frames = e.BytesRecorded / (bytesPerSample * channels);
            if (_mono.Length < frames) _mono = new float[frames];

            double sumSquares = 0;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var o = (i * channels + c) * bytesPerSample;
                    sum += _isFloat ? BitConverter.ToSingle(e.Buffer, o)
                        : bytesPerSample switch
                        {
                            2 => BitConverter.ToInt16(e.Buffer, o) / 32768f,
                            3 => (e.Buffer[o] | e.Buffer[o + 1] << 8 | (sbyte)e.Buffer[o + 2] << 16) / 8388608f,
                            _ => BitConverter.ToInt32(e.Buffer, o) / 2147483648f,
                        };
                }
                var v = sum / channels;
                _mono[i] = v;
                sumSquares += v * v;
            }
            peak = (float)Math.Sqrt(sumSquares / Math.Max(frames, 1));

            if (_resampler is null)
            {
                for (var i = 0; i < frames; i++) _samples.Add(_mono[i]);
            }
            else
            {
                var wanted = _resampler.ResamplePrepare(frames, 1, out var inBuffer, out var inOffset);
                Array.Copy(_mono, 0, inBuffer, inOffset, Math.Min(wanted, frames));
                var maxOut = frames * SampleRate / _format.SampleRate + 16;
                if (_resampled.Length < maxOut) _resampled = new float[maxOut];
                var produced = _resampler.ResampleOut(_resampled, 0, frames, maxOut, 1);
                for (var i = 0; i < produced; i++) _samples.Add(_resampled[i]);
            }
        }
        // RMS of speech sits well below 1; scale so normal speaking fills the meter.
        Level?.Invoke(Math.Min(1f, peak * 4f));
    }

    public void Dispose() => Stop();
}
