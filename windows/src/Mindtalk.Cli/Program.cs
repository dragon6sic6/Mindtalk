using System.Diagnostics;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Platform;

// Self-tests that run against the same code the app uses — the Windows counterpart of the
// Mac app's `--transcribe`, `--clean` and `--install` switches.

Console.OutputEncoding = System.Text.Encoding.UTF8;   // → – å ä ö

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
          --record-test [--device <id>]            Live microphone check (no speech model needed)
          --dictate [--model ...] [--seconds 5]    Record from the default input and transcribe
          --mics [--listen]                        Inputs with their kind, the default, the built-in mic,
                                                   access; --listen: 3 s of the picker's level preview
          --spell <word>...                        Ordinary Swedish/English word? (Windows spell checkers)
          --focus [--seconds 10]                   Every second: the app in front, its focused field's kind,
                                                   password field? (never the field's text)
          --media-list                             Media sessions, sound sessions, volume (changes nothing)
          --cue start|done|celebrate               Play a cue (at its normal, low volume)
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

    case "--record-test":
    {
        // Live microphone: start/stop twice per input, 16 kHz finite samples, level callbacks.
        var microphones = WasapiRecorder.List();
        Console.WriteLine($"Inputs: {(microphones.Count == 0 ? "none" : string.Join(", ", microphones.Select(m => m.Name)))}");
        var ids = new List<string?> { null };
        if (Option(args, "--device") is { } device) ids.Add(device);
        var failed = false;
        using var recorder = new WasapiRecorder();
        var levels = 0;
        var loudest = 0f;
        recorder.Level += l => { Interlocked.Increment(ref levels); if (l > loudest) loudest = l; };
        foreach (var id in ids)
        foreach (var round in new[] { 1, 2 })
        {
            levels = 0;
            loudest = 0;
            var label = id is null ? "system default" : microphones.FirstOrDefault(m => m.Id == id)?.Name ?? id;
            try
            {
                var started = Stopwatch.StartNew();
                recorder.Start(id);
                Thread.Sleep(1000);
                var samples = recorder.Stop();
                var seconds = samples.Length / (double)WasapiRecorder.SampleRate;
                var finite = samples.All(float.IsFinite);
                var ok = finite && levels > 0 && seconds is > 0.7 and < 1.5;
                failed |= !ok;
                Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {label} #{round}: {seconds:0.00} s of audio in {started.Elapsed.TotalSeconds:0.00} s, " +
                                  $"{levels} level callbacks, loudest {loudest:0.000}, finite {finite}");
            }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine($"FAIL {label} #{round}: {DictationController.MicrophoneProblem(ex)}");
            }
        }
        return failed ? 1 : 0;
    }

    case "--dictate":
    {
        // Record for a few seconds from the default input, then transcribe and clean up.
        var model = PickModel(args);
        if (!store.IsInstalled(model)) { Console.Error.WriteLine($"{model.ModelName} isn't installed."); return 1; }
        using var engine = new ParakeetEngine(store.DirectoryFor(model), model);
        using var recorder = new WasapiRecorder();
        var seconds = int.TryParse(Option(args, "--seconds"), out var s) ? s : 5;
        Console.WriteLine($"Speak now ({seconds} s)…");
        recorder.Start(null);
        Thread.Sleep(seconds * 1000);
        var samples = recorder.Stop();
        var raw = engine.Transcribe(samples);
        Console.WriteLine(raw);
        Console.WriteLine("→ " + TextCleanup.Apply(raw));
        return 0;
    }

    case "--mics":
    {
        using var mics = new Microphones();
        Console.WriteLine($"Microphone access allowed: {MicrophoneAccess.IsAllowed}");
        foreach (var device in mics.Devices)
            Console.WriteLine($"  {device.Kind,-9} {device.Name}\n            {device.Id}\n            {Microphones.Describe(device.Id)}");
        if (mics.Devices.Count == 0) Console.WriteLine("  (no active inputs)");
        Console.WriteLine($"System default: {mics.SystemDefault?.Name ?? "none"}");
        Console.WriteLine($"Built-in:       {mics.BuiltIn?.Name ?? "none"}");
        foreach (var choice in new[] { Microphones.BuiltInChoice, Microphones.SystemChoice, "{missing-device}" })
            Console.WriteLine($"Choice {choice,-16} → {mics.SelectedDeviceId(choice) ?? "(system input)"} · \"{mics.SelectionName(choice)}\"");
        if (args.Contains("--listen"))
        {
            var updates = 0;
            mics.LevelsChanged += () => Interlocked.Increment(ref updates);
            mics.StartMonitor(Microphones.BuiltInChoice);
            Thread.Sleep(3000);
            var history = mics.History;
            var hearing = mics.Hearing;
            mics.StopMonitor();
            Console.WriteLine($"Preview: {updates} updates in 3 s, peak {history.Max():0.000}, \"{hearing.Text}\" (good: {hearing.Good?.ToString() ?? "-"})");
        }
        return 0;
    }

    case "--spell":
    {
        Console.WriteLine($"Spell-check languages: {(SpellCheck.Languages.Count == 0 ? "none" : string.Join(", ", SpellCheck.Languages))}");
        foreach (var word in args.Skip(1))
            Console.WriteLine($"  {word}: {(SpellCheck.IsDictionaryWord(word) ? "dictionary word" : "not a dictionary word")}");
        return 0;
    }

    case "--focus":
    {
        // The inspector's own log line names the app and the kind of control — never its text.
        Trace.Listeners.Add(new ConsoleTraceListener());
        using var inspector = new FocusInspector();
        var seconds = int.TryParse(Option(args, "--seconds"), out var n) ? n : 10;
        for (var i = 0; i < seconds; i++)
        {
            var watch = Stopwatch.StartNew();
            var kind = await inspector.FocusedFieldAsync(0);
            var took = watch.ElapsedMilliseconds;
            var secure = await inspector.IsSecureFieldFocusedAsync();
            Console.WriteLine($"  → {kind}, password field: {secure} ({took} ms)");
            Thread.Sleep(1000);
        }
        return 0;
    }

    case "--media-list":
    {
        var (media, audio, volume) = MediaControl.Inspect();
        Console.WriteLine("Media sessions:");
        foreach (var session in media) Console.WriteLine($"  {session.App}: {session.Status}");
        if (media.Count == 0) Console.WriteLine("  (none)");
        Console.WriteLine("Sound sessions on the default output:");
        foreach (var player in audio) Console.WriteLine($"  {player.Process} (pid {player.ProcessId}): {(player.Active ? "active" : "inactive")}");
        if (audio.Count == 0) Console.WriteLine("  (none)");
        Console.WriteLine($"Master volume: {(volume is { } v ? $"{v:0.00}" : "unknown")}");
        return 0;
    }

    case "--cue":
    {
        var cues = new Cues(() => true);
        switch (args.Length > 1 ? args[1] : "start")
        {
            case "done": cues.Done(); break;
            case "celebrate": cues.Celebrate(); break;
            default: cues.Start(); break;
        }
        Thread.Sleep(1200);   // let it play out
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown option {args[0]}. Try --help.");
        return 2;
}
