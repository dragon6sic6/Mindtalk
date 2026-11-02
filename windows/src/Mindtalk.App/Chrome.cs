using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mindtalk.App.Controls;
using Windows.UI;

namespace Mindtalk.App;

/// <summary>
/// The look shared by Mindtalk's windows: content up into the title bar (the Mac hides its
/// title bar too), caption buttons that suit the paper, the app's appearance.
/// </summary>
internal static class Chrome
{
    public static void Prepare(Window window, UIElement titleBar)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        window.AppWindow.SetIcon(App.IconPath);
        window.AppWindow.Title = "Mindtalk";
        Paint(window);
    }

    public static void Paint(Window window)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = UI.Theme;
        var bar = window.AppWindow.TitleBar;
        var ink = UI.Dark ? Color.FromArgb(255, 242, 242, 242) : Color.FromArgb(255, 17, 17, 17);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ink;
        bar.ButtonInactiveForegroundColor = UI.Dark ? Color.FromArgb(255, 112, 112, 112) : Color.FromArgb(255, 140, 140, 140);
        bar.ButtonHoverBackgroundColor = UI.Dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
        bar.ButtonHoverForegroundColor = ink;
        bar.ButtonPressedBackgroundColor = UI.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
        bar.ButtonPressedForegroundColor = ink;
    }

    private const long Frame = Interop.Win32.WS_CAPTION | Interop.Win32.WS_THICKFRAME | Interop.Win32.WS_SYSMENU
                               | Interop.Win32.WS_MINIMIZEBOX | Interop.Win32.WS_MAXIMIZEBOX;

    /// The window procedures that keep the frame off — kept alive for as long as the windows.
    private static readonly Dictionary<nint, Interop.Win32.WndProc> Guarded = [];

    /// <summary>
    /// A window that is only what it draws: no frame, no border, no backdrop — where nothing
    /// is drawn, what's behind shows through (the HUD, the tray panel). The presenter leaves a
    /// dialog frame in place even "without border", which Windows draws as a thin light
    /// outline on dark backgrounds; it goes, along with the system's border and backdrop.
    /// </summary>
    public static void SeeThrough(nint hwnd)
    {
        // The presenter puts the frame back whenever it likes (on showing, for one): refuse it.
        if (!Guarded.ContainsKey(hwnd))
        {
            nint previous = 0;
            Interop.Win32.WndProc proc = (h, msg, w, l) =>
            {
                // No non-client area at all: the window is all client, so there is no frame to draw.
                if (msg == Interop.Win32.WM_NCCALCSIZE && w != 0) return 0;
                if (msg == Interop.Win32.WM_STYLECHANGING && w == Interop.Win32.GWL_STYLE)
                {
                    var change = System.Runtime.InteropServices.Marshal.PtrToStructure<Interop.Win32.STYLESTRUCT>(l);
                    change.styleNew &= ~(uint)Frame;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(change, l, false);
                }
                return Interop.Win32.CallWindowProcW(previous, h, msg, w, l);
            };
            previous = Interop.Win32.SetWindowLongPtr(hwnd, Interop.Win32.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(proc));
            Guarded[hwnd] = proc;
        }
        var style = (long)Interop.Win32.GetWindowLongPtr(hwnd, Interop.Win32.GWL_STYLE);
        Interop.Win32.SetWindowLongPtr(hwnd, Interop.Win32.GWL_STYLE, (nint)((style & ~Frame) | Interop.Win32.WS_POPUP_STYLE));
        Interop.Win32.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Interop.Win32.SWP_NOMOVE | Interop.Win32.SWP_NOSIZE | Interop.Win32.SWP_NOZORDER | Interop.Win32.SWP_NOACTIVATE | Interop.Win32.SWP_FRAMECHANGED);
        var square = Interop.Win32.DWMWCP_DONOTROUND;
        Interop.Win32.DwmSetWindowAttribute(hwnd, Interop.Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        var noBorder = Interop.Win32.DWMWA_COLOR_NONE;
        Interop.Win32.DwmSetWindowAttribute(hwnd, Interop.Win32.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        // No system backdrop: an active window would otherwise get one painted behind it.
        var none = Interop.Win32.DWMSBT_NONE;
        Interop.Win32.DwmSetWindowAttribute(hwnd, Interop.Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        // The frame "extended" over the whole window: where nothing is drawn, DWM shows
        // what's behind instead of the window's black background.
        var sheet = new Interop.Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Interop.Win32.DwmExtendFrameIntoClientArea(hwnd, ref sheet);
    }

    /// The window's size in physical pixels for a size in points on its monitor.
    public static Windows.Graphics.SizeInt32 Scaled(Window window, double width, double height)
    {
        var scale = Interop.Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        return new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
    }

    /// Centres a window on the monitor it's on.
    public static void Center(Window window)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
