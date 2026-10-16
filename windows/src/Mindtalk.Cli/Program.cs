using System.Diagnostics;
using Mindtalk.Core;
using Mindtalk.Core.Speech;

// Self-tests that run against the same code the app uses — the Windows counterpart of the
// Mac app's `--transcribe`, `--clean` and `--install` switches.

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static SpeechModel PickModel(string[] args) =>
    SpeechModel.Find(Option(args, "--model") ?? "swedish")
    ?? throw new ArgumentException("--model must be 'swedish' or 'multilingual'.");

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        mindtalk-cli — Mindtalk for Windows, self-tests

          --clean "<text>"                         Run the text cleanup only
          --install [--model swedish|multilingual] Download and verify a speech model
          --transcribe <file.wav> [--model ...] [--clean]
                                                   Transcribe a WAV file (any rate; mixed down to 16 kHz mono)
        """);
    return 0;
}

var store = new ModelStore();

switch (args[0])
{
    case "--clean":
        Console.WriteLine(TextCleanup.Apply(args.Length > 1 ? args[1] : ""));
        return 0;

    case "--install":
    {
        var model = PickModel(args);
        var watch = Stopwatch.StartNew();
        var last = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (percent == last) return;
            last = percent;
            Console.Write($"\r{model.ModelName}: {percent}% of {model.DownloadBytes / 1_000_000} MB");
        });
        await store.InstallAsync(model, progress);
        Console.WriteLine($"\nInstalled in {store.DirectoryFor(model)} ({watch.Elapsed:mm\\:ss}).");
        return 0;
    }

    case "--transcribe":
    {
        var file = args.Length > 1 ? args[1] : throw new ArgumentException("Give a WAV file.");
        var model = PickModel(args);
        if (!store.IsInstalled(model)) { Console.Error.WriteLine($"{model.ModelName} isn't installed. Run --install --model {model.Id}."); return 1; }

        var samples = WavFile.Read16kMono(file);
        var load = Stopwatch.StartNew();
        using var engine = new ParakeetEngine(store.DirectoryFor(model), model);
        var loaded = load.Elapsed;

        var run = Stopwatch.StartNew();
        var text = engine.Transcribe(samples);
        var took = run.Elapsed;
        Console.WriteLine(text);
        if (args.Contains("--clean")) Console.WriteLine("→ " + TextCleanup.Apply(text));
        Console.Error.WriteLine($"[{samples.Length / 16000.0:0.0} s of audio · load {loaded.TotalSeconds:0.0} s · transcribe {took.TotalSeconds:0.00} s]");
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown option {args[0]}. Try --help.");
        return 2;
}
