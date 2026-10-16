using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// <remarks>
/// The capture loop is our own rather than NAudio's WasapiCapture: when a device vanishes,
/// that one's thread can throw past its own error handling (stopping a dead audio client
/// fails), which would take the app down instead of reporting the interruption.
/// </remarks>
/// <param name="keepSamples">False for the level preview in the microphone picker — nothing is kept.</param>
public sealed class WasapiRecorder(bool keepSamples = true) : IRecorder, IDisposable
{
    public const int SampleRate = 16_000;
    /// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _gate = new();
    private readonly List<float> _samples = keepSamples ? new(SampleRate * 30) : [];
    private volatile Capture? _capture;
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
        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
        {
            device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
        }

        Capture capture;
        try { capture = new Capture(device, OnData, OnStopped); }
        catch
        {
            device.Dispose();
            throw;
        }
        var format = capture.Format;
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
            if (keepSamples && format.SampleRate != SampleRate)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(true);
                _resampler.SetRates(format.SampleRate, SampleRate);
            }
            _capture = capture;
        }
        Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
        try
        {
            capture.Start();
        }
        catch
        {
            lock (_gate) _capture = null;
            capture.Dispose();
            throw;
        }
    }

    public float[] Stop()
    {
        Capture? capture;
        // Cleared first: a stop we asked for is never reported as an interruption.
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        // Hands over what's still buffered (the end of the last word), then lets go.
        capture?.Dispose();
        lock (_gate)
        {
            var result = _samples.ToArray();
            _samples.Clear();
            return result;
        }
    }

    /// <summary>
    /// Capture ended without being asked to: the mic was unplugged, a Bluetooth headset
    /// dropped, the endpoint was invalidated. Reported once per recording (a capture ends
    /// only once), and never from inside <see cref="Stop"/> or on the capture thread — a
    /// listener will want to stop, which waits for that thread.
    /// </summary>
    private void OnStopped(Capture capture, Exception? error)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(capture, _capture)) return;
        }
        Trace.WriteLine($"WasapiRecorder: capture stopped by itself ({error?.GetType().Name ?? "no error"} 0x{error?.HResult ?? 0:X8})");
        Task.Run(() =>
        {
            if (ReferenceEquals(capture, _capture)) Interrupted?.Invoke();
        });
    }

    private void OnData(byte[] buffer, int bytes)
    {
        if (bytes == 0) return;
        Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
        float rms;
        lock (_gate)
        {
            if (_format is null) return;
            var channels = _format.Channels;
            var bytesPerSample = _format.BitsPerSample / 8;
            var frames = bytes / (bytesPerSample * channels);
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
            rms = (float)Math.Sqrt(sumSquares / Math.Max(frames, 1));

            if (!keepSamples) { /* level preview only */ }
            else if (_resampler is null)
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
        // The Mac's scale: normal speech fills the meter, and the dictation's "heard
        // nothing" threshold and the waveform are calibrated to it.
        Level?.Invoke(Math.Min(1f, rms * 12f));
    }

    public void Dispose() => Stop();

    /// <summary>
    /// One shared-mode, event-driven capture stream in the device's own format, read on a
    /// thread of its own. Any failure there ends the stream and is reported, never thrown.
    /// </summary>
    private sealed class Capture : IDisposable
    {
        /// 100 ns units: a 100 ms buffer, delivered every device period (~10 ms).
        private const long BufferDuration = 100 * 10_000;

        private readonly MMDevice _device;
        private readonly AudioClient _client;
        private readonly Action<byte[], int> _data;
        private readonly Action<Capture, Exception?> _stopped;
        private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
        private readonly Thread _thread;
        private volatile bool _stopping;
        private byte[] _buffer = [];

        public WaveFormat Format { get; }

        public Capture(MMDevice device, Action<byte[], int> data, Action<Capture, Exception?> stopped)
        {
            _device = device;
            _data = data;
            _stopped = stopped;
            _client = device.AudioClient;
            Format = _client.MixFormat;
            _thread = new Thread(Run) { IsBackground = true, Name = "Mindtalk microphone" };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        public void Start()
        {
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, BufferDuration, 0, Format, Guid.Empty);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
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
                    // Also wakes without an event: a dead device then fails the read below
                    // instead of just going quiet.
                    _ready.WaitOne(100);
                    Drain(reader);
                    if (_stopping) break;
                }
            }
            catch (Exception ex) { error = ex; }
            try { _client.Stop(); } catch { /* the device is gone */ }
            if (!_stopping) _stopped(this, error);
        }

        private void Drain(AudioCaptureClient reader)
        {
            var blockAlign = Format.BlockAlign;
            while (reader.GetNextPacketSize() > 0)
            {
                var pointer = reader.GetBuffer(out var frames, out var flags);
                var bytes = frames * blockAlign;
                if (_buffer.Length < bytes) _buffer = new byte[bytes];
                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buffer, 0, bytes);
                else Marshal.Copy(pointer, _buffer, 0, bytes);
                reader.ReleaseBuffer(frames);
                _data(_buffer, bytes);
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _ready.Set();
            if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(1));
            if (_thread.IsAlive) return;   // stuck in the driver: leave its objects to it
            _client.Dispose();
            _device.Dispose();
            _ready.Dispose();
        }
    }
}
