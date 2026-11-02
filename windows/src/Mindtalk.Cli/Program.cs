using System.Diagnostics;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Meetings.Engine;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Voices;
using Mindtalk.Platform;
using Mindtalk.Platform.Meetings;

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
          --speakers-install                       Download and verify the speaker models (~34 MB)
          --meeting-file <file> [--model ...] [--remember] [--me <name>] [--out <dir>] [--sv|--en]
                                                   Import a recording as a meeting: speakers, text, exports
          --meeting-live [--seconds 30] [--round] [--device <id>] [--from-file <wav>] [--model ...]
                                                   Record a meeting from the microphone: live text, then the final pass
          --calls [--seconds 30]                   Call apps using a microphone now, then calls starting and ending
          --call-sound <pid> [--seconds 5]         The mic plus that process tree's sound (process loopback)
          --speaker-eval <wav>... --seg <onnx> --emb <onnx> [--threshold 0.5]
                                                   Diarize files and compare voiceprints
          --ai-help                                The writers' self-tests: Gemma, summaries, Ask
        """);
    return 0;
}

if (args[0].StartsWith("--ai-", StringComparison.Ordinal)) return await AiSelfTests.RunAsync(args);

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

    case "--speakers-install":
    {
        var models = new SpeakerModels();
        if (models.IsInstalled) { Console.WriteLine($"Already installed in {models.Folder}."); return 0; }
        var last = -1;
        await models.InstallAsync(new Progress<double>(p =>
        {
            if ((int)(p * 100) == last) return;
            last = (int)(p * 100);
            Console.Write($"\rSpeaker models: {last}% of {SpeakerModels.DownloadBytes / 1_000_000} MB");
        }));
        Console.WriteLine($"\nInstalled in {models.Folder}.");
        return 0;
    }

    case "--meeting-file":
    case "--meeting-live":
    {
        // A meeting end to end, without the app: import a file (or record from the mic for a while),
        // listen through it again with speakers, print the segments and write the exports.
        var live = args[0] == "--meeting-live";
        var model = PickModel(args);
        if (!store.IsInstalled(model)) { Console.Error.WriteLine($"{model.ModelName} isn't installed. Run --install --model {model.Id}."); return 1; }
        if (args.Contains("--sv")) Loc.IsSwedish = true;
        if (args.Contains("--en")) Loc.IsSwedish = false;
        Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
        using var speech = new SpeechService(store, model);
        speech.Use(model);
        while (speech.Status.Phase is ModelPhase.Loading) Thread.Sleep(100);
        if (speech.Status.Phase != ModelPhase.Ready) { Console.Error.WriteLine($"Speech model: {speech.Status.Phase} {speech.Status.Error}"); return 1; }

        var folder = Option(args, "--out") ?? Path.Combine(Path.GetTempPath(), "mindtalk-meeting-test");
        Directory.CreateDirectory(folder);
        var speakers = new SpeakerModels();
        Console.WriteLine($"Speaker models: {(speakers.IsInstalled ? "installed" : "not installed — one speaker (run --speakers-install)")}");
        var settings = new AppSettings { RememberVoices = args.Contains("--remember"), UserName = Option(args, "--me") };
        var meetings = new MeetingStore(Path.Combine(folder, "Meetings"));
        var voices = new VoiceStore(Path.Combine(folder, "voices.json"));
        using var engine = new MeetingEngine(new MeetingEngineHost
        {
            Meetings = meetings,
            Voices = voices,
            Settings = settings,
            Transcriber = speech,
            Language = () => model.Id,
            SpeakerModels = speakers,
            OpenAudio = start => Option(args, "--from-file") is { } wav
                ? new FileMeetingAudio(WavFile.Read16kMono(wav))
                : new MeetingAudio(() => Option(args, "--device"), () => start.CallProcessId),
            Media = new MeetingMedia(),
        });

        var watch = Stopwatch.StartNew();
        Meeting meeting;
        if (live)
        {
            var seconds = int.TryParse(Option(args, "--seconds"), out var s) ? s : 30;
            var current = engine.Start(new MeetingStart(MeetingSource.Room, RoundOfNames: args.Contains("--round")));
            var shown = 0;
            string? lastPartial = null;
            var printing = new object();
            current.Changed += () =>
            {
                lock (printing) Show();
            };
            void Show()
            {
                var segments = current.Meeting.Segments;
                for (; shown < segments.Count; shown++)
                {
                    var seg = segments[shown];
                    Console.WriteLine($"  live {Meeting.Clock(seg.Start),6} {current.Meeting.SpeakerById(seg.SpeakerId)?.DisplayName,-12} {seg.Text}");
                }
                if (current.Partial is { } p && p != lastPartial) { lastPartial = p; Console.WriteLine($"       … {p}"); }
            }
            Console.WriteLine($"Recording for {seconds} s — speak now.");
            Thread.Sleep(seconds * 1000);
            Console.WriteLine($"Stopping after {current.Elapsed.TotalSeconds:0.0} s of audio…");
            var stopping = Stopwatch.StartNew();
            await current.StopAsync();
            Console.WriteLine($"Finished {stopping.Elapsed.TotalSeconds:0.0} s after stopping.");
            meeting = current.Meeting;
        }
        else
        {
            var file = args.Length > 1 ? Path.GetFullPath(args[1]) : throw new ArgumentException("Give a recording.");
            Console.WriteLine($"Can import: {engine.CanImport(file)}");
            meeting = await engine.ImportAsync(file);
        }
        Console.WriteLine($"\n{meeting.Title} — {meeting.State}{(meeting.Error is null ? "" : $" ({meeting.Error})")}, {Meeting.Length(meeting.Duration)}, " +
                          $"{meeting.PeopleLine}, done in {watch.Elapsed.TotalSeconds:0.0} s");
        foreach (var seg in meeting.Segments)
            Console.WriteLine($"  {Meeting.Clock(seg.Start),6}–{Meeting.Clock(seg.End),-6} {meeting.SpeakerById(seg.SpeakerId)?.DisplayName,-12} {seg.Text}");
        Console.WriteLine($"Audio: {meetings.AudioPath(meeting) ?? "none"}");
        foreach (var format in new[] { ExportFormat.Markdown, ExportFormat.Text, ExportFormat.Subtitles, ExportFormat.Word, ExportFormat.Pdf, ExportFormat.Audio })
        {
            var target = Path.Combine(folder, MeetingExport.FileName(meeting, format));
            try
            {
                MeetingExport.Write(meeting, format, target, meetings.AudioPath(meeting), new MeetingMedia());
                Console.WriteLine($"Exported {target} ({new FileInfo(target).Length / 1024.0:0.0} KB)");
            }
            catch (Exception ex) { Console.WriteLine($"Export {format} failed: {ex.Message}"); }
        }
        return meeting.State == MeetingState.Done ? 0 : 1;
    }

    case "--calls":
    {
        // Lists the call apps using a microphone, then watches for calls starting and ending.
        var seconds = int.TryParse(Option(args, "--seconds"), out var s) ? s : 30;
        Console.WriteLine($"Default output: {OutputDevices.DefaultName()} (headphones: {OutputDevices.DefaultIsHeadphones()})");
        if (args.Contains("--all"))
        {
            // Holds the mic open itself, so the session list has at least one entry to show.
            using var mic = new MeetingAudio(() => null, () => null);
            mic.Start();
            Thread.Sleep(500);
            foreach (var (pid, name) in CallWatcher.CaptureSessions()) Console.WriteLine($"Capture session: {name} (pid {pid})");
            mic.Stop();
        }
        var now = CallWatcher.Detect();
        Console.WriteLine(now.Count == 0 ? "No call app is using a microphone." : string.Join("\n", now.Select(c => $"In a call: {c.App} (pid {c.ProcessId})")));
        using var watcher = new CallWatcher();
        watcher.Started += c => Console.WriteLine($"{DateTime.Now:HH:mm:ss} started: {c.App} (pid {c.ProcessId})");
        watcher.Ended += c => Console.WriteLine($"{DateTime.Now:HH:mm:ss} ended:   {c.App} (pid {c.ProcessId})");
        Console.WriteLine($"Watching for {seconds} s…");
        Thread.Sleep(seconds * 1000);
        return 0;
    }

    case "--call-sound":
    {
        // Records a process's sound (and its children's) through process loopback for a few seconds.
        var pid = int.Parse(args[1]);
        var seconds = int.TryParse(Option(args, "--seconds"), out var s) ? s : 5;
        using var audio = new MeetingAudio(() => null, () => pid);
        var count = 0L;
        var peak = 0f;
        audio.Samples += samples => { Interlocked.Add(ref count, samples.Length); foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v)); };
        audio.Start();
        Console.WriteLine($"Hears the call: {audio.HearsCall}");
        Thread.Sleep(seconds * 1000);
        audio.Stop();
        Console.WriteLine($"{count / 16000.0:0.0} s of mixed audio, peak {peak:0.000}");
        return 0;
    }

    case "--speaker-eval":
    {
        // Compares embedding models: diarizes each file and prints how alike the voices are.
        var seg = Option(args, "--seg")!;
        var emb = Option(args, "--emb")!;
        var threshold = float.TryParse(Option(args, "--threshold"), System.Globalization.CultureInfo.InvariantCulture, out var th) ? th : 0.5f;
        using var analyzer = new Mindtalk.Core.Meetings.Engine.SherpaSpeakerAnalyzer(seg, emb, threshold);
        Console.WriteLine($"Embedding dimension {analyzer.Dimension}");
        foreach (var file in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
        {
            var samples = WavFile.Read16kMono(file);
            var watch = Stopwatch.StartNew();
            var turns = analyzer.Diarize(samples);
            Console.WriteLine($"{Path.GetFileName(file)}: {samples.Length / 16000.0:0.0} s, {turns.Select(t => t.Speaker).Distinct().Count()} speakers, {watch.Elapsed.TotalSeconds:0.0} s");
            foreach (var t in turns) Console.WriteLine($"  {t.Start,7:0.00}–{t.End,7:0.00}  S{t.Speaker}");
            var prints = turns.Where(t => t.Length > 1.5)
                .Select(t => (t.Speaker, Print: analyzer.Embed(samples[(int)(t.Start * 16000)..Math.Min(samples.Length, (int)(t.End * 16000))])))
                .Where(p => p.Print is not null).ToList();
            for (var i = 0; i < prints.Count; i++)
                Console.WriteLine("  " + string.Join(" ", prints.Select(p => $"{Mindtalk.Core.Voices.VoiceStore.Cosine(prints[i].Print!, p.Print!),5:0.00}")) + $"   S{prints[i].Speaker}");
        }
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown option {args[0]}. Try --help.");
        return 2;
}
