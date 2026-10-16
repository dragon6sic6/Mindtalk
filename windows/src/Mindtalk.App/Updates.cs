namespace Mindtalk.App;

/// <summary>
/// Updates: looks for a newer Mindtalk once a day (when allowed) or when asked, and offers
/// it in the tray panel — the Mac uses Sparkle for this.
/// </summary>
public sealed class Updates(App app)
{
    /// A newer version found by a scheduled check — shown in the tray panel.
    public string? Available { get; private set; }

    public bool Automatic
    {
        get => app.Settings.AutomaticUpdates;
        set { app.Settings.AutomaticUpdates = value; app.SaveSettings(); }
    }

    public void Start() { }

    /// "Check for updates…"
    public void CheckNow() => Platform.Beep.Play();   // debug builds don't update
}
