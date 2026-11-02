using Mindtalk.Core;
using Mindtalk.Core.AI;
using Mindtalk.Core.Speech;

namespace Mindtalk.Writers;

/// <summary>A Gemma 4 download: one GGUF file from a pinned revision, checked against its SHA-256.</summary>
public sealed record GemmaVariant(WriterKind Kind, string Title, string Id, string Repo, string Revision, string File, long Size, string Sha256, int MemoryGB)
{
    /// Gemma 4 E4B (Q4_K_M, 5.0 GB) — the Mac's 6.9 GB MLX build, for 16 GB of memory.
    public static readonly GemmaVariant Small = new(WriterKind.GemmaSmall, "Gemma 4 E4B", "gemma-4-e4b",
        "unsloth/gemma-4-E4B-it-GGUF", "bfc15c382204943c3a8fff0c750b94ae2364d7a3", "gemma-4-E4B-it-Q4_K_M.gguf",
        4_977_171_584, "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87", 16);

    /// Gemma 4 26B A4B (Q4_K_M, 16.9 GB): the most accurate, for 32 GB of memory.
    public static readonly GemmaVariant Large = new(WriterKind.GemmaLarge, "Gemma 4 26B", "gemma-4-26b-a4b",
        "unsloth/gemma-4-26B-A4B-it-GGUF", "c099eb48e663fd284577b04978a94ffccb261841", "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        16_947_541_728, "f2c28b3dc4776931ac6f879e11f203dec637ea0f14267a86ec8f6165f63f293f", 32);

    public static GemmaVariant? For(WriterKind kind) => kind switch
    {
        WriterKind.GemmaSmall => Small,
        WriterKind.GemmaLarge => Large,
        _ => null,
    };

    /// As a model the speech models' store can download and check.
    internal SpeechModel AsStoreModel() => new()
    {
        Id = Id, ModelName = Title, Credit = "Google DeepMind", ModelPage = $"https://huggingface.co/google/{Repo.Split('/')[1].Replace("-GGUF", "")}",
        ConversionCredit = "Unsloth", Repo = Repo, Revision = Revision, Encoder = File, DecoderJoint = File,
        Files = [new RemoteFile(File, Size, Sha256)],
    };
}

/// <summary>Whether Windows' own model can write here, and if not, why (in plain words).</summary>
public sealed record WindowsAiState(bool Available, bool Ready, string Detail);

/// <summary>
/// Who writes summaries, titles and answers: Gemma 4 (downloaded once), your model in Ollama or
/// LM Studio on 127.0.0.1, or Windows' own model where Windows lets Mindtalk use it.
/// <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class WriterService : IWriterService, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Func<WindowsAiState> _windows;
    private readonly Func<ILanguageModel?>? _windowsModel;
    private readonly ModelStore _store;
    private readonly long _memory;
    private readonly Dictionary<WriterKind, GemmaModel> _loaded = [];
    private readonly Dictionary<WriterKind, (double Progress, CancellationTokenSource Cancel)> _downloads = [];
    private readonly Dictionary<WriterKind, string> _errors = [];
    private readonly Dictionary<WriterKind, IReadOnlyList<string>?> _local = new() { [WriterKind.Ollama] = null, [WriterKind.LMStudio] = null };
    private readonly object _gate = new();
    private DateTime _checked = DateTime.MinValue;

