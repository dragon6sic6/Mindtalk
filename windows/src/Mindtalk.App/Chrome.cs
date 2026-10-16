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
