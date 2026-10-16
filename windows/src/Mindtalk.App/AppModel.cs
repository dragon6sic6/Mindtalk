using System.Diagnostics;
using Microsoft.Win32;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Speech;
using Mindtalk.Platform;

namespace Mindtalk.App;

/// <summary>The main window's pages.</summary>
public enum Page { Dictation, Recent, Vocabulary, Settings }

/// <summary>How the language in use is doing (the Mac's Dictation.ModelState).</summary>
public enum ModelState { Missing, Downloading, Loading, Ready, Failed }

/// <summary>What stands between you and dictating right now, most important first.</summary>
public enum SetupIssue { Microphone, ModelMissing, ModelDownloading, ModelFailed }

/// <summary>
/// What the windows show and the actions they offer — the Mac app's Dictation and AppPrefs as
/// one place. Everything here runs on the UI thread; <see cref="Changed"/> says "look again".
/// </summary>
public partial class App
{
    /// Something the windows show has changed. Raised on the UI thread.
    public event Action? Changed;

    public void Refresh()
    {
        _tray?.Update(TrayState);
        Changed?.Invoke();
    }

    // ---- The language model -------------------------------------------------------

    /// The language you dictate in.
    public SpeechModel Engine => Speech.Status.Model;

    public ModelState Model => Speech.Status.Phase switch
    {
        ModelPhase.Ready => ModelState.Ready,
        ModelPhase.Loading => ModelState.Loading,
        ModelPhase.Downloading => ModelState.Downloading,
        ModelPhase.Failed => ModelState.Failed,
        _ => Speech.DownloadError(Engine) is not null ? ModelState.Failed : ModelState.Missing,
    };

    /// The active model's download progress (0…1), if it's downloading.
    public double EngineProgress => Speech.DownloadProgress(Engine) ?? 0;

    /// Why the active model failed (a download error or a load error).
    public string? ModelError => Speech.DownloadError(Engine) ?? Speech.Status.Error;

    public bool IsInstalled(SpeechModel model) => Speech.IsInstalled(model);
    public int InstalledCount => SpeechModel.All.Count(Speech.IsInstalled);

    /// Languages to switch to once their download is done (asked for from the menu).
    private readonly HashSet<SpeechModel> _useWhenReady = [];
    private bool _engineSwitchPending;

    public void SetEngine(SpeechModel model)
    {
        if (model == Engine && Settings.Model == model.Id) return;
        Settings.Model = model.Id;
        Settings.Save();
        // Mid-dictation the old model finishes the job first.
        if (Dictation.IsBusy) { _engineSwitchPending = true; _pendingEngine = model; Refresh(); return; }
        Speech.Use(model);
    }

    private SpeechModel? _pendingEngine;

    /// The dictation is over: a language switch that waited can happen now.
    private void SwitchIfPending()
    {
        if (!_engineSwitchPending || Dictation.IsBusy) return;
        _engineSwitchPending = false;
        if (_pendingEngine is { } model) Speech.Use(model);
        _pendingEngine = null;
    }

    public void DownloadModel(SpeechModel? which = null, bool thenUse = false)
    {
        var model = which ?? Engine;
        if (thenUse) _useWhenReady.Add(model);
        if (Speech.DownloadProgress(model) is not null || Speech.IsInstalled(model)) return;
        _ = DownloadAsync(model);
    }

    private async Task DownloadAsync(SpeechModel model)
    {
        await Speech.DownloadAsync(model);
        if (_useWhenReady.Remove(model) && Speech.IsInstalled(model) && model != Engine) SetEngine(model);
        Refresh();
    }

    public void CancelDownload(SpeechModel? which = null) => Speech.CancelDownload(which ?? Engine);

    public void RemoveModel(SpeechModel model) => Speech.Remove(model);

    // ---- Permissions and setup ----------------------------------------------------

    public bool MicGranted { get; private set; } = true;

    /// Windows doesn't announce a changed privacy setting — check when a window comes forward.
    public void RefreshPermissions()
    {
        var granted = MicrophoneAccess.IsAllowed;
        if (granted == MicGranted) return;
        MicGranted = granted;
        Refresh();
    }

    public void RequestMicrophone() => MicrophoneAccess.OpenSettings();

    public bool IsReady
    {
        get
        {
#if DEBUG
            if (Demo.On) return true;
#endif
            return Model == ModelState.Ready && MicGranted;
        }
    }

