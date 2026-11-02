using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Mindtalk.App;

/// <summary>
/// Mindtalk's own entry point (instead of the one XAML generates): the installer and updater
/// get a word first — they run Mindtalk with their own arguments while installing, updating
/// and uninstalling — then the app starts as usual.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                AI.AiAppsService.UnregisterLinks();
                try { App.OpensAtLogin = false; } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            })
            .Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
