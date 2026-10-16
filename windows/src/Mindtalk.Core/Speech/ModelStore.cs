using System.Security.Cryptography;

namespace Mindtalk.Core.Speech;

/// <summary>Where models live on disk, and downloading them (resumable, SHA-256 checked).</summary>
public sealed class ModelStore
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Root { get; }

    public ModelStore(string? root = null) =>
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "Models");

    public string DirectoryFor(SpeechModel model) => Path.Combine(Root, model.Id);

    /// All files present with the pinned size. (Hashes are checked when downloaded.)
    public bool IsInstalled(SpeechModel model) =>
        model.Files.All(f =>
        {
            var info = new FileInfo(Path.Combine(DirectoryFor(model), f.LocalName));
            return info.Exists && info.Length == f.Size;
        });

    /// <param name="progress">Fraction 0…1 of the bytes downloaded or verified.</param>
    public async Task InstallAsync(SpeechModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = DirectoryFor(model);
        Directory.CreateDirectory(dir);
        var total = (double)model.DownloadBytes;
        long done = 0;

        foreach (var file in model.Files)
        {
            var target = Path.Combine(dir, file.LocalName);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size && await Sha256Async(target, ct) == file.Sha256)
            {
                done += file.Size;
                progress?.Report(done / total);
                continue;
            }
            File.Delete(target);

            var part = target + ".part";
            await DownloadAsync(model.UrlFor(file), part, file, bytes => progress?.Report((done + bytes) / total), ct);
            var actual = await Sha256Async(part, ct);
            if (actual != file.Sha256)
            {
                File.Delete(part);
                throw new InvalidDataException($"{file.Path}: checksum mismatch (expected {file.Sha256}, got {actual}).");
            }
            File.Move(part, target, overwrite: true);
            done += file.Size;
        }
        progress?.Report(1);
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
            await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 20];
            long written = have;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                written += n;
                report(written);
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
