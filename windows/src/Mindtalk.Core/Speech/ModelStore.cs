using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mindtalk.Core.Speech;

/// <summary>
/// Where models live on disk, and installing them: download (resumable) into a staging
/// folder of its own, check every file's SHA-256, then move the whole folder into place —
/// so a half-finished install never looks installed.
/// </summary>
public sealed class ModelStore
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// Downloading is the first 90 % of the progress; checking and moving in, the rest (as on the Mac).
    public const double DownloadShare = 0.9;
    /// A misbehaving server can't fill the disk: never more than the pinned size plus this.
    public const long SizeSlack = 1_000_000;

    private const string StagingPrefix = ".download-";
    private readonly ConcurrentDictionary<string, byte> _installing = new();

    public string Root { get; }

    public ModelStore(string? root = null) =>
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "Models");

    public string DirectoryFor(SpeechModel model) => Path.Combine(Root, model.Id);

    /// Where a download collects its files until they're all in and checked.
    public string StagingFor(SpeechModel model) => Path.Combine(Root, StagingPrefix + model.Id);

    public bool IsInstalling(SpeechModel model) => _installing.ContainsKey(model.Id);

    /// All files present with the pinned size. (Hashes are checked before they're moved in.)
    public bool IsInstalled(SpeechModel model) =>
        model.Files.All(f =>
        {
            var info = new FileInfo(Path.Combine(DirectoryFor(model), f.LocalName));
            return info.Exists && info.Length == f.Size;
        });

    /// <param name="progress">0…0.9 while downloading (by bytes), 0.9…1 while checking and moving in.</param>
    public async Task InstallAsync(SpeechModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_installing.TryAdd(model.Id, 0)) throw new InvalidOperationException($"{model.Id} is already being installed.");
        var staging = StagingFor(model);
        try
        {
            Directory.CreateDirectory(staging);
            var total = (double)model.DownloadBytes;
            long done = 0;

            foreach (var file in model.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, file.LocalName);
                // Fetched by an earlier attempt that failed later on: checked with the rest below.
                if (!File.Exists(target) || new FileInfo(target).Length != file.Size)
                {
                    File.Delete(target);
                    var part = target + ".part";
                    var start = done;
                    await DownloadAsync(model.UrlFor(file), part, file,
                                        bytes => progress?.Report(DownloadShare * ((start + bytes) / total)), ct);
                    File.Move(part, target, overwrite: true);
                }
                done += file.Size;
                progress?.Report(DownloadShare * (done / total));
            }

            long verified = 0;
            foreach (var file in model.Files)
            {
                var target = Path.Combine(staging, file.LocalName);
                var actual = await Sha256Async(target, ct);
                if (actual != file.Sha256)
                {
                    File.Delete(target);   // fetched afresh next time; the good files stay
                    throw new InvalidDataException($"{file.Path}: checksum mismatch (expected {file.Sha256}, got {actual}).");
                }
                verified += file.Size;
                progress?.Report(DownloadShare + (1 - DownloadShare) * 0.95 * verified / total);
            }

            var dir = DirectoryFor(model);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(staging, dir);
            progress?.Report(1);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose: give the space back. (After a failure the files stay, so
            // trying again picks up where it broke off.)
            TryDelete(staging);
            throw;
        }
        finally
        {
            _installing.TryRemove(model.Id, out _);
        }
    }

    /// <summary>Removes the model's files (and any unfinished download of it).</summary>
    public void Uninstall(SpeechModel model)
    {
        var dir = DirectoryFor(model);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        if (!IsInstalling(model)) TryDelete(StagingFor(model));
    }

    /// <summary>
    /// Unfinished downloads from an earlier run (quit mid-download) — and partial files
    /// left inside model folders by older versions. Called at launch.
    /// </summary>
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal))
            {
                if (!_installing.ContainsKey(name[StagingPrefix.Length..])) TryDelete(dir);
            }
            else if (!_installing.ContainsKey(name))
            {
                foreach (var part in Directory.EnumerateFiles(dir, "*.part"))
                    try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task DownloadAsync(Uri url, string part, RemoteFile file, Action<long> report, CancellationToken ct)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size) { File.Delete(part); have = 0; }

        if (have < file.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            // A server that ignored the Range header sends the whole file again.
            var append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!append) have = 0;

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long written = have;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (written + n > file.Size + SizeSlack)
                    {
                        await output.DisposeAsync();
                        File.Delete(part);
                        throw new InvalidDataException($"{file.Path}: the server sent more than the pinned {file.Size} bytes.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    written += n;
                    report(written);
                }
            }
        }
        report(file.Size);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
