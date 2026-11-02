using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mindtalk.Core.AI;

/// <summary>
/// Your own model in Ollama or LM Studio. Reached only on 127.0.0.1 — the addresses are fixed
/// here and never taken from settings, so nothing can point Mindtalk anywhere else.
/// </summary>
public static class LocalServers
{
    public static readonly Uri Ollama = new("http://127.0.0.1:11434/");
    public static readonly Uri LMStudio = new("http://127.0.0.1:1234/");

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,                      // never through a proxy: straight to this PC
        ConnectTimeout = TimeSpan.FromSeconds(2),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri Address(WriterKind kind) => kind switch
    {
        WriterKind.Ollama => Ollama,
        WriterKind.LMStudio => LMStudio,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The models the server has, or empty if it isn't running.</summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync(WriterKind kind, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var path = kind == WriterKind.Ollama ? "api/tags" : "v1/models";
            using var response = await Http.GetAsync(new Uri(Address(kind), path), timeout.Token);
            if (!response.IsSuccessStatusCode) return [];
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var list = kind == WriterKind.Ollama ? json?["models"] as JsonArray : json?["data"] as JsonArray;
            return (list ?? [])
                .Select(m => (string?)(kind == WriterKind.Ollama ? m?["name"] : m?["id"]))
                .OfType<string>()
                // Embedding models can't write.
                .Where(n => !n.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return [];
        }
    }

    /// <summary>A model in Ollama, streaming /api/chat.</summary>
    public sealed class OllamaModel(string model) : ILanguageModel, IContextWindow
    {
        public string Name => $"Ollama · {model}";
        // Ollama's own default context; asked for explicitly below.
        public int ContextTokens => 8192;

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options,
                                                          [EnumeratorCancellation] CancellationToken ct = default)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["messages"] = Messages(messages),
                ["options"] = new JsonObject
                {
                    ["temperature"] = options.Temperature,
                    ["num_predict"] = options.MaxTokens,
                    ["num_ctx"] = ContextTokens,
                },
            };
            if (options.Json) body["format"] = "json";
            using var request = Post(new Uri(Ollama, "api/chat"), body);
            using var response = await Send(request, ct);
            await foreach (var line in Lines(response, ct))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (node?["error"] is { } error) throw new InvalidOperationException((string?)error);
                if ((string?)node?["message"]?["content"] is { Length: > 0 } text) yield return text;
                if ((bool?)node?["done"] == true) yield break;
            }
        }
    }

    /// <summary>A model in LM Studio, streaming its OpenAI-style /v1/chat/completions.</summary>
    public sealed class LMStudioModel(string model) : ILanguageModel, IContextWindow
    {
        public string Name => $"LM Studio · {model}";
        public int ContextTokens => 8192;

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options,
                                                          [EnumeratorCancellation] CancellationToken ct = default)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["messages"] = Messages(messages),
                ["temperature"] = options.Temperature,
                ["max_tokens"] = options.MaxTokens,
            };
            using var request = Post(new Uri(LMStudio, "v1/chat/completions"), body);
            using var response = await Send(request, ct);
            await foreach (var line in Lines(response, ct))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                JsonNode? node;
                try { node = JsonNode.Parse(data); } catch (JsonException) { continue; }
                if (node?["error"] is { } error) throw new InvalidOperationException((string?)error["message"] ?? error.ToJsonString());
                if ((string?)node?["choices"]?[0]?["delta"]?["content"] is { Length: > 0 } text) yield return text;
            }
        }
    }

    private static JsonArray Messages(IReadOnlyList<ChatMessage> messages) =>
        new(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());

    private static HttpRequestMessage Post(Uri url, JsonObject body)
    {
        if (!url.IsLoopback) throw new InvalidOperationException("Only 127.0.0.1.");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new HttpRequestException($"{(int)response.StatusCode}: {text}");
        }
        return response;
    }

    private static async IAsyncEnumerable<string> Lines(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Length > 0) yield return line;
    }
}
