namespace Mindtalk.Core.AI;

/// <summary>
/// Who writes summaries, titles and answers. On the Mac: Apple Intelligence, Gemma 4 (MLX), or
/// Ollama / LM Studio. On Windows: Gemma 4 (llama.cpp), Ollama / LM Studio on 127.0.0.1, or
/// Windows' on-device model where Windows lets Mindtalk use it.
/// </summary>
public enum WriterKind
{
    /// Windows' on-device model (Phi Silica), the Mac's Apple Intelligence. Copilot+ PCs, packaged apps only.
    Windows,
    /// Gemma 4 E4B, downloaded once (6.9 GB on the Mac; the GGUF here). For 16 GB of memory.
    GemmaSmall,
    /// Gemma 4 26B A4B: the most accurate. For 32 GB of memory.
    GemmaLarge,
    /// Your own model in Ollama, on 127.0.0.1 only.
    Ollama,
    /// Your own model in LM Studio, on 127.0.0.1 only.
    LMStudio,
}

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage Assistant(string text) => new("assistant", text);
}

public sealed record GenerateOptions(int MaxTokens = 1500, double Temperature = 0.2, bool Json = false);

/// <summary>A language model that runs on this PC (or on 127.0.0.1).</summary>
public interface ILanguageModel
{
    /// "Gemma 4 E4B", "Ollama · llama3.2".
    string Name { get; }
    /// The text as it's written.
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options, CancellationToken ct = default);
}

public static class LanguageModelExtensions
{
    public static async Task<string> CompleteAsync(this ILanguageModel model, IReadOnlyList<ChatMessage> messages, GenerateOptions? options = null, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var piece in model.StreamAsync(messages, options ?? new GenerateOptions(), ct)) sb.Append(piece);
        return sb.ToString();
    }
}
