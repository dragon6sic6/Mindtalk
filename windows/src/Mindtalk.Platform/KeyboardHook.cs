using System.Runtime.InteropServices;
using Mindtalk.Core.Input;

namespace Mindtalk.Platform;

/// <summary>
/// A system-wide low-level keyboard hook (WH_KEYBOARD_LL) feeding the <see cref="KeyRouter"/>.
/// Runs on its own thread with its own message loop, so a busy UI thread can never make
/// typing lag — or make Windows drop the hook for answering too slowly.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const uint WM_MASK_MENU = Native.WM_APP + 1;
    /// The scan code Windows gives the fake Left Ctrl it sends with AltGr.
    private const uint AltGrControlScanCode = 0x21D;

    private readonly Action<KeySignal> _deliver;
    private readonly Native.HookProc _proc;   // kept alive: the hook holds only a native pointer
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hook;
    private uint _threadId;
    private Exception? _startError;

    public KeyRouter Router { get; }

    /// <param name="deliver">Receives every signal but the menu mask, on the hook thread —
    /// it must only queue them (to the UI thread).</param>
    public KeyboardHook(Hotkey hotkey, Action<KeySignal> deliver)
    {
        _deliver = deliver;
        Router = new KeyRouter(hotkey, Post);
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Mindtalk keyboard hook" };
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw _startError;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == 0) _startError = new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastPInvokeError()}).");
        _ready.Set();
        if (_hook == 0) return;

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_MASK_MENU) SendMenuMask();
        }
        Native.UnhookWindowsHookEx(_hook);
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            var isDown = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            var isUp = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            // Our own Ctrl+V and menu mask aren't the user's keys. (Other software's keys
            // are: remappers and on-screen keyboards send them on the user's behalf.)
            var ours = (info.flags & Native.LLKHF_INJECTED) != 0 && info.dwExtraInfo == Native.InjectedByMindtalk;
            var altGrControl = info.vkCode == VirtualKey.LeftCtrl && info.scanCode == AltGrControlScanCode;
            if ((isDown || isUp) && !ours && !altGrControl)
            {
                bool swallow;
                try { swallow = Router.Handle(new KeyEvent((int)info.vkCode, isDown)); }
                catch { swallow = false; }   // never break the keyboard
                if (swallow) return 1;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Post(KeySignal signal)
    {
        // Sent after the hook has handed the Alt key-down back, so it lands between
        // Alt's down and up.
        if (signal is KeySignal.MaskMenu) Native.PostThreadMessageW(_threadId, WM_MASK_MENU, 0, 0);
        else _deliver(signal);
    }

    private static void SendMenuMask()
    {
        Native.INPUT[] inputs = [Native.Key(VirtualKey.MenuMask, up: false), Native.Key(VirtualKey.MenuMask, up: true)];
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public static bool IsPhysicallyDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    /// Catch a key-up the hook may have missed (secure desktop, sleep, a dropped hook).
    public void Resync() => Router.Resync(IsPhysicallyDown);

    /// What a key is called on the current keyboard layout ("§", "Å", "Num 5").
    public static string? KeyName(int key)
    {
        var scan = Native.MapVirtualKeyW((uint)key, 0 /* MAPVK_VK_TO_VSC */);
        if (scan == 0) return null;
        var buffer = new char[64];
        var length = Native.GetKeyNameTextW((int)(scan << 16), buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
    }
}
