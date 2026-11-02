namespace Mindtalk.Core.Speech;

/// <summary>Minimal WAV reader (PCM 8/16/24/32-bit and 32-bit float) → 16 kHz mono floats. For the CLI and tests.</summary>
public static class WavFile
{
    public static float[] Read16kMono(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF/WAV file.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadUInt16();
                bits = reader.ReadUInt16();
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
                // Extensible: the real format is in the sub-format GUID's first two bytes.
                if (format == 0xFFFE) format = bits == 32 ? 3 : 1;
            }
            else if (id == "data")
            {
                data = reader.ReadBytes((int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position));
                break;
            }
            else reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
        if (data is null || channels == 0) throw new InvalidDataException("No audio data in the WAV file.");

        var bytesPerSample = bits / 8;
        var frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (i * channels + c) * bytesPerSample;
                sum += (format, bits) switch
                {
                    (1, 8) => (data[o] - 128) / 128.0,
                    (1, 16) => BitConverter.ToInt16(data, o) / 32768.0,
                    (1, 24) => ((data[o] | data[o + 1] << 8 | (sbyte)data[o + 2] << 16)) / 8388608.0,
                    (1, 32) => BitConverter.ToInt32(data, o) / 2147483648.0,
                    (3, 32) => BitConverter.ToSingle(data, o),
                    _ => throw new NotSupportedException($"WAV format {format} / {bits}-bit isn't supported."),
                };
            }
            mono[i] = (float)(sum / channels);
        }
        return rate == ParakeetEngine.SampleRate ? mono : Resample(mono, rate, ParakeetEngine.SampleRate);
    }

    /// Linear interpolation: plenty for speech.
    public static float[] Resample(float[] input, int from, int to)
    {
        if (input.Length == 0 || from == to) return input;
        var length = (int)((long)input.Length * to / from);
        var output = new float[length];
        var ratio = (double)from / to;
        for (var i = 0; i < length; i++)
        {
            var position = i * ratio;
            var index = (int)position;
            var fraction = (float)(position - index);
            var next = Math.Min(index + 1, input.Length - 1);
            output[i] = input[index] + (input[next] - input[index]) * fraction;
        }
        return output;
    }
}
