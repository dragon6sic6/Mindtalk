using System.Collections.Concurrent;
using Mindtalk.Core.Dictation;

namespace Mindtalk.Core.Speech;

public enum ModelPhase { Missing, Downloading, Loading, Ready, Failed }

public sealed record ModelStatus(SpeechModel Model, ModelPhase Phase, string? Error = null);

/// <summary>
/// The speech models: downloads them, keeps the chosen one loaded, and transcribes on a
/// worker thread. Downloading one model doesn't disturb dictation with the other.
/// <see cref="Changed"/> is raised on whichever thread changed something — the UI marshals it.
/// </summary>
public sealed class SpeechService : ITranscriber, IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, double> _downloads = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new();
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private ParakeetEngine? _engine;
    private int _generation;

    public SpeechService(ModelStore store, SpeechModel model)
    {
        Store = store;
        Status = new ModelStatus(model, store.IsInstalled(model) ? ModelPhase.Loading : ModelPhase.Missing);
    }

    public ModelStore Store { get; }
    /// The chosen model, and whether it's ready.
    public ModelStatus Status { get; private set; }
    public event Action? Changed;

    public bool IsInstalled(SpeechModel model) => Store.IsInstalled(model);
    /// Download progress 0…1, or null if it isn't downloading.
    public double? DownloadProgress(SpeechModel model) => _downloads.TryGetValue(model.Id, out var p) ? p : null;
    public string? DownloadError(SpeechModel model) => _errors.GetValueOrDefault(model.Id);

    public string? NotReadyReason => Status.Phase switch
    {
        ModelPhase.Ready => null,
        ModelPhase.Loading => Loc.T("Språkmodellen startar – ett ögonblick…", "The speech model is starting — one moment…"),
        ModelPhase.Downloading => Loc.T("Språkmodellen laddas fortfarande ned…", "The speech model is still downloading…"),
        ModelPhase.Failed => Loc.T("Språkmodellen startade inte – försök igen.", "The speech model didn't start — try again."),
        _ => Loc.T($"Språkmodellen ({Status.Model.Title}) är inte nedladdad än.",
                   $"The speech model ({Status.Model.ModelName}) isn't downloaded yet."),
    };

    /// <summary>Switches to <paramref name="model"/>, loading it if installed.</summary>
    public void Use(SpeechModel model)
    {
        var generation = Interlocked.Increment(ref _generation);
        if (!Store.IsInstalled(model))
        {
            SetEngine(null);
            Publish(new ModelStatus(model, _downloads.ContainsKey(model.Id) ? ModelPhase.Downloading : ModelPhase.Missing));
            return;
        }
        Publish(new ModelStatus(model, ModelPhase.Loading));
        Task.Run(() =>
        {
            try
            {
                var engine = new ParakeetEngine(Store.DirectoryFor(model), model);
                // Warm up so the first dictation is as fast as the rest.
                engine.Transcribe(new float[ParakeetEngine.SampleRate / 2]);
                if (generation != _generation) { engine.Dispose(); return; }
                SetEngine(engine);
                Publish(new ModelStatus(model, ModelPhase.Ready));
            }
            catch (Exception ex)
            {
                if (generation == _generation) Publish(new ModelStatus(model, ModelPhase.Failed, ex.Message));
            }
        });
    }

    /// <summary>Downloads <paramref name="model"/> (verified); loads it if it's the chosen one.</summary>
    public async Task DownloadAsync(SpeechModel model)
    {
        if (_downloads.ContainsKey(model.Id)) return;
        var cts = new CancellationTokenSource();
        _cancels[model.Id] = cts;
        _errors.TryRemove(model.Id, out _);
        _downloads[model.Id] = 0;
        if (Status.Model == model) Publish(Status with { Phase = ModelPhase.Downloading });
        else Changed?.Invoke();

        var lastReported = -1;
        var finished = false;
        var progress = new Progress<double>(p =>
        {
            // A few hundred updates are plenty. Reports still queued after the download
            // ended mustn't bring it back.
            var permille = (int)(p * 1000);
            if (finished || permille == lastReported || cts.IsCancellationRequested) return;
            lastReported = permille;
            _downloads[model.Id] = p;
            Changed?.Invoke();
        });
        try
        {
            await Task.Run(() => Store.InstallAsync(model, progress, cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errors[model.Id] = PlainWords(ex);
        }
        finally
        {
            finished = true;
            _downloads.TryRemove(model.Id, out _);
            _cancels.TryRemove(model.Id, out _);
        }
        if (Status.Model == model) Use(model);
        else Changed?.Invoke();
    }

    public void CancelDownload(SpeechModel model)
    {
        if (_cancels.TryGetValue(model.Id, out var cts)) cts.Cancel();
    }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        ParakeetEngine engine;
        lock (_gate) engine = _engine ?? throw new InvalidOperationException(NotReadyReason);
        return Task.Run(() => engine.Transcribe(samples), ct);
    }

    private void SetEngine(ParakeetEngine? engine)
    {
        ParakeetEngine? old;
        lock (_gate) { old = _engine; _engine = engine; }
        // Off this thread: Dispose waits for a transcription still running on the old one.
        if (old is not null) Task.Run(old.Dispose);
    }

    private void Publish(ModelStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private static string PlainWords(Exception ex) => ex switch
    {
        HttpRequestException => Loc.T("Kunde inte ladda ned – kontrollera internetanslutningen.",
                                      "Couldn't download — check the internet connection."),
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => Loc.T("Det finns inte plats på disken.",
                                                                         "There isn't enough space on the disk."),
        _ => ex.Message,
    };

    public void Dispose()
    {
        foreach (var cts in _cancels.Values) cts.Cancel();
        SetEngine(null);
    }
}