    /// Something to do or wait out: a permission, a download, a failure. A model that's
    /// merely starting isn't — it's ready in a moment.
    public bool NeedsSetup
    {
        get
        {
#if DEBUG
            if (Demo.On) return false;
#endif
            return !MicGranted || Model is ModelState.Missing or ModelState.Downloading or ModelState.Failed;
        }
    }

    public SetupIssue? Issue
    {
        get
        {
            if (!MicGranted) return SetupIssue.Microphone;
            return Model switch
            {
                ModelState.Missing => SetupIssue.ModelMissing,
                ModelState.Downloading => SetupIssue.ModelDownloading,
                ModelState.Failed => SetupIssue.ModelFailed,
                _ => null,
            };
        }
    }

    /// The issue said plainly, with one action that fixes it.
    public string IssueTitle(SetupIssue issue)
    {
        var model = Engine.Title.ToLower(Loc.Culture);
        return issue switch
        {
            SetupIssue.Microphone => Loc.T("Tillåt mikrofonen", "Allow the microphone"),
            SetupIssue.ModelMissing => Loc.T($"Ladda ned språkmodellen ({model})", $"Download the language model ({model})"),
            SetupIssue.ModelDownloading => Loc.T($"Laddar ned språkmodellen – {(int)(EngineProgress * 100)} %",
                                                 $"Downloading the language model – {(int)(EngineProgress * 100)}%"),
            _ => Loc.T("Språkmodellen startade inte – försök igen", "The language model didn’t start – try again"),
        };
    }

    /// Why it's needed, for a tooltip.
    public static string IssueWhy(SetupIssue issue) => issue == SetupIssue.Microphone
        ? Loc.T("Behövs för att höra vad du säger.", "Needed to hear what you say.")
        : Loc.T("Taligenkänningen körs på din dator och behöver modellen en gång.",
                "Speech recognition runs on your PC and needs the model once.");

    public void Fix(SetupIssue issue)
    {
        switch (issue)
        {
            case SetupIssue.Microphone: RequestMicrophone(); break;
            case SetupIssue.ModelMissing or SetupIssue.ModelFailed: DownloadModel(); break;
            case SetupIssue.ModelDownloading: ShowMain(Page.Settings); break;
        }
    }

    // ---- The dictation key ------------------------------------------------------

    public bool PickingKey { get; private set; }

    public void PickKey()
    {
        PickingKey = true;
        _capture = key =>
        {
            PickingKey = false;
            Settings.Hotkey = key;
            Settings.Save();
            Hook.Router.Hotkey = key;
            Hook.Router.Reset();
            Refresh();
        };
        Hook.Router.BeginCapture();
        Refresh();
    }

    public void StopPickingKey()
    {
        if (!PickingKey) return;
        PickingKey = false;
        _capture = null;
        Hook.Router.CancelCapture();
        Refresh();
    }

    public void SetMode(DictationMode mode)
    {
        Settings.Mode = mode;
        Settings.Save();
        Refresh();
    }

    public void SaveSettings()
    {
        Settings.Save();
        Refresh();
    }

    // ---- Appearance -----------------------------------------------------------------

    /// The windows have to be drawn again in the other appearance.
    public event Action? ThemeChanged;

    /// Windows' own choice for apps (Settings → Personalization → Colors).
    private static bool SystemIsDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
    }

    private bool ResolveDark() => Settings.Appearance switch
    {
        Appearance.Dark => true,
        Appearance.Light => false,
        _ => SystemIsDark,
    };

    /// Light, Dark or following Windows — applied to every window at once.
    public void ApplyAppearance()
    {
        var dark = ResolveDark();
        if (dark == Controls.UI.Dark) return;
        Controls.UI.Dark = dark;
        ThemeChanged?.Invoke();
    }

    public void SetAppearance(Appearance appearance)
    {
        Settings.Appearance = appearance;
        Settings.Save();
        ApplyAppearance();
        Refresh();
    }

    // ---- App ------------------------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool OpensAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Mindtalk") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Mindtalk", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Mindtalk", throwOnMissingValue: false);
        }
    }

    /// Quits and starts again (to switch the app's language), reopening the window on <paramref name="page"/>.
    public void Relaunch(Page page)
    {
        Settings.ReopenPage = page.ToString();
        Settings.Save();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });
        Quit();
    }

    public static string Version =>
        typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
}
