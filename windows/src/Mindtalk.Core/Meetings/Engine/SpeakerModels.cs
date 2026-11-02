using System.Security.Cryptography;
using Mindtalk.Core.Speech;

namespace Mindtalk.Core.Meetings.Engine;

/// <summary>A file of the speaker models: where it comes from, pinned by size and SHA-256.</summary>
public sealed record PinnedFile(string LocalName, Uri Url, long Size, string Sha256);

/// <summary>
/// The models that keep speakers apart and recognize voices (the Mac's Nemotron 3 and
/// ReDimNet2+ are Core ML, which Windows can't run): pyannote segmentation 3.0 finds who
/// speaks when, and a 3D-Speaker CAM++ model turns a voice into a voiceprint of 192 numbers.
/// Both run through sherpa-onnx. Downloaded once to %LOCALAPPDATA%\Mindtalk\Models\speakers,
/// each file checked against its SHA-256 before it's moved into place.
/// </summary>
public sealed class SpeakerModels
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// pyannote segmentation 3.0 (MIT), as ONNX by the sherpa-onnx team, at a pinned revision.
    public static readonly PinnedFile Segmentation = new("segmentation.onnx",
        new("https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx"),
        5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079");

    /// 3D-Speaker CAM++ (Apache 2.0), trained on Chinese and English speech: 192 numbers per voice.
    public static readonly PinnedFile Embedding = new("embedding.onnx",
        new("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"),
        28_281_164, "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2");

    public static IReadOnlyList<PinnedFile> Files { get; } = [Segmentation, Embedding];

    public static long DownloadBytes => Files.Sum(f => f.Size);

    public SpeakerModels(string? folder = null) =>
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindtalk", "Models", "speakers");

    public string Folder { get; }
    public string SegmentationPath => Path.Combine(Folder, Segmentation.LocalName);
    public string EmbeddingPath => Path.Combine(Folder, Embedding.LocalName);

    /// Every file present with its pinned size (hashes are checked before they're moved in).
    public bool IsInstalled => Files.All(f => new FileInfo(Path.Combine(Folder, f.LocalName)) is { Exists: true } info && info.Length == f.Size);

    /// <summary>
    /// Downloads what's missing into a staging folder, checks every file, then moves them in.
    /// <paramref name="progress"/>: 0…0.9 by bytes, then checking.
    /// </summary>
    public async Task InstallAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var staging = Folder + ".download";
        Directory.CreateDirectory(staging);
        var total = (double)DownloadBytes;
        long done = 0;
        foreach (var file in Files)
        {
            var target = Path.Combine(staging, file.LocalName);
            var installed = Path.Combine(Folder, file.LocalName);
            if (new FileInfo(installed) is { Exists: true } have && have.Length == file.Size) { done += file.Size; continue; }
            if (!File.Exists(target) || new FileInfo(target).Length != file.Size)
            {
                var start = done;
                await DownloadAsync(file, target + ".part", bytes => progress?.Report(ModelStore.DownloadShare * (start + bytes) / total), ct);
                File.Move(target + ".part", target, overwrite: true);
            }
            done += file.Size;
            progress?.Report(ModelStore.DownloadShare * done / total);
        }

        foreach (var file in Files)
        {
            var target = Path.Combine(staging, file.LocalName);
            if (!File.Exists(target)) continue;   // already installed
            var actual = await ModelStore.Sha256Async(target, ct);
            if (actual != file.Sha256)
            {
                File.Delete(target);
                throw new InvalidDataException($"{file.LocalName}: checksum mismatch (expected {file.Sha256}, got {actual}).");
            }
        }
        Directory.CreateDirectory(Folder);
        foreach (var file in Files)
        {
            var target = Path.Combine(staging, file.LocalName);
            if (File.Exists(target)) File.Move(target, Path.Combine(Folder, file.LocalName), overwrite: true);
        }
        try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        progress?.Report(1);
    }

    private static async Task DownloadAsync(PinnedFile file, string part, Action<long> report, CancellationToken ct)
    {
        using var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        long written = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            // A misbehaving server can't fill the disk.
            if (written + n > file.Size + ModelStore.SizeSlack)
                throw new InvalidDataException($"{file.LocalName}: the server sent more than the pinned {file.Size} bytes.");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            written += n;
            report(written);
        }
    }
}
