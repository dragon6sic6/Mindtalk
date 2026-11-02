using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Mindtalk.Core.Input;

namespace Mindtalk.Platform;

/// <summary>
/// App-wide shortcuts (Ctrl+Alt+M for a meeting…) through Windows' RegisterHotKey, on a
/// message-only window with its own thread. A shortcut another app already owns can't be
/// registered: <see cref="Register"/> says so and the caller can tell you.
/// </summary>
public sealed class GlobalShortcuts : IDisposable
{
    private const int WM_HOTKEY = 0x0312, WM_APP = 0x8000, WM_QUIT = 0x0012;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentDictionary<int, Action> _actions = new();
    private readonly BlockingCollection<Action> _work = new();
    private nint _window;
    private uint _threadId;
    private int _nextId = 1;

    public GlobalShortcuts()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Mindtalk shortcuts" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// Registers a shortcut; <paramref name="pressed"/> runs on the shortcuts thread (post to the UI yourself).
    /// Returns an id for <see cref="Unregister"/>, or 0 if Windows refused (another app has it).
    public int Register(Shortcut shortcut, Action pressed)
    {
        var id = Interlocked.Increment(ref _nextId);
        var ok = false;
        Invoke(() => ok = RegisterHotKey(_window, id, shortcut.NativeModifiers, (uint)shortcut.Key));
        if (!ok) return 0;
        _actions[id] = pressed;
        return id;
    }

    public void Unregister(int id)
    {
        if (id == 0) return;
        _actions.TryRemove(id, out _);
        Invoke(() => UnregisterHotKey(_window, id));
    }

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) { action(); return; }
        using var done = new ManualResetEventSlim();
        _work.Add(() => { try { action(); } finally { done.Set(); } });
        PostThreadMessage(_threadId, WM_APP, 0, 0);
        done.Wait(2000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // HWND_MESSAGE (-3): a window only for messages, never shown.
        _window = CreateWindowEx(0, "STATIC", "Mindtalk shortcuts", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        _ready.Set();
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && _actions.TryGetValue((int)msg.wParam, out var pressed))
            {
                try { pressed(); } catch (Exception) { /* a shortcut's action shouldn't take the thread down */ }
            }
            else if (msg.message == WM_APP)
            {
                while (_work.TryTake(out var work)) work();
            }
            else
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Unregister(id);
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
