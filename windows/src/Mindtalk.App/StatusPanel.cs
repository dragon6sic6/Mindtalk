using Windows.Foundation;

namespace Mindtalk.App;

/// <summary>The tray panel (the Mac's menu bar panel). Placeholder until it's built.</summary>
public sealed class StatusPanel(App app)
{
    public bool IsShown { get; private set; }

    /// Opens next to the tray icon at <paramref name="anchor"/> (physical pixels), or closes.
    public void Toggle(Rect anchor) => app.ShowMain();

    public void Close() => IsShown = false;
}
