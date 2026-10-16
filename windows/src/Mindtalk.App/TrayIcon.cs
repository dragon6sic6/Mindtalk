using System.Runtime.InteropServices;
using Mindtalk.App.Interop;

namespace Mindtalk.App;

/// <summary>One line in the tray menu.</summary>
public sealed record TrayMenuItem(string Text, Action? Action = null, bool Checked = false, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new("-");
}

/// <summary>
/// The notification-area icon (Shell_NotifyIcon), the Windows counterpart of the Mac
/// menu bar item. Lives on the UI thread, which pumps its hidden window's messages.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Win32.WM_USER + 100;
    private static readonly uint TaskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

    private readonly Win32.WndProc _wndProc;   // kept alive for the native window
    private readonly nint _hwnd;
    private readonly nint _icon;
    private string _tooltip = "Mindtalk";

    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }
    public event Action? Clicked;
    /// The PC woke up or the session was unlocked: keys may have changed while we weren't looking.
    public event Action? Resumed;

    public TrayIcon(string iconPath)
    {
        _wndProc = WindowProc;
        var instance = Win32.GetModuleHandleW(null);
        var windowClass = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = "MindtalkTray",
        };
        Win32.RegisterClassExW(ref windowClass);
        // A hidden top-level window (not message-only): the menu needs one that can come to the front.
        _hwnd = Win32.CreateWindowExW(0, "MindtalkTray", "Mindtalk", Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        Win32.WTSRegisterSessionNotification(_hwnd, 0);

        var size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        _icon = Win32.LoadImageW(0, iconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        Add();
    }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value.Length > 127 ? value[..127] : value;
            var data = Data(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
            Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        }
    }

    private Win32.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref data);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            // Version 4: the event is in the low word of lParam.
            switch ((uint)(lParam & 0xFFFF))
            {
                case Win32.NIN_SELECT or Win32.NIN_KEYSELECT or Win32.WM_LBUTTONUP:
                    Clicked?.Invoke();
                    break;
                case Win32.WM_CONTEXTMENU or Win32.WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return 0;
        }
        // Explorer restarted: the icon has to be added again.
        if (msg == TaskbarCreated) { Add(); return 0; }
        if (msg == Win32.WM_POWERBROADCAST && wParam == 0x12 /* PBT_APMRESUMEAUTOMATIC */) Resumed?.Invoke();
        if (msg == Win32.WM_WTSSESSION_CHANGE && wParam == 0x8 /* WTS_SESSION_UNLOCK */) Resumed?.Invoke();
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = Menu?.Invoke() ?? [];
        var menu = Win32.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (ReferenceEquals(item, TrayMenuItem.Separator)) { Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null); continue; }
                var flags = Win32.MF_STRING;
                if (item.Checked) flags |= Win32.MF_CHECKED | Win32.MFT_RADIOCHECK;
                if (!item.Enabled || item.Action is null) flags |= Win32.MF_GRAYED;
                Win32.AppendMenuW(menu, flags, (nuint)(i + 1), item.Text);
            }
            Win32.GetCursorPos(out var point);
            // Without this the menu doesn't close when you click elsewhere.
            Win32.SetForegroundWindow(_hwnd);
            var chosen = Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_BOTTOMALIGN,
                                                point.X, point.Y, _hwnd, 0);
            Win32.PostMessageW(_hwnd, 0, 0, 0);
            if (chosen > 0 && chosen <= items.Count) items[(int)chosen - 1].Action?.Invoke();
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        var data = Data(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        if (_icon != 0) Win32.DestroyIcon(_icon);
        Win32.DestroyWindow(_hwnd);
    }
}
