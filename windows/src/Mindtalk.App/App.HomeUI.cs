using System.Runtime.InteropServices;
using Mindtalk.App.Pages;
using Mindtalk.Core.Input;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App;

/// <summary>
/// Home, Dictation and Thoughts: their pages, Say a thought (its key, Ctrl+Alt+T when that
/// key would clash with dictation, the buttons), Start meeting from Home and the tray panel,
/// and pasting a dictation from the history into the app you were in.
/// </summary>
public partial class App
{
    partial void RegisterHomeUI()
    {
#if DEBUG
        // The app icon from the same dot sphere as the orb: Mindtalk --render-icon <folder>
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--render-icon") is var i and > 0 && i + 1 < Environment.GetCommandLineArgs().Length)
        {
            IconArt.Save(Environment.GetCommandLineArgs()[i + 1]);
            Environment.Exit(0);
        }
#endif
        MainWindow.HomeFactory = (app, window) => new HomePage(app, window);
        MainWindow.DictationFactory = (app, window) => new DictationPage(app, window);
        MainWindow.ThoughtsFactory = (app, window) => new ThoughtsPage(app, window);
    }

    // ---- Meetings, from Home and the panel ---------------------------------------------

    /// Opens a meeting, at a moment if given (seconds from its start). The meetings part sets
    /// its own; until then it shows the Meetings page.
    public Action<Guid, double?> OpenMeeting { get; set; } = (_, _) => Instance.ShowMain(Page.Meetings);

    /// Start Meeting (Home, the panel): a meeting in the room — or, while one is being
    /// recorded, that one.
    public void StartMeeting()
    {
        // The hub opens the live page and turns on the keys that only work while recording.
        if (MeetingHub is { } hub)
        {
            ShowMain();
            hub.Start(Settings.RoundOfNamesFirst);
            return;
        }
        if (MeetingService is { } service)
        {
            if (service.Current is { State: MeetingState.Recording or MeetingState.Paused } live)
            {
                OpenMeeting(live.Meeting.Id, null);
                return;
            }
            try { service.Start(new MeetingStart(MeetingSource.Room, Settings.RoundOfNamesFirst)); }
            catch (InvalidOperationException) { /* the meetings page says what's missing */ }
        }
        ShowMain(Page.Meetings);
    }

    /// End Meeting, from the panel.
    public void EndMeeting() => _ = MeetingService?.Current?.StopAsync();

    // ---- Say a thought -----------------------------------------------------------------

    private Hotkey? _appliedThought, _appliedDictation;
    private bool _thoughtKeyApplied;
    private int _thoughtShortcut;
    private Action<Hotkey>? _thoughtCapture;

    /// Called once the keyboard hook runs: thoughts go to the Thoughts page, and the thought
    /// key follows the settings from then on.
    private void StartThoughts()
    {
        Dictation.AddThought = text => Thoughts.Add(text, DateTime.Now);
        Changed += ApplyThoughtKey;
        ApplyThoughtKey();
    }

    /// The thought key the settings ask for — or Ctrl+Alt+T while it would share a key with dictation.
    private void ApplyThoughtKey()
    {
        if (_thoughtKeyApplied && Equals(_appliedThought, Settings.ThoughtHotkey) && Equals(_appliedDictation, Settings.Hotkey)) return;
        _thoughtKeyApplied = true;
        _appliedThought = Settings.ThoughtHotkey;
        _appliedDictation = Settings.Hotkey;
        var (hold, fallback) = ThoughtKeys.Resolve(Settings.Hotkey, Settings.ThoughtHotkey);
        Hook.Router.ThoughtHotkey = hold;
        if (fallback && _thoughtShortcut == 0)
            _thoughtShortcut = Shortcuts.Register(Shortcut.Thought, () => _ui.TryEnqueue(SayThought));
        else if (!fallback && _thoughtShortcut != 0)
        {
            Shortcuts.Unregister(_thoughtShortcut);
            _thoughtShortcut = 0;
        }
    }

    /// Say a Thought: starts one hands-free (the button, Ctrl+Alt+T), or saves the one being said.
    public void SayThought() => Dictation.StartThought();

    /// Why the last key picked for thoughts wasn't taken (it shares a key with dictation…).
    public string? ThoughtKeyProblem { get; private set; }

    public bool PickingThoughtKey => PickingKey && _capture is not null && ReferenceEquals(_capture, _thoughtCapture);

    /// Change, on the Thoughts page: hold the modifiers you want together, then let go. Esc keeps the old ones.
    public void PickThoughtKey()
    {
        StopPickingKey();
        ThoughtKeyProblem = null;
        _thoughtCapture = key =>
        {
            PickingKey = false;
            if (ThoughtKeys.Problem(Settings.Hotkey, key) is { } problem) ThoughtKeyProblem = problem;
            else Settings.ThoughtHotkey = key;
            Settings.Save();
            Refresh();
        };
        _capture = _thoughtCapture;
        PickingKey = true;
        Hook.Router.BeginCapture(thought: true);
        Refresh();
    }

    /// Turn Off / Turn On (left Alt, or Ctrl+Alt+T if dictation uses left Alt).
    public void SetThoughtKey(Hotkey? key)
    {
        StopPickingKey();
        ThoughtKeyProblem = null;
        Settings.ThoughtHotkey = key;
        SaveSettings();
    }

    // ---- Pasting from the history -------------------------------------------------------

    /// Pastes <paramref name="text"/> into the app you were in before Mindtalk's window —
    /// or, if there's none, puts it in the clipboard.
    public async void PasteIntoPreviousApp(string text)
    {
        var target = PreviousApp();
        if (target == 0)
        {
            Copy(text);
            return;
        }
        SetForegroundWindow(target);
        for (var i = 0; i < 25 && GetForegroundWindow() != target; i++) await Task.Delay(10);
        await _output.InsertAsync(text + " ", target, Settings.KeepInClipboard ? text : null);
    }

    /// The window under Mindtalk's: the app you were in.
    private nint PreviousApp()
    {
        if (_main is null) return 0;
        var own = WinRT.Interop.WindowNative.GetWindowHandle(_main);
        GetWindowThreadProcessId(own, out var us);
        for (var w = GetWindow(own, GW_HWNDNEXT); w != 0; w = GetWindow(w, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(w) || GetWindow(w, GW_OWNER) != 0) continue;
            GetWindowThreadProcessId(w, out var pid);
            if (pid == us) continue;
            var style = (long)Interop.Win32.GetWindowLongPtr(w, Interop.Win32.GWL_EXSTYLE);
            if ((style & Interop.Win32.WS_EX_TOOLWINDOW) != 0) continue;
            // Windows that are there but not shown (suspended Store apps, other desktops).
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetWindowTextLengthW(w) == 0) continue;
            return w;
        }
        return 0;
    }

    private const uint GW_HWNDNEXT = 2, GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
