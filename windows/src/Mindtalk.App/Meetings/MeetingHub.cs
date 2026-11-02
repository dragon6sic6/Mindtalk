using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mindtalk.Core;
using Mindtalk.Core.Input;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// What the meeting windows share: the meeting being recorded (its changes brought to the UI
/// thread, a few times a second at most), starting and ending, the shortcuts that only work
/// while recording, the floating panel and the call box, and going to a meeting.
/// </summary>
internal sealed class MeetingHub
{
    private readonly App _app;
    private readonly DispatcherQueueTimer _timer;
    private ILiveMeeting? _live;
    private IMeetingService? _service;
    private MeetingState? _lastState;
    private bool _pending;
    private readonly List<int> _recordingKeys = [];
    private FloatingPanel? _panel;
    private CallBox? _callBox;
    private Shortcut? _meetingKey;
    private int _meetingKeyId;

    public MeetingHub(App app)
    {
        _app = app;
        _timer = app.UI.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.Tick += (_, _) => Tick();
    }

    public IMeetingService? Service => _app.MeetingService;

    /// The meeting being recorded — kept while it finishes and is summarized.
    public ILiveMeeting? Live => _live;

    /// The meeting's changes, on the UI thread (structure and text; the level is read on <see cref="Frame"/>).
    public event Action? LiveChanged;
    /// About eight times a second while a meeting is live: the clock and the level.
    public event Action? Frame;

    /// A file being imported, before it's a meeting of its own.
    public string? Importing { get; private set; }
    public string? ImportError { get; private set; }

    public bool IsLive => _live is { State: MeetingState.Recording or MeetingState.Paused or MeetingState.Finishing or MeetingState.Summarizing };

    // ---- The service ------------------------------------------------------------------

