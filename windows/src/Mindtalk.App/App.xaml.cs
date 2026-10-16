using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Mindtalk.Core.Speech;
using Mindtalk.Core.Stats;
using Mindtalk.Core.Vocabulary;
using Mindtalk.Platform;

namespace Mindtalk.App;

/// <summary>
/// A tray app: no window at startup (unless there's setup to do), a global dictation key,
/// and the HUD while you talk. The introduction on first launch; the main window and the
/// tray panel on demand.
/// </summary>
public partial class App : Application
{
    public static App Instance => (App)Current;
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Mindtalk.ico");

    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private DispatcherQueue _ui = null!;
    private TrayIcon? _tray;
    private HudWindow? _hud;
    private MainWindow? _main;
    private OnboardingWindow? _onboarding;
    private StatusPanel? _panel;
    private Action<Hotkey>? _capture;
    private readonly TextInserter _output = new();

    public AppSettings Settings { get; private set; } = null!;
    public SpeechService Speech { get; private set; } = null!;
    public DictationController Dictation { get; private set; } = null!;
    public KeyboardHook Hook { get; private set; } = null!;
    public VocabularyStore Vocabulary { get; private set; } = null!;
    public StatsStore Stats { get; private set; } = null!;
    public RecentStore Recent { get; private set; } = null!;
    public Microphones Microphones { get; private set; } = null!;
    public MediaControl Media { get; private set; } = null!;
    public IPolisher Polisher { get; private set; } = null!;
    public Updates Updates { get; private set; } = null!;
    public DispatcherQueue UI => _ui;

    public App()
    {
        InitializeComponent();
        // Closing the window hides it; the app lives in the tray until you quit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        // Relaunched to switch language: the old one has to be gone first.
        var wait = Array.IndexOf(arguments, "--wait-for");
        if (wait > 0 && wait + 1 < arguments.Length && int.TryParse(arguments[wait + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(5000); } catch (ArgumentException) { }
        }

        // One Mindtalk at a time; opening it again shows the window of the one running.
        var instance = "Mindtalk";
#if DEBUG
        // Debug builds from different checkouts may run side by side.
        instance += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..12];
#endif
        _single = new Mutex(true, $@"Local\{instance}.App", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instance}.Show");
        if (!first)
        {
            _showSignal.Set();
            Exit();
            return;
        }

        _ui = DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => _ui.TryEnqueue(Reopen), null, -1, false);

        Settings = AppSettings.Load(Demo.SettingsPath);
        Loc.Use(Settings.Language);
#if DEBUG
        Demo.Prepare(Settings);
#endif
        Hotkey.KeyNames = KeyboardHook.KeyName;
        Controls.UI.Dark = ResolveDark();

        Media = new MediaControl(() => Settings.MediaMode, ducked => _ui.TryEnqueue(() => { Settings.Ducked = ducked; Settings.Save(); }));
        Media.RestoreAfterCrash(Settings.Ducked);

        var store = new ModelStore(Demo.ModelsDirectory);
        store.RemoveLeftovers();
        var model = SpeechModel.Find(Settings.Model ?? "") ?? SpeechModel.Recommended;
        Speech = new SpeechService(store, model);
        Speech.Changed += () => _ui.TryEnqueue(Refresh);

        Vocabulary = new VocabularyStore(Demo.File("vocabulary.json")) { IsDictionaryWord = SpellCheck.IsDictionaryWord };
        Stats = new StatsStore(Demo.File("stats.json"));
        Recent = new RecentStore(Demo.File("Recent.json"));
        foreach (var changed in new Action<Action>[] { a => Vocabulary.Changed += a, a => Stats.Changed += a, a => Recent.Changed += a })
            changed(() => _ui.TryEnqueue(Refresh));

        Microphones = new Microphones();
        Microphones.Changed += () => _ui.TryEnqueue(Refresh);
        Polisher = new OnDevicePolisher();
        Updates = new Updates(this);

        IRecorder recorder = new WasapiRecorder();
#if DEBUG
        var testAudio = Array.IndexOf(arguments, "--test-audio");
        if (testAudio > 0 && testAudio + 1 < arguments.Length) recorder = new FileRecorder(WavFile.Read16kMono(arguments[testAudio + 1]));
#endif
        Dictation = new DictationController(recorder, Speech, _output, new DispatcherScheduler(_ui), Settings)
        {
            SelectedMicrophone = () => Microphones.SelectedDeviceId(Settings.Microphone),
            MicrophoneAllowed = () => MicGranted,
            IsOnboarding = () => IsOnboarding,
            Engine = () => Engine.Id,
            InstalledModels = () => InstalledCount,
            Focus = new FocusInspector(),
            Media = Media,
            Cues = new Cues(() => Settings.Sounds),
            Polisher = Polisher,
            ApplyVocabulary = Vocabulary.Apply,
            ReapplyVocabulary = Vocabulary.Reapply,
            RecordStats = Stats.Record,
            AddRecent = text => Recent.Add(text, DateTime.Now),
            LatestRecent = () => Recent.Latest?.Text,
            PasteModifiersHeld = () => new[] { VirtualKey.LeftCtrl, VirtualKey.RightCtrl, VirtualKey.LeftAlt, VirtualKey.RightAlt }
                .Any(KeyboardHook.IsPhysicallyDown),
            Beep = Platform.Beep.Play,
        };
        Dictation.ShowWindowRequested += () => ShowMain();
        Dictation.StateChanged += _ => { SwitchIfPending(); Refresh(); };

