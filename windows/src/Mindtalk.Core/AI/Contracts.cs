namespace Mindtalk.Core.AI;

/// <summary>A writer as Settings shows it: available here, ready, downloading…</summary>
/// <param name="Title">"Gemma 4 E4B", "Ollama", "LM Studio", "Windows AI".</param>
/// <param name="Detail">One line: "6.9 GB, for 16 GB of memory", "Not running on this PC", "llama3.2".</param>
/// <param name="Available">Can be chosen on this PC (Windows AI only on Copilot+ PCs, packaged).</param>
/// <param name="Ready">Can write now (downloaded / reachable with a model chosen).</param>
/// <param name="Progress">Download progress 0…1 while downloading.</param>
/// <param name="SizeBytes">Download size, for the downloadable ones.</param>
/// <param name="Model">For Ollama / LM Studio: the model chosen there.</param>
public sealed record WriterStatus(WriterKind Kind, string Title, string Detail, bool Available, bool Ready,
                                  double? Progress = null, string? Error = null, long? SizeBytes = null, string? Model = null);

/// <summary>Who writes summaries, titles and answers — and getting them ready.</summary>
public interface IWriterService
{
    WriterKind Current { get; }
    /// "Gemma 4 E4B" / "Ollama · llama3.2" — for "Summarized by …".
    string CurrentName { get; }
    /// Every writer, for Settings → Meetings and the Meetings page's "Change".
    IReadOnlyList<WriterStatus> All { get; }
    WriterStatus Status(WriterKind kind);
    /// The chosen writer, if it's ready.
    ILanguageModel? Model { get; }

    void Use(WriterKind kind, string? localModel = null);
    Task DownloadAsync(WriterKind kind);
    void CancelDownload(WriterKind kind);
    void Remove(WriterKind kind);
    /// The models in Ollama or LM Studio on 127.0.0.1 (empty if it isn't running).
    Task<IReadOnlyList<string>> LocalModelsAsync(WriterKind kind, CancellationToken ct = default);

    event Action? Changed;
}

/// <summary>One read by an AI app, for the log under Settings → AI apps.</summary>
/// <param name="Client">"Claude", "Cursor", "command line"…</param>
/// <param name="Action">"search", "read meeting"…</param>
/// <param name="What">What was read: a meeting's title, the search words.</param>
public sealed record AccessLogEntry(DateTime When, string Client, string Action, string? What);

/// <summary>A place an AI app is connected from, with what to paste there.</summary>
public sealed record AiAppSetup(string Name, string Instructions, string Config);

/// <summary>AI apps (MCP) and the command line: off by default, read-only, every read logged.</summary>
public interface IAiApps
{
    bool Enabled { get; }
    void SetEnabled(bool enabled);
    /// Newest first.
    IReadOnlyList<AccessLogEntry> Log { get; }
    void ClearLog();
    /// The helper AI apps start (mindtalk-mcp.exe), and the command line's name.
    string HelperPath { get; }
    /// Claude, Claude Code, ChatGPT (Codex), Cursor, others.
    IReadOnlyList<AiAppSetup> Setups { get; }
    event Action? Changed;
}
