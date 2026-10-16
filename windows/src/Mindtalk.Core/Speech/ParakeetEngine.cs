using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mindtalk.Core.Speech;

/// <summary>
/// Parakeet TDT 0.6B (NeMo FastConformer + token-and-duration transducer) on ONNX Runtime.
/// Port of the `nemo-conformer-tdt` pipeline in onnx-asr: log-mel preprocessor → encoder →
/// greedy TDT decoding with the decoder/joint network.
/// </summary>
public sealed class ParakeetEngine : IDisposable
{
    public const int SampleRate = 16_000;

    private readonly InferenceSession _preprocessor, _encoder, _decoderJoint;
    private readonly string[] _vocab;
    private readonly int _blank, _vocabSize, _maxTokensPerStep;
    private readonly int _stateLayers, _stateHidden;
    private readonly bool _targetsAreInt32;
    private readonly object _gate = new();

    public ParakeetEngine(string directory, SpeechModel model)
    {
        string P(string name) => Path.Combine(directory, name);

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        _preprocessor = new InferenceSession(P("nemo128.onnx"), new SessionOptions { IntraOpNumThreads = 1 });
        _encoder = new InferenceSession(P(model.Encoder), options);
        _decoderJoint = new InferenceSession(P(model.DecoderJoint), new SessionOptions { IntraOpNumThreads = 1 });

        _maxTokensPerStep = 10;
        if (File.Exists(P("config.json")))
        {
            using var config = JsonDocument.Parse(File.ReadAllText(P("config.json")));
            if (config.RootElement.TryGetProperty("max_tokens_per_step", out var m)) _maxTokensPerStep = m.GetInt32();
        }

        // vocab.txt: "<token> <id>" per line; "▁" marks a word start.
        var vocab = new SortedDictionary<int, string>();
        foreach (var line in File.ReadLines(P("vocab.txt")))
        {
            var l = line.TrimEnd('\r', '\n');
            if (l.Length == 0) continue;
            var space = l.LastIndexOf(' ');
            vocab[int.Parse(l[(space + 1)..])] = l[..space].Replace('▁', ' ');
        }
        _vocab = new string[vocab.Keys.Max() + 1];
        foreach (var (id, token) in vocab) _vocab[id] = token;
        _vocabSize = _vocab.Length;
        _blank = Array.IndexOf(_vocab, "<blk>");
        if (_blank < 0) throw new InvalidDataException("vocab.txt has no <blk> token.");

        var inputs = _decoderJoint.InputMetadata;
        _stateLayers = inputs["input_states_1"].Dimensions[0];
        _stateHidden = inputs["input_states_1"].Dimensions[2];
        _targetsAreInt32 = inputs["targets"].ElementDataType == TensorElementType.Int32;
    }

    /// <summary>Transcribes 16 kHz mono samples in [-1, 1].</summary>
    public string Transcribe(ReadOnlySpan<float> samples)
    {
        // Too short for the encoder to see anything (under 0.1 s).
        if (samples.Length < SampleRate / 10) return "";
        var waveform = samples.ToArray();
        lock (_gate)
        {
            var (features, featureLength) = Preprocess(waveform);
            var (encoded, frames, width) = Encode(features, featureLength);
            return Decode(encoded, frames, width);
        }
    }

    private (DenseTensor<float> features, long length) Preprocess(float[] waveform)
    {
        var input = new DenseTensor<float>(waveform, [1, waveform.Length]);
        var lengths = new DenseTensor<long>(new[] { (long)waveform.Length }, [1]);
        using var results = _preprocessor.Run(
        [
            NamedOnnxValue.CreateFromTensor("waveforms", input),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", lengths),
        ]);
        var features = results.First(r => r.Name == "features").AsTensor<float>();
        var length = results.First(r => r.Name == "features_lens").AsTensor<long>()[0];
        return (features.ToDenseTensor(), length);
    }

    /// <returns>The encoder output laid out as [frames, width] (row-major).</returns>
    private (float[] data, int frames, int width) Encode(DenseTensor<float> features, long featureLength)
    {
        using var results = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("audio_signal", features),
            NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new[] { featureLength }, [1])),
        ]);
        var outputs = results.First(r => r.Name == "outputs").AsTensor<float>();   // [1, width, frames]
        var length = (int)results.First(r => r.Name == "encoded_lengths").AsTensor<long>()[0];
        var width = outputs.Dimensions[1];
        var available = outputs.Dimensions[2];
        var frames = Math.Min(length, available);

        var source = outputs.ToArray();
        var data = new float[frames * width];
        for (var d = 0; d < width; d++)
            for (var t = 0; t < frames; t++)
                data[t * width + d] = source[d * available + t];
        return (data, frames, width);
    }

    private string Decode(float[] encoded, int frames, int width)
    {
        var state1 = new DenseTensor<float>(new float[_stateLayers * _stateHidden], [_stateLayers, 1, _stateHidden]);
        var state2 = new DenseTensor<float>(new float[_stateLayers * _stateHidden], [_stateLayers, 1, _stateHidden]);
        var tokens = new List<int>();
        var frame = new float[width];
        var t = 0;
        var emitted = 0;

        while (t < frames)
        {
            Array.Copy(encoded, t * width, frame, 0, width);
            var last = tokens.Count > 0 ? tokens[^1] : _blank;
            using var results = _decoderJoint.Run(
            [
                NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>((float[])frame.Clone(), [1, width, 1])),
                _targetsAreInt32
                    ? NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<int>(new[] { last }, [1, 1]))
                    : NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<long>(new[] { (long)last }, [1, 1])),
                _targetsAreInt32
                    ? NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<int>(new[] { 1 }, [1]))
                    : NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<long>(new[] { 1L }, [1])),
                NamedOnnxValue.CreateFromTensor("input_states_1", state1),
                NamedOnnxValue.CreateFromTensor("input_states_2", state2),
            ]);

            var output = results.First(r => r.Name == "outputs").AsTensor<float>().ToArray();
            var token = ArgMax(output, 0, _vocabSize);
            // TDT: the tail of the output scores how many frames to skip.
            var step = output.Length > _vocabSize ? ArgMax(output, _vocabSize, output.Length - _vocabSize) : 0;

            if (token != _blank)
            {
                state1 = results.First(r => r.Name == "output_states_1").AsTensor<float>().ToDenseTensor();
                state2 = results.First(r => r.Name == "output_states_2").AsTensor<float>().ToDenseTensor();
                tokens.Add(token);
                emitted++;
            }

            if (step > 0)
            {
                t += step;
                emitted = 0;
            }
            else if (token == _blank || emitted == _maxTokensPerStep)
            {
                t++;
                emitted = 0;
            }
        }
        return Detokenize(tokens);
    }

    private static int ArgMax(float[] values, int offset, int count)
    {
        var best = 0;
        for (var i = 1; i < count; i++)
            if (values[offset + i] > values[offset + best]) best = i;
        return best;
    }

    // "▁" (now a space) starts a word. Drops the leading space and any space that isn't
    // followed by a word character, as onnx-asr does.
    private static readonly Regex SpacePattern = new(@"\A\s|\s\B|(\s)\b", RegexOptions.CultureInvariant);

    private string Detokenize(List<int> ids)
    {
        var text = new StringBuilder();
        foreach (var id in ids) text.Append(_vocab[id]);
        return SpacePattern.Replace(text.ToString(), m => m.Groups[1].Success ? " " : "");
    }

    /// Waits for a transcription in progress.
    public void Dispose()
    {
        lock (_gate)
        {
            _preprocessor.Dispose();
            _encoder.Dispose();
            _decoderJoint.Dispose();
        }
    }
}