    public WriterService(AppSettings settings, Action save, Func<WindowsAiState> windows, Func<ILanguageModel?>? windowsModel = null, string? modelsRoot = null)
    {
        _settings = settings;
        _save = save;
        _windows = windows;
        _windowsModel = windowsModel;
        _store = new ModelStore(Path.Combine(modelsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "Models"), "writers"));
        _memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _ = CheckLocalAsync();
    }

    public event Action? Changed;

    public string Root => _store.Root;
    public WriterKind Current => _settings.Writer;

    public string CurrentName
    {
        get
        {
            var status = Status(Current);
            return status.Model is { } model ? $"{status.Title} · {model}" : status.Title;
        }
    }

    public IReadOnlyList<WriterStatus> All =>
        [Status(WriterKind.Windows), Status(WriterKind.GemmaSmall), Status(WriterKind.GemmaLarge), Status(WriterKind.Ollama), Status(WriterKind.LMStudio)];

    public string PathOf(GemmaVariant variant) => Path.Combine(_store.DirectoryFor(variant.AsStoreModel()), variant.File);
    public bool IsDownloaded(GemmaVariant variant) => _store.IsInstalled(variant.AsStoreModel());

    public WriterStatus Status(WriterKind kind)
    {
        RefreshLocalSoon();
        lock (_gate)
        {
            var error = _errors.GetValueOrDefault(kind);
            switch (kind)
            {
                case WriterKind.Windows:
                {
                    var w = _windows();
                    return new WriterStatus(kind, Loc.T("Windows AI", "Windows AI"), w.Detail, w.Available, w.Available && w.Ready, Error: error);
                }
                case WriterKind.GemmaSmall or WriterKind.GemmaLarge:
                {
                    var v = GemmaVariant.For(kind)!;
                    var enough = _memory <= 0 || _memory >= (v.MemoryGB - 4) * 1_000_000_000L;
                    var gb = Core.Input.ShortcutRules.Size(v.Size);
                    var size = Loc.T($"{gb}, för {v.MemoryGB} GB minne", $"{gb}, for {v.MemoryGB} GB of memory");
                    // Settings adds "This PC has 8 GB" where there is too little memory.
                    var detail = size;
                    double? progress = _downloads.TryGetValue(kind, out var d) ? d.Progress : null;
                    return new WriterStatus(kind, v.Title, detail, enough, IsDownloaded(v), progress, error, v.Size);
                }
                default:
                {
                    var name = kind == WriterKind.Ollama ? "Ollama" : "LM Studio";
                    var chosen = kind == WriterKind.Ollama ? _settings.OllamaModel : _settings.LMStudioModel;
                    var models = _local[kind];
                    var running = models is { Count: > 0 };
                    var detail = models is null ? Loc.T("Söker på den här datorn…", "Looking on this PC…")
                        : models.Count == 0 ? Loc.T("Körs inte på den här datorn", "Not running on this PC")
                        : chosen is not null && models.Contains(chosen) ? chosen
                        : Loc.T("Välj en modell", "Choose a model");
                    // Not running: greyed in Settings, with why (still looked for now and then).
                    return new WriterStatus(kind, name, detail, models is null || running, running && chosen is not null && models!.Contains(chosen), Error: error, Model: chosen);
                }
            }
        }
    }

    public ILanguageModel? Model => ModelFor(Current);

    /// The writer of a kind, if it's ready to write.
    public ILanguageModel? ModelFor(WriterKind kind)
    {
        switch (kind)
        {
            case WriterKind.Windows:
                return Status(kind).Ready ? _windowsModel?.Invoke() : null;
            case WriterKind.GemmaSmall or WriterKind.GemmaLarge:
            {
                var v = GemmaVariant.For(kind)!;
                if (!IsDownloaded(v)) return null;
                lock (_gate)
                {
                    if (!_loaded.TryGetValue(kind, out var model)) _loaded[kind] = model = new GemmaModel(v.Title, PathOf(v));
                    return model;
                }
            }
            case WriterKind.Ollama:
                return Status(kind).Ready ? new LocalServers.OllamaModel(_settings.OllamaModel!) : null;
            default:
                return Status(kind).Ready ? new LocalServers.LMStudioModel(_settings.LMStudioModel!) : null;
        }
    }

    public void Use(WriterKind kind, string? localModel = null)
    {
        _settings.Writer = kind;
        if (kind == WriterKind.Ollama && localModel is not null) _settings.OllamaModel = localModel;
        if (kind == WriterKind.LMStudio && localModel is not null) _settings.LMStudioModel = localModel;
        _save();
        // A Gemma that isn't in use any more gives its memory back.
        lock (_gate)
            foreach (var other in _loaded.Keys.Where(k => k != kind).ToList())
            {
                _loaded[other].Dispose();
                _loaded.Remove(other);
            }
        Changed?.Invoke();
    }

    public async Task DownloadAsync(WriterKind kind)
    {
        var v = GemmaVariant.For(kind) ?? throw new ArgumentException($"{kind} isn't downloaded.", nameof(kind));
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_downloads.ContainsKey(kind)) return;
            cancel = new CancellationTokenSource();
            _downloads[kind] = (0, cancel);
            _errors.Remove(kind);
        }
        Changed?.Invoke();
        var last = -1;
        var progress = new SyncProgress(p =>
        {
            lock (_gate) if (_downloads.ContainsKey(kind)) _downloads[kind] = (p, cancel);
            var step = (int)(p * 200);
            if (step == last) return;
            last = step;
            Changed?.Invoke();
        });
        try
        {
            await Task.Run(() => _store.InstallAsync(v.AsStoreModel(), progress, cancel.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate) _errors[kind] = ex is HttpRequestException or IOException
                ? Loc.T("Nedladdningen avbröts. Försök igen.", "The download was interrupted. Try again.")
                : ex.Message;
        }
        finally
        {
            lock (_gate) _downloads.Remove(kind);
            cancel.Dispose();
            Changed?.Invoke();
        }
    }

    public void CancelDownload(WriterKind kind)
    {
        lock (_gate) if (_downloads.TryGetValue(kind, out var d)) d.Cancel.Cancel();
    }

    public void Remove(WriterKind kind)
    {
        var v = GemmaVariant.For(kind);
        if (v is null) return;
        CancelDownload(kind);
        lock (_gate)
        {
            if (_loaded.Remove(kind, out var model)) model.Dispose();
            _errors.Remove(kind);
        }
        try { _store.Uninstall(v.AsStoreModel()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<string>> LocalModelsAsync(WriterKind kind, CancellationToken ct = default)
    {
        if (kind is not (WriterKind.Ollama or WriterKind.LMStudio)) return [];
        var models = await LocalServers.ModelsAsync(kind, ct);
        bool changed;
        lock (_gate)
        {
            changed = _local[kind] is not { } before || !before.SequenceEqual(models);
            _local[kind] = models;
        }
        if (changed) Changed?.Invoke();
        return models;
    }

    /// Ollama and LM Studio are looked for now and then (they may be started or stopped any time).
    private void RefreshLocalSoon()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(15)) return;
            _checked = DateTime.UtcNow;
        }
        _ = CheckLocalAsync();
    }

    private async Task CheckLocalAsync()
    {
        lock (_gate) _checked = DateTime.UtcNow;
        try
        {
            await Task.WhenAll(LocalModelsAsync(WriterKind.Ollama), LocalModelsAsync(WriterKind.LMStudio));
        }
        catch (Exception) { /* not there: shown as not running */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var d in _downloads.Values) d.Cancel.Cancel();
            foreach (var m in _loaded.Values) m.Dispose();
            _loaded.Clear();
        }
    }

    /// Progress reported where it happens (Progress&lt;T&gt; would post to a context that may not be there).
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