    /// Hooks up to the meeting service (it may come later than the windows, or not at all).
    public void Attach()
    {
        if (ReferenceEquals(_service, Service)) return;
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
            _service.CallStarted -= OnCallStarted;
            _service.CallEnded -= OnCallEnded;
        }
        _service = Service;
        if (_service is null) return;
        _service.Changed += OnServiceChanged;
        _service.CallStarted += OnCallStarted;
        _service.CallEnded += OnCallEnded;
        OnServiceChanged();
    }

    private void OnServiceChanged() => _app.UI.TryEnqueue(() =>
    {
        if (_service?.Current is { } current && !ReferenceEquals(current, _live)) Follow(current);
        _app.Refresh();
    });

    private void OnCallStarted(DetectedCall call) => _app.UI.TryEnqueue(() =>
    {
        if (!_app.Settings.AskWhenCallStarts || IsLive) return;
        _callBox ??= new CallBox(_app, this);
        _callBox.Show(call);
    });

    private void OnCallEnded(DetectedCall call) => _app.UI.TryEnqueue(() => _callBox?.Hide(call));

    // ---- Starting and ending ---------------------------------------------------------

    /// Starts a meeting and shows it (in the window if Mindtalk is in front, else the floating panel).
    public void Start(bool roundOfNames, DetectedCall? call = null, bool showWindow = true)
    {
        _callBox?.Hide(null);
        if (IsLive) { OpenLive(); return; }
        if (Service is not { } service)
        {
            _ = _app.Ask(Loc.T("Möten är inte klara än", "Meetings aren't ready yet"),
                Loc.T("Inspelning av möten finns inte i den här versionen av Mindtalk för Windows än.",
                      "Recording meetings isn't available in this version of Mindtalk for Windows yet."), "OK", null);
            return;
        }
        var start = call is null
            ? new MeetingStart(MeetingSource.Room, roundOfNames)
            : new MeetingStart(MeetingSource.Call, roundOfNames, call.App, call.ProcessId);
        ILiveMeeting live;
        try { live = service.Start(start); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            _ = _app.Ask(Loc.T("Mötet kunde inte starta", "The meeting couldn't start"), ex.Message, "OK", null);
            return;
        }
        Follow(live);
        if (showWindow) OpenLive();
    }

    /// Ctrl+Alt+M: starts a meeting, or ends the one being recorded.
    public void Toggle()
    {
        if (_live is { State: MeetingState.Recording or MeetingState.Paused }) _ = End();
        else if (!IsLive) Start(_app.Settings.RoundOfNamesFirst, showWindow: IsMindtalkInFront());
        else OpenLive();
    }

    public async Task End()
    {
        if (_live is not { State: MeetingState.Recording or MeetingState.Paused } live) return;
        try { await live.StopAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { }
        Changed();
    }

    public async void Discard()
    {
        if (_live is not { } live) return;
        var sure = await _app.Ask(Loc.T("Kasta mötet?", "Discard the meeting?"),
            Loc.T("Ljudet och texten tas bort. Det går inte att ångra.", "The audio and the text are deleted. This can't be undone."),
            Loc.T("Kasta", "Discard"), Loc.T("Avbryt", "Cancel"));
        if (!sure || !ReferenceEquals(live, _live)) return;
        live.Discard();
        Unfollow();
        if (_app.MainWindowIfOpen is { Pushed: LivePage } main) main.Pop();
    }

    public void TogglePause()
    {
        if (_live?.State == MeetingState.Recording) _live.Pause();
        else if (_live?.State == MeetingState.Paused) _live.Resume();
        Changed();
    }

    public void Mark(MarkKind kind)
    {
        if (_live?.State is not (MeetingState.Recording or MeetingState.Paused)) return;
        _live.AddMark(kind);
        Marked?.Invoke(kind);
        Changed();
    }

    /// A mark was just set (the buttons flash).
    public event Action<MarkKind>? Marked;

    // ---- Following the live meeting --------------------------------------------------

    private void Follow(ILiveMeeting live)
    {
        if (_live is not null) _live.Changed -= OnLiveChanged;
        _live = live;
        _lastState = null;
        live.Changed += OnLiveChanged;
        _timer.Start();
        Changed();
    }

    private void Unfollow()
    {
        if (_live is not null) _live.Changed -= OnLiveChanged;
        _live = null;
        _timer.Stop();
        SetRecordingKeys(false);
        _panel?.Hide();
        LiveChanged?.Invoke();
        _app.Refresh();
    }

    /// Any thread: brought to the UI thread once, however many changes come in meanwhile.
    private void OnLiveChanged()
    {
        if (_pending) return;
        _pending = true;
        _app.UI.TryEnqueue(Changed);
    }

    private void Changed()
    {
        _pending = false;
        if (_live is not { } live) return;
        var state = live.State;
        if (state != _lastState)
        {
            var previous = _lastState;
            _lastState = state;
            SetRecordingKeys(state is MeetingState.Recording or MeetingState.Paused);
            _app.Refresh();
            if (state is MeetingState.Done or MeetingState.Failed)
            {
                // Discarded (Failed straight from recording) or already done when first seen: nothing to show.
                if (previous is MeetingState.Finishing or MeetingState.Summarizing) Finished(live);
                else Unfollow();
                return;
            }
        }
        LiveChanged?.Invoke();
        UpdatePanel();
    }

    /// Done: the meeting page takes over from the live one (and the panel offers it once more).
    private void Finished(ILiveMeeting live)
    {
        var id = live.Meeting.Id;
        Unfollow();
        if (_app.MainWindowIfOpen is { IsOpen: true, Pushed: LivePage } main) main.Push(new MeetingPage(_app, main, this, id));
        else if (_app.Settings.ShowMeetingPanel)
        {
            _panel ??= new FloatingPanel(_app, this);
            _panel.ShowDone(live.Meeting);
        }
    }

    private void Tick()
    {
        if (_live is null) { _timer.Stop(); return; }
        Frame?.Invoke();
        UpdatePanel();
    }

    // ---- Keys while recording ----------------------------------------------------------

    private void SetRecordingKeys(bool on)
    {
        if (on == _recordingKeys.Count > 0) return;
        if (!on)
        {
            foreach (var id in _recordingKeys) _app.Shortcuts.Unregister(id);
            _recordingKeys.Clear();
            return;
        }
        void Add(Shortcut shortcut, Action action)
        {
            var id = _app.Shortcuts.Register(shortcut, () => _app.UI.TryEnqueue(() => action()));
            if (id != 0) _recordingKeys.Add(id);
        }
        Add(Shortcut.Pause, TogglePause);
        Add(Shortcut.MarkDecision, () => Mark(MarkKind.Decision));
        Add(Shortcut.MarkTodo, () => Mark(MarkKind.Todo));
        Add(Shortcut.MarkImportant, () => Mark(MarkKind.Important));
        // Nothing registered (all taken by other apps): still counts as on, so it isn't retried every change.
        if (_recordingKeys.Count == 0) _recordingKeys.Add(0);
    }

    /// Ctrl+Alt+M (or the one chosen in Settings), registered again when it changes.
    public void RegisterMeetingKey()
    {
        var wanted = _app.Settings.MeetingShortcut;
        if (Equals(wanted, _meetingKey) && (_meetingKeyId != 0 || wanted is null)) return;
        if (_meetingKeyId != 0) _app.Shortcuts.Unregister(_meetingKeyId);
        _meetingKey = wanted;
        _meetingKeyId = wanted is null ? 0 : _app.Shortcuts.Register(wanted, () => _app.UI.TryEnqueue(Toggle));
    }

    // ---- The floating panel ------------------------------------------------------------

    private void UpdatePanel()
    {
        var show = IsLive && _app.Settings.ShowMeetingPanel && !IsMindtalkInFront();
#if DEBUG
        if (Demo.Has("--demo-meeting-panel") && IsLive) show = true;
#endif
        if (show)
        {
            _panel ??= new FloatingPanel(_app, this);
            _panel.ShowLive();
        }
        else if (_panel is { ShowsLive: true }) _panel.Hide();
    }

    /// Mindtalk's main window is the one in front (the panel isn't needed then).
    public bool IsMindtalkInFront()
    {
        var front = GetForegroundWindow();
        if (front == 0) return false;
        if (_panel?.Handle == front || _callBox?.Handle == front) return false;
        GetWindowThreadProcessId(front, out var pid);
        return pid == Environment.ProcessId && _app.MainWindowIfOpen is { IsOpen: true };
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);

    // ---- Going places --------------------------------------------------------------------

    /// The main window with the live meeting.
    public void OpenLive() => Show(main =>
    {
        if (main.Pushed is LivePage) return;
        main.Push(new LivePage(_app, main, this));
    });

    /// A meeting, optionally at a moment (the Text tab, scrolled there).
    public void OpenMeeting(Guid id, double? at = null, Action<MeetingPage>? then = null)
    {
        if (_live?.Meeting.Id == id && IsLive) { OpenLive(); return; }
        Show(main =>
        {
            var page = new MeetingPage(_app, main, this, id);
            main.Push(page);
            if (at is { } time) page.ShowMoment(time);
            then?.Invoke(page);
        });
    }

    /// The Meetings page in the main window, then a page over it. A window opened just now
    /// gets a moment to lay out its sidebar first (its selection is placed after layout).
    private void Show(Action<MainWindow> push)
    {
        var wasOpen = _app.MainWindowIfOpen?.IsOpen == true;
        var main = _app.OpenMainWindow();
        if (main.CurrentPage != Page.Meetings) main.ShowPage(Page.Meetings);
        if (wasOpen && main.Content is FrameworkElement { IsLoaded: true }) { push(main); return; }
        var timer = _app.UI.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => push(main);
        timer.Start();
    }

    // ---- Importing -------------------------------------------------------------------------

    public static readonly string[] ImportTypes =
        [".mp3", ".m4a", ".wav", ".mp4", ".mov", ".webm", ".wma", ".aac", ".flac", ".ogg", ".opus", ".mkv", ".avi", ".3gp", ".caf", ".aiff", ".aif", ".amr"];

    public bool CanImport(string path) => Service?.CanImport(path) ?? ImportTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    public async Task Import(IReadOnlyList<string> paths)
    {
        if (Service is not { } service)
        {
            await _app.Ask(Loc.T("Import är inte klart än", "Importing isn't ready yet"),
                Loc.T("Att göra möten av inspelningar finns inte i den här versionen av Mindtalk för Windows än.",
                      "Turning recordings into meetings isn't available in this version of Mindtalk for Windows yet."), "OK", null);
            return;
        }
        Meeting? last = null;
        foreach (var path in paths.Where(CanImport))
        {
            Importing = Path.GetFileName(path);
            ImportError = null;
            _app.Refresh();
            try { last = await service.ImportAsync(path); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or COMException)
            {
                ImportError = Loc.T($"{Path.GetFileName(path)} kunde inte läsas: {ex.Message}", $"{Path.GetFileName(path)} couldn't be read: {ex.Message}");
            }
        }
        Importing = null;
        _app.Refresh();
        if (last is not null && paths.Count == 1) OpenMeeting(last.Id);
    }

    /// Who writes the summaries now: "Gemma 4 E4B".
    public string WriterName => _app.Writers?.CurrentName ?? _app.Summarizer?.WriterName ?? WriterTitle(_app.Settings.Writer);

    public static string WriterTitle(Core.AI.WriterKind kind) => kind switch
    {
        Core.AI.WriterKind.GemmaSmall => "Gemma 4 E4B",
        Core.AI.WriterKind.GemmaLarge => "Gemma 4 26B",
        Core.AI.WriterKind.Ollama => "Ollama",
        Core.AI.WriterKind.LMStudio => "LM Studio",
        _ => "Windows AI",
    };
}
