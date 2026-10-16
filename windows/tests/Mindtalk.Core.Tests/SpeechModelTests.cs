using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Mindtalk.Core.Speech;

namespace Mindtalk.Core.Tests;

[Collection("Loc")]
public class SpeechModelTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);

    [Fact]
    public void Metadata()
    {
        Assert.Equal("SV", SpeechModel.Swedish.Code);
        Assert.Equal("EN", SpeechModel.Multilingual.Code);
        Assert.Equal("Klang AI", SpeechModel.Swedish.ConversionCredit);
        Assert.Equal("https://huggingface.co/KlangAI/pianissimo-sv-onnx", SpeechModel.Swedish.ConversionPage);
        Assert.Equal("Olicorne", SpeechModel.Multilingual.ConversionCredit);
        Assert.Equal("https://huggingface.co/Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx", SpeechModel.Multilingual.ConversionPage);
        Assert.Equal("661 MB", SpeechModel.Swedish.SizeText);        // 660 572 496 bytes
        Assert.Equal("668 MB", SpeechModel.Multilingual.SizeText);   // 667 974 615 bytes
        using (Language.Swedish()) Assert.Equal("Bäst på svenska – hänger med i engelska också.", SpeechModel.Swedish.Pitch);
        using (Language.English()) Assert.Equal("25 languages, including Swedish, English, German and French.", SpeechModel.Multilingual.Pitch);
    }

    [Fact]
    public void Recommended()
    {
        Assert.Same(SpeechModel.Swedish, SpeechModel.RecommendedFor(CultureInfo.GetCultureInfo("sv-SE"), "US"));
        Assert.Same(SpeechModel.Swedish, SpeechModel.RecommendedFor(CultureInfo.GetCultureInfo("sv-FI"), "FI"));
        Assert.Same(SpeechModel.Swedish, SpeechModel.RecommendedFor(CultureInfo.GetCultureInfo("en-US"), "SE"));
        Assert.Same(SpeechModel.Multilingual, SpeechModel.RecommendedFor(CultureInfo.GetCultureInfo("en-GB"), "GB"));
    }

    [Fact]
    public void ProgressText()
    {
        var model = SpeechModel.Swedish;
        using (Language.Swedish())
        {
            Assert.Equal("0 av 660 MB", model.ProgressText(0, Now, Now));
            Assert.Equal("330 av 660 MB", model.ProgressText(0.45, null, Now));
            // Too early for an estimate: under 3 % or 3 seconds.
            Assert.Equal("14 av 660 MB", model.ProgressText(0.02, Now.AddSeconds(-30), Now));
            Assert.Equal("330 av 660 MB", model.ProgressText(0.45, Now.AddSeconds(-3), Now));
            Assert.Equal("330 av 660 MB · ca 30 s kvar", model.ProgressText(0.45, Now.AddSeconds(-30), Now));
            Assert.Equal("330 av 660 MB · ca 30 s kvar", model.ProgressText(0.45, Now.AddSeconds(-21), Now));   // 21 s left → 30
            Assert.Equal("220 av 660 MB · ca 2 min kvar", model.ProgressText(0.3, Now.AddSeconds(-60), Now));
            // Under five seconds left: no estimate.
            Assert.Equal("638 av 660 MB", model.ProgressText(0.87, Now.AddSeconds(-100), Now));
            Assert.Equal("Kontrollerar och förbereder för din dator…", model.ProgressText(0.899, Now, Now));
            Assert.Equal("Kontrollerar och förbereder för din dator…", model.ProgressText(1, Now, Now));
        }
        using (Language.English())
        {
            Assert.Equal("220 of 660 MB · about 2 min left", model.ProgressText(0.3, Now.AddSeconds(-60), Now));
            Assert.Equal("330 of 660 MB · about 30 s left", model.ProgressText(0.45, Now.AddSeconds(-30), Now));
            Assert.Equal("Checking and preparing for your PC…", model.ProgressText(0.95, Now, Now));
        }
        Assert.False(SpeechModel.IsPreparing(0.898));
        Assert.True(SpeechModel.IsPreparing(0.899));
    }

    [Fact]
    public void DownloadErrorsInPlainWords()
    {
        using var _ = Language.Swedish();
        const string dropped = "Internetanslutningen bröts. Kontrollera nätverket och försök igen.";
        const string unreachable = "Kunde inte nå servern just nu. Försök igen om en stund.";
        const string fallback = "Något gick fel under nedladdningen. Försök igen.";
        Assert.Equal(unreachable, SpeechService.PlainWords(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.Equal(unreachable, SpeechService.PlainWords(new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.Equal(dropped, SpeechService.PlainWords(new HttpRequestException(HttpRequestError.NameResolutionError), online: false));
        Assert.Equal(dropped, SpeechService.PlainWords(new HttpIOException(HttpRequestError.ResponseEnded)));
        Assert.Equal(dropped, SpeechService.PlainWords(new IOException("x", new System.Net.Sockets.SocketException(10054))));
        Assert.Equal("Det finns inte tillräckligt med ledigt utrymme på din dator.",
                     SpeechService.PlainWords(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.Equal(fallback, SpeechService.PlainWords(new HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound)));
        Assert.Equal(fallback, SpeechService.PlainWords(new InvalidDataException("checksum")));
        using (Language.English())
            Assert.Equal("There isn’t enough free space on your PC.",
                         SpeechService.PlainWords(new IOException("disk full", unchecked((int)0x80070027))));
    }
}

public sealed class ModelStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly byte[] One = Encoding.UTF8.GetBytes("first file");
    private static readonly byte[] Two = Encoding.UTF8.GetBytes("the second file, a little longer");

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// A model whose files are already in the staging folder — no network needed.
    private static SpeechModel Model(string? wrongHash = null) => new()
    {
        Id = "test",
        ModelName = "Test",
        Credit = "Test",
        ModelPage = "https://example.invalid",
        ConversionCredit = "Test",
        Repo = "test/none",
        Revision = "0",
        Encoder = "one.bin",
        DecoderJoint = "two.bin",
        Files = [new("one.bin", One.Length, Hash(One)), new("sub/two.bin", Two.Length, wrongHash ?? Hash(Two))],
    };

    private sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) { lock (Values) Values.Add(value); }
    }

    private void Stage(ModelStore store, SpeechModel model)
    {
        var staging = store.StagingFor(model);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "one.bin"), One);
        File.WriteAllBytes(Path.Combine(staging, "two.bin"), Two);
    }

    [Fact]
    public async Task VerifiedFilesAreMovedIntoPlaceAtTheEnd()
    {
        var store = new ModelStore(_temp.Path);
        var model = Model();
        Stage(store, model);
        Assert.False(store.IsInstalled(model));

        var progress = new Recorder();
        await store.InstallAsync(model, progress);

        Assert.True(store.IsInstalled(model));
        Assert.False(Directory.Exists(store.StagingFor(model)));
        Assert.Equal(One, File.ReadAllBytes(Path.Combine(store.DirectoryFor(model), "one.bin")));
        // Downloading ends at 0.9; checking and moving take the rest.
        Assert.Contains(0.9, progress.Values);
        Assert.Equal(1, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.All(progress.Values.Where(v => v > 0.9), v => Assert.True(v <= 1));
    }

    [Fact]
    public async Task ABadFileIsRefusedAndNothingIsInstalled()
    {
        var store = new ModelStore(_temp.Path);
        var model = Model(wrongHash: new string('0', 64));
        Stage(store, model);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(model));
        Assert.False(store.IsInstalled(model));
        Assert.False(Directory.Exists(store.DirectoryFor(model)));
        // The good file stays for the next try; the bad one goes.
        Assert.True(File.Exists(Path.Combine(store.StagingFor(model), "one.bin")));
        Assert.False(File.Exists(Path.Combine(store.StagingFor(model), "two.bin")));
        Assert.False(store.IsInstalling(model));
    }

    [Fact]
    public async Task UninstallAndLeftovers()
    {
        var store = new ModelStore(_temp.Path);
        var model = Model();
        Stage(store, model);
        await store.InstallAsync(model);

        // A download interrupted by quitting, and a partial file from an older version.
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".download-swedish"));
        File.WriteAllText(Path.Combine(store.DirectoryFor(model), "old.onnx.part"), "x");
        store.RemoveLeftovers();
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, ".download-swedish")));
        Assert.False(File.Exists(Path.Combine(store.DirectoryFor(model), "old.onnx.part")));
        Assert.True(store.IsInstalled(model));

        store.Uninstall(model);
        Assert.False(store.IsInstalled(model));
        Assert.False(Directory.Exists(store.DirectoryFor(model)));
        store.Uninstall(model);   // nothing there: fine
        new ModelStore(Path.Combine(_temp.Path, "missing")).RemoveLeftovers();
    }
}
