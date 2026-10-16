using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mindtalk.App;

/// <summary>
/// A window background that isn't there: only what the XAML draws is visible, with
/// per-pixel transparency — so the HUD can be a capsule with a soft shadow instead of a
/// rectangle. (The system backdrop takes a brush from the system compositor, which needs
/// a Windows.System dispatcher queue on the thread.)
/// </summary>
internal sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static object? _queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        target.SystemBackdrop = Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    private static Windows.UI.Composition.Compositor Compositor
    {
        get
        {
            if (_compositor is not null) return _compositor;
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            {
                var options = new DispatcherQueueOptions
                {
                    dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                    threadType = 2,          // DQTYPE_THREAD_CURRENT
                    apartmentType = 0,       // DQTAT_COM_NONE
                };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
                _queueController = controller;
            }
            return _compositor = new Windows.UI.Composition.Compositor();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);
}
