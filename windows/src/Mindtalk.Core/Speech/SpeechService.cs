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
    private readonly ConcurrentDictionary<string, DateTime> _started = new();
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
    /// Download progress 0…1 (the last tenth is checking and moving in), or null if it isn't downloading.
    public double? DownloadProgress(SpeechModel model) => _downloads.TryGetValue(model.Id, out var p) ? p : null;
    /// When the running download started — for "about 2 min left".
    public DateTime? DownloadStarted(SpeechModel model) => _started.TryGetValue(model.Id, out var d) ? d : null;
    public string? DownloadError(SpeechModel model) => _errors.GetValueOrDefault(model.Id);

    /// <summary>
    /// Deletes a model to free up space — never the one in use, nor one that's downloading.
    /// (Only the model in use is ever loaded, so nothing needs unloading.)
    /// </summary>
    public bool Remove(SpeechModel model)
    {
        if (Status.Model == model || _downloads.ContainsKey(model.Id)) return false;
        try
        {
            Store.Uninstall(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
        return true;
    }

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
        _started[model.Id] = DateTime.Now;
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
            _errors[model.Id] = PlainWords(ex, System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable());
        }
        finally
        {
            finished = true;
            _downloads.TryRemove(model.Id, out _);
            _started.TryRemove(model.Id, out _);
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

    /// <summary>A failed download in plain words, as on the Mac.</summary>
    /// <param name="online">Whether the PC has a network at all (no network reads as a dropped connection).</param>
    public static string PlainWords(Exception ex, bool online = true)
    {
        var dropped = Loc.T("Internetanslutningen bröts. Kontrollera nätverket och försök igen.",
                            "The internet connection dropped. Check your network and try again.");
        var unreachable = Loc.T("Kunde inte nå servern just nu. Försök igen om en stund.",
                                "Couldn’t reach the server right now. Try again in a moment.");
        switch (ex)
        {
            // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL.
            case IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27:
                return Loc.T("Det finns inte tillräckligt med ledigt utrymme på din dator.",
                             "There isn’t enough free space on your PC.");
            case HttpRequestException http when http.StatusCode is null:
                if (!online) return dropped;
                return http.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => unreachable,
                    HttpRequestError.ResponseEnded => dropped,
                    _ => Loc.T("Något gick fel under nedladdningen. Försök igen.",
                               "Something went wrong during the download. Try again."),
                };
            // The connection broke while the file was coming in.
            case HttpIOException or IOException { InnerException: System.Net.Sockets.SocketException }:
                return dropped;
            case TimeoutException or TaskCanceledException { InnerException: TimeoutException }:
                return unreachable;
            default:
                return Loc.T("Något gick fel under nedladdningen. Försök igen.",
                             "Something went wrong during the download. Try again.");
        }
    }

    public void Dispose()
    {
        foreach (var cts in _cancels.Values) cts.Cancel();
        SetEngine(null);
    }
}
