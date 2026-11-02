using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Meetings.Engine;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Mindtalk.Platform.Meetings;

/// <summary>
/// Notices when a call app starts using the microphone: every couple of seconds, the sound
/// sessions on the capture devices are listed with the processes behind them. Teams, Zoom,
/// Slack, Webex, Discord, WhatsApp and Skype count as calls; a browser only while one of its
/// windows is in Google Meet, Teams or Zoom. Nothing is listened to — only which app has the
/// microphone open. Events come on a background thread.
/// </summary>
public sealed partial class CallWatcher : ICallWatcher
{
    /// Executable (lower case, no .exe) → the app's name.
    public static readonly IReadOnlyDictionary<string, string> CallApps = new Dictionary<string, string>
    {
        ["ms-teams"] = "Teams", ["teams"] = "Teams", ["msteams"] = "Teams",
        ["zoom"] = "Zoom",
        ["slack"] = "Slack",
        ["ciscocollabhost"] = "Webex", ["webexmta"] = "Webex", ["atmgr"] = "Webex", ["webex"] = "Webex", ["ciscowebexstart"] = "Webex",
        ["discord"] = "Discord",
        ["whatsapp"] = "WhatsApp", ["whatsapp.root"] = "WhatsApp",
        ["skype"] = "Skype", ["skypeapp"] = "Skype",
    };

    public static readonly IReadOnlySet<string> Browsers = new HashSet<string> { "chrome", "msedge", "firefox", "brave", "arc", "opera", "vivaldi" };

    /// A browser window in a call: Google Meet, Teams or Zoom in its title.
    [GeneratedRegex(@"\b(Google Meet|Meet|Microsoft Teams|Teams|Zoom)\b")]
    private static partial Regex CallTitle();

    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DetectedCall Call, int Missed)> _active = [];
    private int _busy;

    public CallWatcher(TimeSpan? every = null)
    {
        var period = every ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), period);
    }

    public event Action<DetectedCall>? Started;
    public event Action<DetectedCall>? Ended;

    public IReadOnlyList<DetectedCall> Active { get { lock (_gate) return _active.Values.Select(v => v.Call).ToList(); } }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = Detect();
            var started = new List<DetectedCall>();
            var ended = new List<DetectedCall>();
            lock (_gate)
            {
                foreach (var call in now)
                {
                    var key = Key(call);
                    if (_active.TryGetValue(key, out var known)) _active[key] = (known.Call, 0);
                    else { _active[key] = (call, 0); started.Add(call); }
                }
                // Gone for two looks in a row: the call has ended (a moment's gap doesn't count).
                foreach (var key in _active.Keys.ToList())
                {
                    if (now.Any(c => Key(c) == key)) continue;
                    var (call, missed) = _active[key];
                    if (missed + 1 >= 2) { _active.Remove(key); ended.Add(call); }
                    else _active[key] = (call, missed + 1);
                }
            }
            foreach (var call in started) Started?.Invoke(call);
            foreach (var call in ended) Ended?.Invoke(call);
        }
        catch (Exception ex) { Trace.WriteLine($"CallWatcher: {ex.GetType().Name}: {ex.Message}"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static string Key(DetectedCall call) => $"{call.App}:{call.ProcessId}";

    /// <summary>The call apps using a microphone right now, each with the process to take the sound from.</summary>
    public static List<DetectedCall> Detect()
    {
        var processes = ProcessTable.Snapshot();
        var sessionPids = ActiveSessionPids();
        return Classify(sessionPids, processes);
    }

    /// Every process with an active capture session, by name — for the self-test.
    public static List<(int Pid, string Name)> CaptureSessions()
    {
        var processes = ProcessTable.Snapshot();
        return ActiveSessionPids().Select(pid => (pid, processes.Ancestors(pid).FirstOrDefault().Name ?? "?")).ToList();
    }

    private static HashSet<int> ActiveSessionPids()
    {
        var sessionPids = new HashSet<int>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (var i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            if (session.State == AudioSessionState.AudioSessionStateActive && !session.IsSystemSoundsSession)
                                sessionPids.Add((int)session.GetProcessID);
                        }
                    }
                    catch (Exception ex) { Trace.WriteLine($"CallWatcher: sessions on {device.FriendlyName}: {ex.GetType().Name}"); }
                }
            }
        }
        return sessionPids;
    }

    private static List<DetectedCall> Classify(HashSet<int> sessionPids, ProcessTable processes)
    {
        var calls = new List<DetectedCall>();
        Dictionary<int, List<string>>? titles = null;
        foreach (var pid in sessionPids)
        {
            // The app may use the mic from a helper process (Teams' web view): look up the family.
            var chain = processes.Ancestors(pid).ToList();
            var app = chain.FirstOrDefault(p => CallApps.ContainsKey(p.Name));
            if (app.Pid != 0)
            {
                var root = chain.Last(p => CallApps.TryGetValue(p.Name, out var n) && n == CallApps[app.Name]);
                calls.Add(new DetectedCall(CallApps[app.Name], root.Pid, DateTime.Now));
                continue;
            }
            var browser = chain.FirstOrDefault(p => Browsers.Contains(p.Name));
            if (browser.Pid == 0) continue;
            // The browser's top process owns its windows (and plays the sound for all its tabs).
            var top = chain.Last(p => p.Name == browser.Name);
            titles ??= WindowTitles();
            var family = processes.All.Where(p => p.Name == browser.Name).Select(p => p.Pid);
            var match = family.SelectMany(p => titles.GetValueOrDefault(p) ?? []).Select(t => CallTitle().Match(t)).FirstOrDefault(m => m.Success);
            if (match is null) continue;
            var name = match.Value switch { "Meet" or "Google Meet" => "Google Meet", "Microsoft Teams" => "Teams", var v => v };
            calls.Add(new DetectedCall(name, top.Pid, DateTime.Now));
        }
        return calls.DistinctBy(Key).ToList();
    }

    /// Visible top-level windows' titles, by process.
    private static Dictionary<int, List<string>> WindowTitles()
    {
        var result = new Dictionary<int, List<string>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!result.TryGetValue((int)pid, out var list)) result[(int)pid] = list = [];
            list.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public void Dispose() => _timer.Dispose();

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// <summary>Every process with its parent and executable name (lower case, no .exe), from one snapshot.</summary>
internal sealed class ProcessTable
{
    public readonly record struct Entry(int Pid, int Parent, string Name);

    private readonly Dictionary<int, Entry> _byPid;
    private ProcessTable(Dictionary<int, Entry> byPid) => _byPid = byPid;

    public IEnumerable<Entry> All => _byPid.Values;

    /// The process, its parent, and so on up (stops at a loop or a parent that's gone).
    public IEnumerable<Entry> Ancestors(int pid)
    {
        var seen = new HashSet<int>();
        while (_byPid.TryGetValue(pid, out var e) && seen.Add(pid))
        {
            yield return e;
            pid = e.Parent;
        }
    }

    public static ProcessTable Snapshot()
    {
        var map = new Dictionary<int, Entry>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return new ProcessTable(map);
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return new ProcessTable(map);
            do
            {
                var name = entry.ExeFile;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                map[(int)entry.ProcessId] = new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, name.ToLowerInvariant());
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return new ProcessTable(map);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
