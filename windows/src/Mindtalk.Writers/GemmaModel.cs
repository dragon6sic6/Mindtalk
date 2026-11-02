using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Mindtalk.Core;
using Mindtalk.Core.AI;

namespace Mindtalk.Writers;

/// <summary>
/// Gemma 4 on this PC, through llama.cpp (LLamaSharp): on the graphics card with Vulkan where
/// there is one, else on the processor. Loaded on first use and let go after a few idle minutes,
/// so its memory is only taken while it writes. One answer at a time.
/// </summary>
public sealed class GemmaModel : ILanguageModel, IContextWindow, IDisposable
{
    /// Room for a 25-minute meeting in one go; longer ones are summarized in parts.
    public const int Context = 16384;
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ModelParams? _params;
    private Timer? _idle;

    public GemmaModel(string name, string path)
    {
        Name = name;
        _path = path;
    }

    public string Name { get; }
    public int ContextTokens => Context;

    /// "Vulkan" or "CPU", once loaded — for the self-test.
    public string? Backend { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options,
                                                      [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            var context = await LoadAsync(ct);
            using var sampling = new DefaultSamplingPipeline
            {
                Temperature = (float)options.Temperature,
                TopK = 64,
                TopP = 0.95f,
                // A little, so a long answer doesn't loop on itself.
                RepeatPenalty = 1.05f,
                Grammar = options.Json ? new Grammar(JsonGrammar, "root") : null,
            };
            // The answer gets the room the question leaves.
            var tokens = context.Tokenize(Prompt(messages), true, true);
            var room = Context - tokens.Length - 8;
            if (room < 128) throw new InvalidOperationException(Loc.T($"Texten är för lång för {Name}.", $"The text is too long for {Name}."));
            var maxTokens = Math.Min(options.MaxTokens, room);

            // One context, kept with the weights and cleared for each answer. (LLamaSharp's
            // StatelessExecutor makes a new one per answer; on some graphics cards the second
            // one fails with "device lost", and llama.cpp then crashes the whole app.)
            context.NativeHandle.MemoryClear(true);
            var batch = new LLamaBatch();
            var past = 0;
            var size = (int)context.BatchSize;
            for (var i = 0; i < tokens.Length; i += size)
            {
                batch.Clear();
                var n = Math.Min(size, tokens.Length - i);
                for (var j = 0; j < n; j++) batch.Add(tokens[i + j], past + j, LLamaSeqId.Zero, i + j == tokens.Length - 1);
                past += n;
                Decoded(await context.DecodeAsync(batch, ct));
            }
            Native.Worked();

            var vocab = context.NativeHandle.ModelHandle.Vocab;
            var decoder = new StreamingTokenDecoder(context);
            var filter = new ThinkingFilter();
            var index = batch.TokenCount - 1;
            for (var produced = 0; produced < maxTokens; produced++)
            {
                ct.ThrowIfCancellationRequested();
                var token = sampling.Sample(context.NativeHandle, index);
                if (token.IsEndOfGeneration(vocab)) break;
                decoder.Add(token);
                var text = filter.Push(decoder.Read());
                if (text.Length > 0) yield return text;
                if (filter.Ended) break;
                batch.Clear();
                batch.Add(token, past++, LLamaSeqId.Zero, true);
                Decoded(await context.DecodeAsync(batch, ct));
                index = 0;
            }
            var rest = filter.Flush();
            if (rest.Length > 0) yield return rest;
        }
        finally
        {
            _idle ??= new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task<LLamaContext> LoadAsync(CancellationToken ct)
    {
        if (_context is not null) return _context;
        Native.Configure(Path.GetDirectoryName(Path.GetDirectoryName(_path)));
        // All layers on the graphics card if there is one (llama.cpp keeps them on the CPU otherwise).
        _params = new ModelParams(_path)
        {
            ContextSize = Context,
            GpuLayerCount = Native.UsesGpu ? 99 : 0,
            Threads = Math.Max(2, Environment.ProcessorCount / 2),
        };
        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _params.GpuLayerCount > 0)
        {
            // The graphics card couldn't take it (too little memory): the processor can.
            _params.GpuLayerCount = 0;
            _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
        }
        // llama.cpp hands back an empty context instead of an error when the graphics card fails,
        // and using it crashes the app: check, and leave the graphics card from now on.
        var context = _weights.CreateContext(_params);
        if (context.NativeHandle.IsInvalid)
        {
            context.Dispose();
            Release();
            Native.Failed();
            throw new InvalidOperationException(Native.UsesGpu
                ? Loc.T($"{Name} kunde inte starta på grafikkortet. Starta om Mindtalk, så körs den på processorn.", $"{Name} couldn't start on the graphics card. Restart Mindtalk and it runs on the processor.")
                : Loc.T($"{Name} kunde inte starta.", $"{Name} couldn't start."));
        }
        _context = context;
        Backend = _params.GpuLayerCount > 0 && Native.UsesGpu ? "Vulkan" : $"CPU ({Native.Variant})";
        return _context;
    }

    private static void Decoded(LLama.Native.DecodeResult result)
    {
        if (result != LLama.Native.DecodeResult.Ok) throw new InvalidOperationException(Loc.T($"Gemma slutade skriva ({result}).", $"Gemma stopped writing ({result})."));
    }

    private void Release()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
    }

