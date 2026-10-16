using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mindtalk.App.Interop;
using Mindtalk.Core;
using Windows.Foundation;

namespace Mindtalk.App;

/// <summary>What the tray icon shows (the Mac's menu bar item).</summary>
public enum TrayState { Idle, Recording, Transcribing, Failed, Copied, Downloading }

/// <summary>
/// The notification-area icon (Shell_NotifyIcon), the Windows counterpart of the Mac's menu
/// bar item: Mindtalk's mark at rest, a symbol for what it's doing otherwise — drawn white
/// or black to suit the taskbar. A click (left or right) opens the panel. Lives on the UI
/// thread, which pumps its hidden window's messages.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Win32.WM_USER + 100;
    private static readonly uint TaskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

    private readonly Win32.WndProc _wndProc;   // kept alive for the native window
    private readonly nint _hwnd;
    private nint _icon;
    private TrayState _state = TrayState.Idle;
    private string _tooltip = "Mindtalk";

    public event Action? Clicked;
    /// The PC woke up or the session was unlocked: keys may have changed while we weren't looking.
    public event Action? Resumed;
    /// The PC is going to sleep.
    public event Action? Suspending;
    /// Windows settings changed (perhaps light or dark).
    public event Action? SettingsChanged;

    public TrayIcon()
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
        _hwnd = Win32.CreateWindowExW(0, "MindtalkTray", "Mindtalk", Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        Win32.WTSRegisterSessionNotification(_hwnd, 0);
        _icon = Render(_state);
        Add();
    }

    public void Update(TrayState state)
    {
        if (state == _state) return;
        _state = state;
        Redraw();
    }

    public string Tooltip
    {
        set
        {
            _tooltip = value.Length > 127 ? value[..127] : value;
            var data = Data(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
            Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        }
    }

    /// Where the icon is on screen (physical pixels), to put the panel next to it.
    public Rect Bounds()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = 1 };
        if (Win32.Shell_NotifyIconGetRect(ref id, out var r) == 0) return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        Win32.GetCursorPos(out var p);
        return new Rect(p.X, p.Y, 1, 1);
    }

    private void Redraw()
    {
        var old = _icon;
        _icon = Render(_state);
        var data = Data(Win32.NIF_ICON);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        if (old != 0) Win32.DestroyIcon(old);
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
            // Version 4: the event is in the low word of lParam. Left or right: the panel —
            // one menu, nothing hidden.
            switch ((uint)(lParam & 0xFFFF))
            {
                case Win32.NIN_SELECT or Win32.NIN_KEYSELECT or Win32.WM_CONTEXTMENU:
                    Clicked?.Invoke();
                    break;
            }
            return 0;
        }
        // Explorer restarted: the icon has to be added again.
        if (msg == TaskbarCreated) { Add(); return 0; }
        // The taskbar switched between light and dark.
        if (msg == Win32.WM_SETTINGCHANGE) { Redraw(); SettingsChanged?.Invoke(); }
        if (msg == Win32.WM_POWERBROADCAST && wParam == 0x4 /* PBT_APMSUSPEND */) Suspending?.Invoke();
        if (msg == Win32.WM_POWERBROADCAST && wParam == 0x12 /* PBT_APMRESUMEAUTOMATIC */) Resumed?.Invoke();
        if (msg == Win32.WM_WTSSESSION_CHANGE && wParam == 0x8 /* WTS_SESSION_UNLOCK */) Resumed?.Invoke();
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---- Drawing -----------------------------------------------------------------

    /// A light taskbar wants a black glyph; a dark one (the default) a white one.
    private static bool LightTaskbar
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is 1;
        }
    }

#if DEBUG
    /// Screenshots without the taskbar: each state's icon, light and dark, as PNG files.
    public static void SavePreviews(string folder, int size)
    {
        Directory.CreateDirectory(folder);
        foreach (var state in Enum.GetValues<TrayState>())
            foreach (var light in new[] { false, true })
                using (var bitmap = Draw(state, size, light ? Color.Black : Color.White))
                    bitmap.Save(Path.Combine(folder, $"tray-{state}-{(light ? "light" : "dark")}-{size}.png"));
    }
