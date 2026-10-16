using System.Globalization;

namespace Mindtalk.Core.Speech;

/// <summary>One file of a model, pinned by size and SHA-256.</summary>
public sealed record RemoteFile(string Path, long Size, string Sha256)
{
    /// The name the file is stored under locally (folders in the repo are flattened).
    public string LocalName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The two speech models, as ONNX (the Mac app runs Core ML conversions, which don't run
/// on Windows). Both are NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0), trained further by others.
/// Neither ships with the app: each downloads once from a pinned revision and every file
/// is checked against its SHA-256 so a changed upstream repo can't slip in new weights.
/// </summary>
public sealed class SpeechModel
{
    public required string Id { get; init; }
    /// What the user picks: the language.
    public string Title => Id == "swedish" ? Loc.T("Svenska", "Swedish") : Loc.T("Flerspråkig", "Multilingual");
    /// Short tag for Swedish ("SV"); the multilingual model shows a globe instead.
    public string Code => Id == "swedish" ? "SV" : "EN";
    public string Pitch => Id == "swedish"
        ? Loc.T("Bäst på svenska – hänger med i engelska också.", "Best at Swedish – keeps up with English too.")
        : Loc.T("25 språk, bland annat svenska, engelska, tyska och franska.",
                "25 languages, including Swedish, English, German and French.");
    public required string ModelName { get; init; }
    public required string Credit { get; init; }
    public required string ModelPage { get; init; }
    /// Who made the ONNX export we actually download.
    public required string ConversionCredit { get; init; }
    public string ConversionPage => $"https://huggingface.co/{Repo}";
    public required string Repo { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<RemoteFile> Files { get; init; }
    /// File names (after flattening) the engine loads.
    public required string Encoder { get; init; }
    public required string DecoderJoint { get; init; }

    public long DownloadBytes => Files.Sum(f => f.Size);

    /// <summary>"690 MB".</summary>
    public string SizeText => $"{(long)Math.Round(DownloadBytes / 1_000_000.0, MidpointRounding.AwayFromZero)} MB";

    /// <summary>
    /// "312 of 688 MB · about 2 min left" — or "Checking and preparing…" once the files are
    /// in (the last tenth of <paramref name="progress"/> is checking and moving into place).
    /// </summary>
    /// <param name="started">When the download started (null: no time estimate).</param>
    public string ProgressText(double progress, DateTime? started, DateTime now)
    {
        if (IsPreparing(progress)) return Loc.T("Kontrollerar och förbereder för din dator…", "Checking and preparing for your PC…");
        var total = DownloadBytes / 1_000_000.0;
        var done = Math.Min(1, progress / 0.9) * total;
        var text = Loc.T($"{(long)done} av {(long)total} MB", $"{(long)done} of {(long)total} MB");
        if (started is { } start && progress > 0.03)
        {
            var elapsed = (now - start).TotalSeconds;
            var left = elapsed * (0.9 - progress) / progress;
            if (elapsed > 3 && left >= 5)
            {
                text += " · " + (left < 60
                    ? Loc.T($"ca {(long)(Math.Ceiling(left / 10) * 10)} s kvar", $"about {(long)(Math.Ceiling(left / 10) * 10)} s left")
                    : Loc.T($"ca {(long)Math.Round(left / 60, MidpointRounding.AwayFromZero)} min kvar",
                            $"about {(long)Math.Round(left / 60, MidpointRounding.AwayFromZero)} min left"));
            }
        }
        return text;
    }

    /// <summary>All files in; now they're checked and moved into place (progress 0.9 → 1).</summary>
    public static bool IsPreparing(double progress) => progress >= 0.899;

    /// <summary>
    /// The model to suggest on this PC: Swedish for anyone who speaks it — Windows in Swedish,
    /// or the region set to Sweden (plenty of Swedes run Windows in English) — the
    /// multilingual one for everyone else.
    /// </summary>
    public static SpeechModel Recommended =>
        RecommendedFor(CultureInfo.CurrentUICulture, RegionInfo.CurrentRegion.TwoLetterISORegionName);

    public static SpeechModel RecommendedFor(CultureInfo uiCulture, string region) =>
        uiCulture.TwoLetterISOLanguageName == "sv" || string.Equals(region, "SE", StringComparison.OrdinalIgnoreCase)
            ? Swedish : Multilingual;

    public Uri UrlFor(RemoteFile file) =>
        new($"https://huggingface.co/{Repo}/resolve/{Revision}/{file.Path}");

    public static readonly SpeechModel Swedish = new()
    {
        Id = "swedish",
        ModelName = "Klang Pianissimo",
        Credit = "Klang AI AB",
        ModelPage = "https://huggingface.co/KlangAI/pianissimo-sv",
        ConversionCredit = "Klang AI",
        // Klang AI's own int8 export (SmoothQuant), ~660 MB; WER within 0.2 points of fp32.
        Repo = "KlangAI/pianissimo-sv-onnx",
        Revision = "63730c6021234f26b9bbae9a07a04fec39e7a52e",
        Encoder = "encoder-model.int8.onnx",
        DecoderJoint = "decoder_joint-model.int8.onnx",
        Files =
        [
            new("config.json", 116, "f19eee59d2ba995f6d5cdb164e30dd64d2a8b7ed0ce9f2198ab68a5467445694"),
            new("decoder_joint-model.int8.onnx", 30025652, "0f9213242acd8874f2717c5e3cdb888d4e7673ddf3abb9ee3a2fd2eaf0acdd03"),
            new("encoder-model.int8.onnx", 630313965, "13288a5f4f009bf6bf8a3260044d098f6922e8caf1c564521d4962fa5749f66c"),
            new("nemo128.onnx", 138824, "5b4a84c52eeaa615dc46d781cc7e4598f9b432184831d57493c48adcd01371a9"),
            new("vocab.txt", 93939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        ],
    };

    public static readonly SpeechModel Multilingual = new()
    {
        Id = "multilingual",
        ModelName = "Parakeet Ultra",
        Credit = "Moondream",
        ModelPage = "https://huggingface.co/moondream/parakeet-ultra",
        ConversionCredit = "Olicorne",
        // Community ONNX export of Parakeet Ultra (int8 MatMulNBits), by Olicorne.
        Repo = "Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx",
        Revision = "a1fe742758beb8ddc918bff00e1e319fe7e4d3e3",
        Encoder = "encoder-model.int8.onnx",
        DecoderJoint = "decoder_joint-model.int8.onnx",
        Files =
        [
            new("config.json", 97, "666903c76b9798caf2c210afd4f6cd60b08a8dbf9800ec8d7a3bc0d2148ac466"),
            new("int8/decoder_joint-model.int8.onnx", 18203490, "667ffde2242a560923cba6bc7f69ef03d736f8a36ca9588f2e6f9fa818cf2fc1"),
            new("int8/encoder-model.int8.onnx", 649537325, "8a2b47169cf3f1b114010e12c0221bc6f07203289477a65a44847b9e3f1e01ed"),
            new("nemo128.onnx", 139764, "a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f"),
            new("vocab.txt", 93939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        ],
    };

    public static IReadOnlyList<SpeechModel> All { get; } = [Swedish, Multilingual];

    public static SpeechModel? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
