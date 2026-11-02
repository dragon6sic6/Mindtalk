namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// Writes 16 kHz mono 16-bit WAV as the audio comes in. The header's sizes are brought up to
/// date every second of audio, so if Mindtalk or the PC stops mid-meeting, what was recorded
/// is still a playable file.
/// </summary>
public sealed class WavWriter : IDisposable
{
    public const int SampleRate = 16_000;
    private readonly FileStream _file;
    private readonly object _gate = new();
    private long _samples, _sinceHeader;
    private byte[] _buffer = [];

    public WavWriter(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        WriteHeader();
    }

    public string Path { get; }
    public long Samples { get { lock (_gate) return _samples; } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_buffer.Length < samples.Length * 2) _buffer = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                _buffer[2 * i] = (byte)v;
                _buffer[2 * i + 1] = (byte)(v >> 8);
            }
            _file.Write(_buffer, 0, samples.Length * 2);
            _samples += samples.Length;
            _sinceHeader += samples.Length;
            if (_sinceHeader >= SampleRate)
            {
                _sinceHeader = 0;
                WriteHeader();
            }
        }
    }

    private void WriteHeader()
    {
        var data = _samples * 2;
        var end = _file.Position;
        _file.Position = 0;
        using (var w = new BinaryWriter(_file, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write((int)Math.Min(int.MaxValue, 36 + data));
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);            // PCM
            w.Write((short)1);            // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);      // bytes per second
            w.Write((short)2);            // block align
            w.Write((short)16);           // bits
            w.Write("data"u8);
            w.Write((int)Math.Min(int.MaxValue, data));
        }
        _file.Position = Math.Max(end, 44);
        _file.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_file.CanWrite) return;
            WriteHeader();
            _file.Dispose();
        }
    }
}