#endif

    private static nint Render(TrayState state)
    {
        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForSystem());
        using var bitmap = Draw(state, size, LightTaskbar ? Color.Black : Color.White);
        return bitmap.GetHicon();
    }

    private static Bitmap Draw(TrayState state, int size, Color ink)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var s = size / 16f;
            switch (state)
            {
                case TrayState.Idle: DrawMark(g, size, ink); break;
                case TrayState.Recording: DrawWaveCircle(g, s, ink); break;
                default: DrawSymbol(g, size, ink, state); break;
            }
        }
        return bitmap;
    }

    /// The mark fitted to the tray, with strokes and cursor drawn heavier so it reads as
    /// well as its neighbours (the Mac's menu bar template image, on a square).
    private static void DrawMark(Graphics g, int size, Color ink)
    {
        var s = size / 16f;
        var waveEnd = 700.0; var minX = 215.0;
        var scale = 11.6 * s / (waveEnd - minX);
        var mid = size / 2f;
        var points = Controls.Mark.Wave().Select(p => new PointF(
            (float)(0.6 * s + (p.X - minX) * scale),
            (float)(mid - (p.Y - 512) * scale * 1.25))).ToArray();
        using var pen = new Pen(ink, 1.5f * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, points);
        var cx = (float)(0.6 * s + (760 - minX) * scale + 0.5 * s);
        float h = 11.5f * s, stem = 1.5f * s, serif = 4.4f * s;
        using var brush = new SolidBrush(ink);
        foreach (var r in new[]
                 {
                     new RectangleF(cx - stem / 2, mid - h / 2, stem, h),
                     new RectangleF(cx - serif / 2, mid + h / 2 - stem, serif, stem),
                     new RectangleF(cx - serif / 2, mid - h / 2, serif, stem),
                 })
            using (var path = Capsule(r)) g.FillPath(brush, path);
    }

    /// waveform.circle.fill: a filled circle with the bars cut out of it.
    private static void DrawWaveCircle(Graphics g, float s, Color ink)
    {
        using var brush = new SolidBrush(ink);
        g.FillEllipse(brush, 1 * s, 1 * s, 14 * s, 14 * s);
        float[] heights = [3, 6, 8, 5, 3];
        g.CompositingMode = CompositingMode.SourceCopy;
        using var hole = new SolidBrush(Color.Transparent);
        for (var i = 0; i < heights.Length; i++)
        {
            var x = (4.3f + i * 1.9f) * s;
            using var path = Capsule(new RectangleF(x, 8 * s - heights[i] * s / 2, 1.2f * s, heights[i] * s));
            g.FillPath(hole, path);
        }
        g.CompositingMode = CompositingMode.SourceOver;
    }

    /// The other states as their Segoe Fluent symbols, inside a circle as on the Mac.
    private static void DrawSymbol(Graphics g, int size, Color ink, TrayState state)
    {
        var s = size / 16f;
        using var pen = new Pen(ink, 1.2f * s);
        using var brush = new SolidBrush(ink);
        switch (state)
        {
            case TrayState.Transcribing:   // ellipsis.circle
                g.DrawEllipse(pen, 1.3f * s, 1.3f * s, 13.4f * s, 13.4f * s);
                for (var i = -1; i <= 1; i++) g.FillEllipse(brush, (8 + i * 3.2f - 0.95f) * s, 7.05f * s, 1.9f * s, 1.9f * s);
                break;
            case TrayState.Failed:         // exclamationmark.circle
                g.DrawEllipse(pen, 1.3f * s, 1.3f * s, 13.4f * s, 13.4f * s);
                using (var bar = Capsule(new RectangleF(7.3f * s, 4.2f * s, 1.4f * s, 5.2f * s))) g.FillPath(brush, bar);
                g.FillEllipse(brush, 7.25f * s, 10.4f * s, 1.5f * s, 1.5f * s);
                break;
            case TrayState.Downloading:    // arrow.down.circle
                g.DrawEllipse(pen, 1.3f * s, 1.3f * s, 13.4f * s, 13.4f * s);
                using (var arrow = new Pen(ink, 1.3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLine(arrow, 8 * s, 4.6f * s, 8 * s, 11 * s);
                    g.DrawLines(arrow, [new PointF(5.4f * s, 8.6f * s), new PointF(8 * s, 11.2f * s), new PointF(10.6f * s, 8.6f * s)]);
                }
                break;
            default:                       // doc.on.clipboard
                using (var font = new Font("Segoe Fluent Icons", 13 * s, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("", font, brush, new RectangleF(0, 0.5f * s, size, size), format);
                break;
        }
    }

    private static GraphicsPath Capsule(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = Math.Min(r.Width, r.Height);
        if (d <= 0) return path;
        if (r.Width >= r.Height)
        {
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        }
        else
        {
            path.AddArc(r.Left, r.Top, d, d, 180, 180);
            path.AddArc(r.Left, r.Bottom - d, d, d, 0, 180);
        }
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        var data = Data(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        if (_icon != 0) Win32.DestroyIcon(_icon);
        Win32.DestroyWindow(_hwnd);
    }
}
