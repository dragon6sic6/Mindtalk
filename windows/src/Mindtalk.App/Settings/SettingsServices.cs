using Mindtalk.Core.AI;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Settings;

/// <summary>
/// The services Settings shows, as the app has them (null until they've started).
/// </summary>
internal static class SettingsServices
{
    public static IWriterService? Writers(App app) => app.Writers;
    public static IAiApps? AiApps(App app) => app.AiApps;
    public static IMeetingService? Meetings(App app) => app.MeetingService;
}