    private void Unload()
    {
        if (!_gate.Wait(0)) return;   // writing right now: try again next time it's idle
        try { Release(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _idle?.Dispose();
        _gate.Wait();
        Release();
        _gate.Release();
    }

    // ---- Gemma 4's chat format ----------------------------------------------------------

    private const string EndOfTurn = "<turn|>";

    /// <summary>
    /// The conversation as Gemma 4 was trained on it (its GGUF's chat template): a system turn,
    /// then user and model turns, ending with the model's turn open. Thinking stays off.
    /// llama.cpp adds the &lt;bos&gt; token itself.
    /// </summary>
    public static string Prompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        var rest = messages.AsEnumerable();
        if (messages.Count > 0 && messages[0].Role is "system" or "developer")
        {
            sb.Append("<|turn>system\n").Append(messages[0].Content.Trim()).Append("<turn|>\n");
            rest = messages.Skip(1);
        }
        foreach (var m in rest)
        {
            var role = m.Role == "assistant" ? "model" : m.Role;
            sb.Append("<|turn>").Append(role).Append('\n').Append(m.Content.Trim()).Append("<turn|>\n");
        }
        sb.Append("<|turn>model\n");
        return sb.ToString();
    }

    /// <summary>Leaves out a thinking block (&lt;|channel&gt;thought … &lt;channel|&gt;) and the end-of-turn mark, as text streams in.</summary>
    public sealed class ThinkingFilter
    {
        private readonly StringBuilder _pending = new();
        private bool _thinking;

        /// The end-of-turn mark came: the answer is complete.
        public bool Ended { get; private set; }

        public string Push(string piece)
        {
            if (Ended) return "";
            _pending.Append(piece);
            var output = new StringBuilder();
            while (true)
            {
                var text = _pending.ToString();
                if (_thinking)
                {
                    var end = text.IndexOf("<channel|>", StringComparison.Ordinal);
                    if (end < 0) { Keep(text, "<channel|>".Length - 1); break; }
                    _thinking = false;
                    _pending.Remove(0, end + "<channel|>".Length);
                    continue;
                }
                var start = text.IndexOf("<|channel>", StringComparison.Ordinal);
                var turn = text.IndexOf(EndOfTurn, StringComparison.Ordinal);
                if (turn >= 0 && (start < 0 || turn < start))
                {
                    output.Append(text[..turn]);
                    Ended = true;
                    _pending.Clear();
                    break;
                }
                if (start >= 0)
                {
                    output.Append(text[..start]);
                    _pending.Remove(0, start + "<|channel>".Length);
                    _thinking = true;
                    continue;
                }
                // Hold back what could be the start of a mark.
                var hold = PartialMark(text);
                output.Append(text[..(text.Length - hold)]);
                _pending.Remove(0, text.Length - hold);
                break;
            }
            return output.ToString();
        }

        public string Flush()
        {
            var rest = _thinking ? "" : _pending.ToString();
            _pending.Clear();
            return rest;
        }

        private void Keep(string text, int tail)
        {
            _pending.Clear();
            if (text.Length > tail) _pending.Append(text[^tail..]);
            else _pending.Append(text);
        }

        private static int PartialMark(string text)
        {
            foreach (var mark in new[] { "<|channel>", EndOfTurn })
                for (var n = Math.Min(mark.Length - 1, text.Length); n > 0; n--)
                    if (text.EndsWith(mark[..n], StringComparison.Ordinal)) return n;
            return 0;
        }
    }

    /// llama.cpp's JSON grammar: the answer can only be a JSON object.
    private const string JsonGrammar = """
        root   ::= object
        value  ::= object | array | string | number | ("true" | "false" | "null") ws
        object ::= "{" ws ( string ":" ws value ("," ws string ":" ws value)* )? "}" ws
        array  ::= "[" ws ( value ("," ws value)* )? "]" ws
        string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt] | "u" [0-9a-fA-F]{4}) )* "\"" ws
        number ::= ("-"? ([0-9] | [1-9] [0-9]{0,15})) ("." [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws
        ws     ::= | " " | "\n" [ \t]{0,20}
        """;

    /// <summary>
    /// Which llama.cpp build to load. LLamaSharp only picks one by itself on .NET 10; on .NET 9 it
    /// asks for plain "llama", so Mindtalk points it at the right folder: Vulkan where Windows has a
    /// Vulkan driver (and it hasn't failed before), else the fastest the processor can run.
    /// </summary>
    public static class Native
    {
        private static readonly object Gate = new();
        private static string? _folder;
        private static LLama.Native.NativeLogConfig.LLamaLogCallback? _log;

        /// "vulkan", "avx512", "avx2", "avx" or "noavx" — once chosen.
        public static string? Variant => _folder is null ? null : Path.GetFileName(_folder);
        public static bool UsesGpu => Variant == "vulkan";

        /// Without the graphics card: set before the first model loads (after Vulkan failed once).
        public static bool CpuOnly { get; set; } = Environment.GetEnvironmentVariable("MINDTALK_LLAMA_CPU") == "1";

        // Notes beside the models: "gpu-trying" while the graphics card hasn't worked yet in this
        // run, "gpu-failed" once it hasn't worked. If Mindtalk is still trying at its next start, it
        // went down with the graphics card (llama.cpp can crash the app): the processor from then on.
        private static string? _notes;
        private static bool _gpuWorked;
        private static string Note(string name) => Path.Combine(_notes!, name);

        /// The graphics card failed: the processor from the next start.
        public static void Failed()
        {
            if (_notes is null || !UsesGpu) return;
            try { File.WriteAllText(Note("gpu-failed"), DateTime.Now.ToString("O")); File.Delete(Note("gpu-trying")); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// Text went through the model: the graphics card works.
        public static void Worked()
        {
            if (_gpuWorked || _notes is null || !UsesGpu) return;
            _gpuWorked = true;
            try { File.Delete(Note("gpu-trying")); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static void Configure(string? notes = null)
        {
            lock (Gate)
            {
                if (_folder is not null) return;
                if (notes is not null && Directory.Exists(notes))
                {
                    _notes = notes;
                    if (File.Exists(Note("gpu-trying")) || File.Exists(Note("gpu-failed")))
                    {
                        CpuOnly = true;
                        try { File.WriteAllText(Note("gpu-failed"), DateTime.Now.ToString("O")); File.Delete(Note("gpu-trying")); }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                var root = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "native");
                var folders = Candidates().Select(v => Path.Combine(root, v)).Where(d => File.Exists(Path.Combine(d, "llama.dll"))).ToList();
                _folder = folders.FirstOrDefault() ?? AppContext.BaseDirectory;
                // The Vulkan build has no processor backend of its own: it uses the best CPU build's,
                // loaded first (its ggml-base.dll is the same build, so the two share it).
                if (Variant == "vulkan" && folders.Skip(1).FirstOrDefault() is { } cpu
                    && !(Preload(Path.Combine(_folder, "ggml-base.dll")) && Preload(Path.Combine(cpu, "ggml-cpu.dll"))))
                    _folder = cpu;
                if (UsesGpu && _notes is not null)
                    try { File.WriteAllText(Note("gpu-trying"), DateTime.Now.ToString("O")); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                NativeLibrary.SetDllImportResolver(typeof(LLamaWeights).Assembly, Resolve);
                // llama.cpp talks a lot; only when asked (MINDTALK_LLAMA_LOG=1).
                var log = Environment.GetEnvironmentVariable("MINDTALK_LLAMA_LOG") == "1";
                // Kept in a field: native code holds on to it.
                _log = (level, message) => { if (log) Console.Error.Write(message); };
                LLama.Native.NativeLogConfig.llama_log_set(_log);
            }
        }

        private static IEnumerable<string> Candidates()
        {
            if (!CpuOnly && HasVulkan()) yield return "vulkan";
            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) yield return "avx512";
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) yield return "avx2";
            if (System.Runtime.Intrinsics.X86.Avx.IsSupported) yield return "avx";
            yield return "noavx";
        }

        // The Vulkan loader comes with the graphics driver (NVIDIA, AMD, Intel).
        private static bool HasVulkan() =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));

        private static bool Preload(string file) => File.Exists(file) && NativeLibrary.TryLoad(file, out _);

        private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
        {
            var file = Path.Combine(_folder!, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            // Loaded by full path, so its own ggml*.dll beside it are found too.
            return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
