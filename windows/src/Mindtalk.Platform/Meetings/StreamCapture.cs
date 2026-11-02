using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Mindtalk.Platform.Meetings;

/// <summary>
/// One WASAPI capture stream, read on a thread of its own and handed on as 16 kHz mono, for
/// as long as it runs (a meeting can last hours, so nothing is kept here). Used for the
/// microphone and for a call app's sound. Any failure ends the stream and is reported, never
/// thrown — as in <see cref="WasapiRecorder"/>.
/// </summary>
internal sealed class StreamCapture : IDisposable
{
    public const int SampleRate = 16_000;
    /// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AudioClient _client;
    private readonly IDisposable? _owner;
    private readonly WaveFormat _format;
    private readonly bool _isFloat;
    private readonly Action<float[], float> _data;
    private readonly Action<Exception?> _stopped;
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private readonly Thread _thread;
    private readonly WdlResampler? _resampler;
    private volatile bool _stopping;
    private byte[] _buffer = [];
    private float[] _mono = [];

    /// <param name="data">16 kHz mono samples and their level (RMS), on the capture thread.</param>
    /// <param name="stopped">The stream ended without being asked to (a device that vanished).</param>
    /// <param name="owner">Disposed with the stream (the device the client came from).</param>
    public StreamCapture(AudioClient client, WaveFormat format, AudioClientStreamFlags flags, string name,
                         Action<float[], float> data, Action<Exception?> stopped, IDisposable? owner = null)
    {
        _client = client;
        _owner = owner;
        _format = format;
        _data = data;
        _stopped = stopped;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        if (!_isFloat && format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException($"Unsupported format: {format}.");
        if (format.SampleRate != SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(format.SampleRate, SampleRate);
        }
        // 100 ms of buffer, delivered every device period.
        _client.Initialize(AudioClientShareMode.Shared, flags | AudioClientStreamFlags.EventCallback, 100 * 10_000, 0, format, Guid.Empty);
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public static StreamCapture ForDevice(MMDevice device, string name, Action<float[], float> data, Action<Exception?> stopped)
    {
        var client = device.AudioClient;
        return new StreamCapture(client, client.MixFormat, AudioClientStreamFlags.None, name, data, stopped, device);
    }

    public void Start()
    {
        var reader = _client.AudioCaptureClient;
        _client.Start();
        _thread.Start(reader);
    }

    private void Run(object? state)
    {
        var reader = (AudioCaptureClient)state!;
        Exception? error = null;
        try
        {
            while (true)
            {
                // Also wakes without an event: a dead device then fails the read below.
                _ready.WaitOne(100);
                Drain(reader);
                if (_stopping) break;
            }
        }
        catch (Exception ex) { error = ex; }
        try { _client.Stop(); } catch { /* the device is gone */ }
        if (!_stopping)
        {
            Trace.WriteLine($"StreamCapture {_thread.Name}: stopped by itself ({error?.GetType().Name} 0x{error?.HResult ?? 0:X8})");
            Task.Run(() => _stopped(error));
        }
    }

    private void Drain(AudioCaptureClient reader)
    {
        var blockAlign = _format.BlockAlign;
        while (reader.GetNextPacketSize() > 0)
        {
            var pointer = reader.GetBuffer(out var frames, out var flags);
            var bytes = frames * blockAlign;
            if (_buffer.Length < bytes) _buffer = new byte[bytes];
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buffer, 0, bytes);
            else Marshal.Copy(pointer, _buffer, 0, bytes);
            reader.ReleaseBuffer(frames);
            Deliver(_buffer, frames);
        }
    }

    private void Deliver(byte[] buffer, int frames)
    {
        if (frames == 0) return;
        var channels = _format.Channels;
        var bytesPerSample = _format.BitsPerSample / 8;
        if (_mono.Length < frames) _mono = new float[frames];
        double sumSquares = 0;
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (i * channels + c) * bytesPerSample;
                sum += _isFloat ? BitConverter.ToSingle(buffer, o)
                    : bytesPerSample switch
                    {
                        2 => BitConverter.ToInt16(buffer, o) / 32768f,
                        3 => (buffer[o] | buffer[o + 1] << 8 | (sbyte)buffer[o + 2] << 16) / 8388608f,
                        _ => BitConverter.ToInt32(buffer, o) / 2147483648f,
                    };
            }
            var v = sum / channels;
            _mono[i] = v;
            sumSquares += v * v;
        }
        var rms = (float)Math.Sqrt(sumSquares / frames);

        float[] output;
        if (_resampler is null) output = _mono[..frames];
        else
        {
            var wanted = _resampler.ResamplePrepare(frames, 1, out var inBuffer, out var inOffset);
            Array.Copy(_mono, 0, inBuffer, inOffset, Math.Min(wanted, frames));
            var maxOut = frames * SampleRate / _format.SampleRate + 16;
            var resampled = new float[maxOut];
            var produced = _resampler.ResampleOut(resampled, 0, frames, maxOut, 1);
            output = resampled[..produced];
        }
        if (output.Length > 0) _data(output, rms);
    }

    public void Dispose()
    {
        _stopping = true;
        _ready.Set();
        if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(1));
        if (_thread.IsAlive) return;   // stuck in the driver: leave its objects to it
        _client.Dispose();
        _owner?.Dispose();
        _ready.Dispose();
    }
}
