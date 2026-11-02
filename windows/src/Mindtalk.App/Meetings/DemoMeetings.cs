#if DEBUG
using System.Collections.Concurrent;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// A pretend meeting engine and Ask, for screenshots and for building the meeting windows
/// before the real engine is there (debug builds, demo mode, only while App.MeetingService
/// is null). It plays the launch meeting from the Mac's screenshots: speakers, words being
/// written, levels, a voice it thinks it knows.
///
///   --demo-live-meeting          a meeting under way, the live page open
///   --demo-round-of-names        a meeting starting with a round of names
///   --demo-new-voice             a meeting where someone new is talking
///   --demo-finishing             a meeting that ends after a few seconds (finishing, summarizing, done)
///   --demo-meeting-panel         the floating panel shown even while Mindtalk is in front
///   --demo-call-box              "Take notes in this Teams call?" after a second
///   --demo-meeting &lt;index|title&gt; a meeting's page; with --demo-tab text, its text
///   --demo-player                the launch meeting with audio, the player showing
///   --demo-ask &lt;question&gt;        the answer sheet for a question about all meetings
///   --demo-search &lt;words&gt;        the Meetings page searching
///   --demo-empty                 no meetings yet
/// </summary>
internal static class DemoMeetings
{
    private static readonly string[] Args = Environment.GetCommandLineArgs();

