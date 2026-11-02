using Mindtalk.Core;
using Velopack;
using Velopack.Sources;

namespace Mindtalk.App;

/// <summary>
/// Updates, the Windows counterpart of the Mac's Sparkle: Velopack looks for a newer
/// Mindtalk among the GitHub releases — once a day if allowed, or when asked — and installs
/// it only after you say so. Releases are packaged by scripts/release.ps1. Debug builds and
/// copies that weren't installed (run from a build folder) never update themselves.
/// </summary>
public sealed class Updates(App app)
{
    private const string Repository = "https://github.com/dragon6sic6/Mindtalk";
    private UpdateManager? _manager;
    private UpdateInfo? _found;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    /// A newer version found by a scheduled check — shown in the tray panel, since a
    /// window could be behind other apps.
    public string? Available { get; private set; }

    /// Look for updates on its own, once a day.
    public bool Automatic
    {
        get => app.Settings.AutomaticUpdates;
        set { app.Settings.AutomaticUpdates = value; app.SaveSettings(); }
    }

    private bool Installed => _manager?.IsInstalled == true;

    public void Start()
    {
#if !DEBUG
        try
        {
            _manager = new UpdateManager(new GithubSource(Repository, null, false));
        }
        catch (Exception)
        {
            return;
        }
        if (!Installed) return;
        // A scheduled check is quiet: it never takes focus — you may be dictating.
        _timer = app.UI.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += async (t, _) =>
        {
            t.Interval = TimeSpan.FromHours(24);
            if (Automatic) await CheckQuietly();
        };
        _timer.Start();
#endif
    }

    private async Task CheckQuietly()
    {
        try
        {
            _found = await _manager!.CheckForUpdatesAsync();
            Available = _found?.TargetFullRelease.Version.ToString();
            app.Refresh();
        }
        catch (Exception)
        {
            // Offline or GitHub unreachable: try again tomorrow.
        }
    }

    /// "Check for Updates…": says what it found, and installs it if you want.
    public async void CheckNow()
    {
        if (!Installed)
        {
            Platform.Beep.Play();   // debug builds and uninstalled copies don't update
            return;
        }
        try
        {
            _found = await _manager!.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            await app.Ask(Loc.T("Kunde inte söka efter uppdateringar", "Couldn’t check for updates"),
                          Loc.T("Kontrollera internetanslutningen och försök igen.", "Check the internet connection and try again."),
                          Loc.T("OK", "OK"), null);
            return;
        }
        Available = _found?.TargetFullRelease.Version.ToString();
        app.Refresh();
        if (_found is null)
        {
            await app.Ask(Loc.T("Du har den senaste versionen", "You’re up to date"),
                          Loc.T($"Mindtalk {App.Version} är den senaste versionen.", $"Mindtalk {App.Version} is the newest version."),
                          Loc.T("OK", "OK"), null);
            return;
        }
        var install = await app.Ask(Loc.T($"Mindtalk {Available} finns", $"Mindtalk {Available} is available"),
                                    Loc.T($"Du har {App.Version}. Mindtalk startar om när den nya versionen är installerad.",
                                          $"You have {App.Version}. Mindtalk restarts once the new version is installed."),
                                    Loc.T("Installera och starta om", "Install and Restart"), Loc.T("Inte nu", "Not Now"));
        if (!install || app.Dictation.IsBusy) return;
        try
        {
            await _manager.DownloadUpdatesAsync(_found);
            app.Media.RestoreNow();
            _manager.ApplyUpdatesAndRestart(_found);
        }
        catch (Exception)
        {
            await app.Ask(Loc.T("Uppdateringen gick inte att installera", "The update couldn’t be installed"),
                          Loc.T("Försök igen om en stund.", "Try again in a moment."), Loc.T("OK", "OK"), null);
        }
    }
}