        Hook = new KeyboardHook(Settings.Hotkey, signal => _ui.TryEnqueue(() => OnKey(signal)));
        Hook.Router.IsRecording = () => Dictation.IsRecording;
        Hook.Router.SpaceLocks = () => Dictation.SpaceLocks;
        Dictation.ResyncRequested += Hook.Resync;
        Hook.Start();

        _hud = new HudWindow(() => Dictation.State, Dictation.Levels, Dictation.CancelFromHud, Dictation.FinishFromHud);
        Dictation.StateChanged += _hud.Update;
#if DEBUG
        if (Demo.Hud(_hud)) return;
#endif

        _tray = new TrayIcon();
        _tray.Clicked += TogglePanel;
        _tray.Resumed += Hook.Resync;
        _tray.Suspending += Dictation.CancelForSleep;
        _tray.SettingsChanged += ApplyAppearance;

        Speech.Use(model);
        MicGranted = MicrophoneAccess.IsAllowed;
        Updates.Start();
        Refresh();

#if DEBUG
        if (Demo.Start(this)) return;
#endif
        if (!Settings.DidOnboard)
        {
            // Start at login from the first launch — as an installed app, that is.
            if (!Debugger.IsAttached && !AppContext.BaseDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                OpensAtLogin = true;
            ShowOnboarding();
        }
        else if (Enum.TryParse<Page>(Settings.ReopenPage, out var page))
        {
            Settings.ReopenPage = null;
            Settings.Save();
            ShowMain(page);
        }
        else if (NeedsSetup || arguments.Contains("--show")) ShowMain();
    }

    private void OnKey(KeySignal signal)
    {
        if (signal is KeySignal.Captured captured)
        {
            var done = _capture;
            _capture = null;
            done?.Invoke(captured.Hotkey);
            return;
        }
        // Esc also closes the tray panel.
        if (signal is KeySignal.Escape && !Dictation.IsRecording) { _panel?.Close(); return; }
        Dictation.Handle(signal);
    }

    // ---- Windows --------------------------------------------------------------------

    public bool IsOnboarding => _onboarding?.IsOpen == true;

    /// Opened again from the Start menu or a shortcut: the introduction if it's open, else the window.
    private void Reopen()
    {
        if (IsOnboarding) _onboarding!.Activate();
        else ShowMain();
    }

    public void ShowMain(Page? page = null)
    {
        _main ??= new MainWindow(this);
        _main.ShowAndActivate(page);
    }

    public void ShowOnboarding()
    {
        if (_onboarding is null)
        {
            _onboarding = new OnboardingWindow(this);
            _onboarding.Finished += () =>
            {
                // Closing the introduction — however it's closed — means it's done.
                StopPickingKey();
                Settings.DidOnboard = true;
                Settings.Save();
                _onboarding = null;
                Refresh();
            };
        }
        _onboarding.Activate();
    }

    /// The introduction again: inside the main window if it's open, else in its own window.
    public void ShowIntroduction()
    {
        if (_main?.IsOpen == true) _main.ShowIntroduction();
        else ShowOnboarding();
    }

    public void TogglePanel()
    {
        _panel ??= new StatusPanel(this);
        _panel.Toggle(_tray!.Bounds());
    }

    public void ClosePanel() => _panel?.Close();

    public void ShowAbout() => ShowMain(Page.Settings, about: true);

    public void ShowMain(Page page, bool about)
    {
        ShowMain(page);
        if (about) _main?.ScrollToAbout();
    }

    /// For the tray icon: what Mindtalk is doing.
    private TrayState TrayState => Dictation.State.Phase switch
    {
        Phase.Recording => TrayState.Recording,
        Phase.Transcribing => TrayState.Transcribing,
        Phase.Failed => TrayState.Failed,
        Phase.Copied => TrayState.Copied,
        _ => Model == ModelState.Downloading ? TrayState.Downloading : TrayState.Idle,
    };

    public void Quit()
    {
        Media.RestoreNow();
        Hook.Dispose();
        _tray?.Dispose();
        Speech.Dispose();
        _single?.ReleaseMutex();
        Exit();
    }
}