    private static string? Value(string flag)
    {
        var i = Array.IndexOf(Args, flag);
        return i > 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    public static void Start(App app)
    {
        if (!Demo.On) return;
        if (app.MeetingService is null)
        {
            app.MeetingService = new Service(app);
            app.MeetingHub.Attach();
        }
        app.Asker ??= new Asker(app);

        if (Demo.Has("--demo-empty"))
            foreach (var m in app.Meetings.All) app.Meetings.Delete(m.Id);
        if (Demo.Has("--demo-player")) AddAudio(app);
        var hub = app.MeetingHub;
        var service = app.MeetingService as Service;

        if (Demo.Has("--demo-live-meeting") || Demo.Has("--demo-new-voice") || Demo.Has("--demo-finishing") || Demo.Has("--demo-meeting-panel"))
        {
            hub.Start(roundOfNames: false, showWindow: !Demo.Has("--demo-meeting-panel"));
            if (Demo.Has("--demo-finishing")) _ = EndSoon(hub);
        }
        else if (Demo.Has("--demo-round-of-names")) hub.Start(roundOfNames: true);
        if (Demo.Has("--demo-call-box") && service is not null) _ = service.RingSoon();
        if (Value("--demo-meeting") is { } which)
        {
            var all = app.Meetings.All;
            var meeting = int.TryParse(which, out var index) && index < all.Count ? all[index]
                : all.FirstOrDefault(m => m.Title.Contains(which, StringComparison.CurrentCultureIgnoreCase)) ?? all.FirstOrDefault();
            if (meeting is not null)
            {
                hub.OpenMeeting(meeting.Id, then: page => { if (Value("--demo-tab") == "text") page.ShowText(); });
            }
        }
        if (Value("--demo-search") is { } words)
            app.UI.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                await Task.Delay(400);
                MeetingsPage.Shown?.Search(words);
            });
        if (Value("--demo-ask") is { } question && app.MainWindowIfOpen is { } main)
            AskSheet.Show(app, main, hub, question, null, null);
    }

    private static async Task EndSoon(MeetingHub hub)
    {
        await Task.Delay(4000);
        await hub.End();
    }

    /// A quiet stand-in recording for the launch meeting, so the player has something to play.
    private static void AddAudio(App app)
    {
        var meeting = app.Meetings.All.FirstOrDefault(m => m.Summary?.Topics.Count > 0);
        if (meeting is null) return;
        const int rate = 8000;
        var seconds = (int)meeting.Duration;
        var path = Path.Combine(app.Meetings.FolderOf(meeting.Id), "audio.wav");
        using (var file = new BinaryWriter(File.Create(path)))
        {
            var bytes = rate * seconds;
            file.Write("RIFF"u8); file.Write(36 + bytes); file.Write("WAVE"u8);
            file.Write("fmt "u8); file.Write(16); file.Write((short)1); file.Write((short)1);
            file.Write(rate); file.Write(rate); file.Write((short)1); file.Write((short)8);
            file.Write("data"u8); file.Write(bytes);
            var random = new Random(3);
            var buffer = new byte[rate];
            for (var s = 0; s < seconds; s++)
            {
                for (var i = 0; i < rate; i++) buffer[i] = (byte)(128 + random.Next(-2, 3));
                file.Write(buffer);
            }
        }
        meeting.AudioFile = "audio.wav";
        app.Meetings.Save(meeting);
    }

    private static string L(string sv, string en) => Loc.T(sv, en);

    /// The launch meeting's lines: who (0, 1, 2 = Anna, Erik, Sara; 3 = someone new) and what.
    private static (int Speaker, string Text)[] Script => [
        (0, L("Hej allihop, ska vi börja med lanseringen?", "Hi everyone, shall we start with the launch?")),
        (1, L("Vi fick fler nedladdningar första dagen än under hela förra månaden.", "We had more downloads on the first day than in all of last month.")),
        (2, L("De flesta användarna finns i Sverige, men Norge och Danmark växer.", "Most users are in Sweden, but Norway and Denmark are growing.")),
        (1, L("Supporten får redan frågor på engelska.", "Support is already getting questions in English.")),
        (3, L("Får jag lägga till en sak om supporten?", "Can I add something about support?")),
        (1, L("Mötesfunktionen är det folk frågar mest om.", "The meetings feature is what people ask about most.")),
        (0, L("Då bygger vi mötesfunktionen härnäst. Är alla med på det?", "Then we build the meetings feature next. Is everyone on board?")),
        (2, L("Ja, absolut.", "Yes, absolutely.")),
        (2, L("Höstbudgeten behöver justeras före styrelsemötet.", "The autumn budget needs adjusting before the board meeting.")),
        (1, L("Jag tar fram siffrorna och skickar dem till Sara på torsdag.", "I'll pull the numbers together and send them to Sara on Thursday.")),
    ];

    private static readonly string[] Names = ["Anna", "Erik", "Sara"];

    // ---- The engine ----------------------------------------------------------------------

    private sealed class Service(App app) : IMeetingService
    {
        private readonly ConcurrentDictionary<Guid, double> _working = new();

        public ILiveMeeting? Current { get; private set; }
        public IReadOnlyDictionary<Guid, double> Working => _working;
        public bool SpeakerModelReady => true;
        public double? SpeakerModelProgress => null;
        public string? SpeakerModelError => null;
        public Task DownloadSpeakerModelAsync() => Task.CompletedTask;
        public event Action? Changed;
        public event Action<DetectedCall>? CallStarted;
        public event Action<DetectedCall>? CallEnded;

        public ILiveMeeting Start(MeetingStart start)
        {
            var prefilled = !start.RoundOfNames && (Demo.Has("--demo-live-meeting") || Demo.Has("--demo-new-voice") || Demo.Has("--demo-meeting-panel"));
            var live = new Live(app, this, start, prefilled);
            Current = live;
            Changed?.Invoke();
            return live;
        }

        public void Ended(Live live)
        {
            if (ReferenceEquals(Current, live)) Current = null;
            Changed?.Invoke();
        }

        public async Task RingSoon()
        {
            await Task.Delay(1000);
            var call = new DetectedCall("Teams", Environment.ProcessId, DateTime.Now);
            CallStarted?.Invoke(call);
            await Task.Delay(TimeSpan.FromMinutes(2));
            CallEnded?.Invoke(call);
        }

        public bool CanImport(string path) => MeetingHub.ImportTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

        public async Task<Meeting> ImportAsync(string path, CancellationToken ct = default)
        {
            var meeting = new Meeting
            {
                Source = MeetingSource.Import,
                ImportedFrom = Path.GetFileName(path),
                Started = File.GetLastWriteTime(path),
                Language = app.Engine.Id,
                State = MeetingState.Finishing,
                Duration = 14 * 60,
            };
            app.Meetings.Save(meeting);
            await Work(meeting.Id, 4, ct);
            Fill(meeting, Script.Length, named: false);
            meeting.State = MeetingState.Done;
            meeting.Summary = Summary(meeting);
            meeting.Title = L("Importerad inspelning", "Imported recording");
            app.Meetings.Save(meeting);
            return meeting;
        }

        public async Task ResummarizeAsync(Guid meetingId, CancellationToken ct = default)
        {
            if (app.Meetings.Get(meetingId) is not { } meeting) return;
            await Work(meetingId, 3, ct);
            meeting.Summary ??= Summary(meeting);
            meeting.Summary.Written = DateTime.Now;
            app.Meetings.Save(meeting);
        }

        private async Task Work(Guid id, double seconds, CancellationToken ct)
        {
            for (var p = 0.0; p < 1; p += 0.05)
            {
                _working[id] = p;
                app.UI.TryEnqueue(app.Refresh);
                await Task.Delay(TimeSpan.FromSeconds(seconds / 20), ct);
            }
            _working.TryRemove(id, out _);
            app.UI.TryEnqueue(app.Refresh);
        }
    }

    /// Segments for the first <paramref name="lines"/> lines of the script, a little apart.
    private static void Fill(Meeting meeting, int lines, bool named)
    {
        var script = Script;
        for (var s = 0; s < 4; s++)
            if (meeting.SpeakerById($"S{s + 1}") is null)
                meeting.Speakers.Add(new Speaker { Id = $"S{s + 1}", Name = named && s < 3 ? Names[s] : null });
        var t = 4.0;
        for (var i = 0; i < Math.Min(lines, script.Length); i++)
        {
            var (speaker, text) = script[i];
            var length = 2 + text.Length / 15.0;
            meeting.Segments.Add(new Segment(t, t + length, $"S{speaker + 1}", text));
            t += length + 1.5;
        }
    }

    private static MeetingSummary Summary(Meeting meeting) => new()
    {
        Overview = L("Lanseringen gick bättre än väntat. Teamet bygger mötesfunktionen härnäst, och höstbudgeten justeras före styrelsemötet.",
                     "The launch went better than expected. The team builds the meetings feature next, and the autumn budget is adjusted before the board meeting."),
        Topics =
        [
            new(L("Lanseringen", "The launch"),
            [
                new(L("Fler nedladdningar första dagen än hela förra månaden.", "More downloads on the first day than in all of last month."), meeting.Segments.ElementAtOrDefault(1)?.Start),
                new(L("Supporten får redan frågor på engelska.", "Support is already getting questions in English."), meeting.Segments.ElementAtOrDefault(3)?.Start),
            ]),
        ],
        Decisions = [new(L("Mötesfunktionen byggs härnäst.", "The meetings feature is built next."), meeting.Segments.ElementAtOrDefault(6)?.Start)],
        Todos = [new(L("Skicka siffrorna till Sara på torsdag.", "Send the numbers to Sara on Thursday."), meeting.Speakers.ElementAtOrDefault(1)?.Name, meeting.Segments.ElementAtOrDefault(9)?.Start)],
        Writer = "Gemma 4 E4B",
        Written = DateTime.Now,
    };

    /// A meeting under way: a line every few seconds, typed out word by word.
    private sealed class Live : ILiveMeeting
    {
        private readonly App _app;
        private readonly Service _service;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
        private readonly DateTime _started = DateTime.Now;
        private readonly double _offset;
        private readonly Random _random = new(5);
        private int _line, _word;
        private double _pausedAt = -1, _pausedTotal, _nextAt, _finishStarted;
        private bool _roundOfNames;
        private int _roundStep;

        public Live(App app, Service service, MeetingStart start, bool prefilled)
        {
            _app = app;
            _service = service;
            _roundOfNames = start.RoundOfNames;
            Meeting = new Meeting
            {
                Started = DateTime.Now,
                Source = start.Source,
                Language = app.Engine.Id,
                State = MeetingState.Recording,
                Title = start.CallApp is { } callApp ? L($"{callApp}-samtal", $"{callApp} call") : "",
            };
            HearsCall = start.Source == MeetingSource.Call;
            if (prefilled)
            {
                // Under way: Anna is known, Erik's voice is suggested, Sara (and someone new) not yet named.
                Fill(Meeting, 4, named: false);
                Meeting.SpeakerById("S1")!.Name = "Anna";
                _line = 4;
                _offset = 6 * 60 + 12;
                if (!Demo.Has("--demo-new-voice")) Suggestion = new VoiceSuggestion("S2", Guid.NewGuid(), "Erik", 0.81f);
                Meeting.Notes.Add(new MeetingNote(30, L("Kolla siffrorna för Norge", "Check the numbers for Norway")));
                Meeting.Marks.Add(new MeetingMark(45, MarkKind.Important));
                Partial = L("Får jag lägga till", "Can I add");
                PartialSpeakerId = "S4";
                _word = 4;
            }
            _timer = app.UI.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(90);
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public Meeting Meeting { get; }
        public MeetingState State { get; private set; } = MeetingState.Recording;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(Now);
        public string? Partial { get; private set; }
        public string? PartialSpeakerId { get; private set; }
        public float Level { get; private set; }
        public bool HearsCall { get; }
        public bool InRoundOfNames => _roundOfNames;
        public VoiceSuggestion? Suggestion { get; private set; }
        public double FinishProgress { get; private set; }
        public event Action? Changed;

        private double Seconds => (DateTime.Now - _started).TotalSeconds;
        private double Now => _offset + (_pausedAt >= 0 ? _pausedAt : Seconds) - _pausedTotal;

        private void Tick()
        {
            if (State is MeetingState.Finishing or MeetingState.Summarizing)
            {
                var part = (Seconds - _finishStarted) / 3;
                if (State == MeetingState.Finishing && part >= 1) { State = MeetingState.Summarizing; _finishStarted = Seconds; part = 0; }
                else if (State == MeetingState.Summarizing && part >= 1) { Finish(); return; }
                FinishProgress = State == MeetingState.Finishing ? part * 0.6 : 0.6 + part * 0.4;
                Changed?.Invoke();
                return;
            }
            if (State != MeetingState.Recording) return;
            Level = (float)Math.Clamp(0.25 + 0.35 * Math.Sin(Seconds * 7) * Math.Sin(Seconds * 2.3) + _random.NextDouble() * 0.3, 0, 1);
            if (Seconds < _nextAt) { Changed?.Invoke(); return; }
            _nextAt = Seconds + 0.18;

            var (speaker, text) = _roundOfNames ? RoundLine() : Script[_line % Script.Length];
            var words = text.Split(' ');
            if (_word < words.Length)
            {
                _word++;
                Partial = string.Join(' ', words.Take(_word));
                PartialSpeakerId = $"S{speaker + 1}";
                if (Meeting.SpeakerById(PartialSpeakerId) is null) Meeting.Speakers.Add(new Speaker { Id = PartialSpeakerId });
            }
            else
            {
                var length = 1.5 + text.Length / 15.0;
                Meeting.Segments.Add(new Segment(Math.Max(0, Now - length), Now, $"S{speaker + 1}", text));
                Partial = null;
                _word = 0;
                _nextAt = Seconds + 1.4;
                if (_roundOfNames && _roundStep < 3)
                {
                    Meeting.SpeakerById($"S{speaker + 1}")!.Name = Names[Math.Min(speaker, 2)];
                    _roundStep++;
                }
                else _line++;
            }
            Changed?.Invoke();
        }

        /// Everyone says their name; then the meeting goes on (the round stays open until Done).
        private (int, string) RoundLine() => _roundStep < 3
            ? (_roundStep, L($"Hej, jag heter {Names[_roundStep]}.", $"Hi, my name is {Names[_roundStep]}."))
            : Script[_line % Script.Length];

        public void Pause()
        {
            if (State != MeetingState.Recording) return;
            State = MeetingState.Paused;
            _pausedAt = Seconds;
            Partial = null;
            Level = 0;
            Changed?.Invoke();
        }

        public void Resume()
        {
            if (State != MeetingState.Paused) return;
            _pausedTotal += Seconds - _pausedAt;
            _pausedAt = -1;
            State = MeetingState.Recording;
            Changed?.Invoke();
        }

        public Task StopAsync()
        {
            Meeting.Duration = Now;
            Partial = null;
            Level = 0;
            State = MeetingState.Finishing;
            _finishStarted = Seconds;
            Meeting.State = State;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        private void Finish()
        {
            _timer.Stop();
            Meeting.State = MeetingState.Done;
            if (string.IsNullOrEmpty(Meeting.Title)) Meeting.Title = L("Veckomöte om lanseringen", "Weekly meeting about the launch");
            Meeting.Summary = Summary(Meeting);
            _app.Meetings.Save(Meeting);
            State = MeetingState.Done;
            _service.Ended(this);
            Changed?.Invoke();
        }

        public void Discard()
        {
            _timer.Stop();
            State = MeetingState.Failed;
            _service.Ended(this);
        }

        public void AddNote(string text) { Meeting.Notes.Add(new MeetingNote(Now, text)); Changed?.Invoke(); }
        public void AddMark(MarkKind kind) { Meeting.Marks.Add(new MeetingMark(Now, kind)); Changed?.Invoke(); }
        public void EndRoundOfNames() { _roundOfNames = false; Changed?.Invoke(); }

        public void NameSpeaker(string speakerId, string? name, bool remember)
        {
            if (Meeting.SpeakerById(speakerId) is { } speaker) speaker.Name = name;
            if (Suggestion?.SpeakerId == speakerId) Suggestion = null;
            Changed?.Invoke();
        }

        public void SetBackground(string speakerId, bool background)
        {
            if (Meeting.SpeakerById(speakerId) is { } speaker) { speaker.IsBackground = background; if (background) speaker.Name = null; }
            Changed?.Invoke();
        }

        public void AcceptSuggestion(bool accept)
        {
            if (Suggestion is { } s && accept && Meeting.SpeakerById(s.SpeakerId) is { } speaker) speaker.Name = s.Name;
            Suggestion = null;
            Changed?.Invoke();
        }
    }

    // ---- Ask -------------------------------------------------------------------------------

    private sealed class Asker(App app) : IMeetingAsk
    {
        public async Task<AskExchange> AskAsync(string question, IReadOnlyList<Meeting> scope, IProgress<string>? partial = null, CancellationToken ct = default)
        {
            var launch = scope.FirstOrDefault(m => m.Summary?.Decisions.Count > 0) ?? scope.FirstOrDefault();
            var answer = launch?.Summary is { } s && s.Decisions.Count > 0
                ? L($"I ”{launch.Title}” bestämdes att mötesfunktionen byggs härnäst. Höstbudgeten justeras också före styrelsemötet, och Erik skickar siffrorna till Sara på torsdag.",
                    $"In “{launch.Title}”, it was decided to build the meetings feature next. The autumn budget is also adjusted before the board meeting, and Erik sends the numbers to Sara on Thursday.")
                : L("Inget i de här mötena svarar på det.", "Nothing in these meetings answers that.");
            var text = "";
            foreach (var word in answer.Split(' '))
            {
                await Task.Delay(45, ct);
                text += (text.Length > 0 ? " " : "") + word;
                partial?.Report(text);
            }
            var citations = launch?.Summary is { } summary && summary.Decisions.Count > 0
                ? new List<AskCitation>
                {
                    new(launch.Id, summary.Decisions[0].Time ?? 0, launch.Segments.FirstOrDefault(x => x.Start >= (summary.Decisions[0].Time ?? 0))?.Text ?? ""),
                    new(launch.Id, summary.Remember.FirstOrDefault()?.Time ?? 0, launch.Segments.FirstOrDefault(x => x.Start >= (summary.Remember.FirstOrDefault()?.Time ?? 0))?.Text ?? ""),
                }
                : [];
            _ = app;
            return new AskExchange(question, answer, citations, DateTime.Now);
        }
    }
}
#endif
