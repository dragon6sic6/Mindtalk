using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Mindtalk.Core.Speech;
using Mindtalk.Platform;

namespace Mindtalk.App;

/// <summary>
/// A tray app: no window at startup (unless there's setup to do), a global dictation key,
/// and the HUD while you talk.
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
    private Action<Hotkey>? _capture;

    public AppSettings Settings { get; private set; } = null!;
    public SpeechService Speech { get; private set; } = null!;
    public DictationController Dictation { get; private set; } = null!;
    public KeyboardHook Hook { get; private set; } = null!;

    public event Action? Changed;

    public App()
    {
        InitializeComponent();
        // Closing the window hides it; the app lives in the tray until you quit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One Mindtalk at a time; opening it again shows the window of the one running.
        _single = new Mutex(true, @"Local\Mindtalk.App", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Mindtalk.Show");
        if (!first)
        {
            _showSignal.Set();
            Exit();
            return;
        }

        _ui = DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => _ui.TryEnqueue(ShowMain), null, -1, false);

        Settings = AppSettings.Load();
        Hotkey.KeyNames = KeyboardHook.KeyName;
        var model = SpeechModel.Find(Settings.Model) ?? SpeechModel.Swedish;
        Speech = new SpeechService(new ModelStore(), model);
        Speech.Changed += () => _ui.TryEnqueue(Refresh);

        IRecorder recorder = new WasapiRecorder();
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var testAudio = Array.IndexOf(arguments, "--test-audio");
        if (testAudio > 0 && testAudio + 1 < arguments.Length) recorder = new FileRecorder(WavFile.Read16kMono(arguments[testAudio + 1]));
#endif
        Dictation = new DictationController(recorder, Speech, new TextInserter(), new DispatcherScheduler(_ui), Settings);
        Dictation.ShowWindowRequested += ShowMain;
        Dictation.StateChanged += _ => Refresh();

        Hook = new KeyboardHook(Settings.Hotkey, signal => _ui.TryEnqueue(() => OnKey(signal)));
        Hook.Router.IsRecording = () => Dictation.IsRecording;
        Hook.Router.SpaceLocks = () => Dictation.SpaceLocks;
        Dictation.ResyncRequested += Hook.Resync;
        Hook.Start();

        _hud = new HudWindow(Dictation, () => Settings.Hotkey.Name);

        _tray = new TrayIcon(IconPath) { Menu = TrayMenu };
        _tray.Clicked += ShowMain;
        _tray.Resumed += Hook.Resync;

        Speech.Use(model);
        Refresh();
        // First run, or no model yet: there's setup to do.
        if (!Speech.IsInstalled(model) || Environment.GetCommandLineArgs().Contains("--show")) ShowMain();
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
        Dictation.Handle(signal);
    }

    // ---- Settings actions (from the window and the tray) ------------------------

    public void UseModel(SpeechModel model)
    {
        Settings.Model = model.Id;
        Settings.Save();
        Speech.Use(model);
    }

    /// Downloads a model; with none installed yet, it becomes the one in use.
    public void Download(SpeechModel model)
    {
        if (!Speech.IsInstalled(Speech.Status.Model)) UseModel(model);
        _ = Speech.DownloadAsync(model);
    }

    public void PickHotkey(Action<Hotkey> picked)
    {
        _capture = hotkey =>
        {
            Settings.Hotkey = hotkey;
            Settings.Save();
            Hook.Router.Hotkey = hotkey;
            picked(hotkey);
            Refresh();
        };
        Hook.Router.BeginCapture();
    }

    public void StopPickingHotkey()
    {
        _capture = null;
        Hook.Router.CancelCapture();
    }

    public void SaveSettings()
    {
        Settings.Save();
        Refresh();
    }

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

    // ---- Window and tray ------------------------------------------------------

    public void ShowMain()
    {
        _main ??= new MainWindow(this);
        _main.ShowAndActivate();
    }

    /// One line on what Mindtalk is doing, for the tray tooltip, menu and window.
    public string StatusLine()
    {
        var status = Speech.Status;
        if (Speech.DownloadProgress(status.Model) is { } progress)
            return Loc.T($"Laddar ned {status.Model.Title.ToLowerInvariant()} – {progress:P0}", $"Downloading {status.Model.ModelName} — {progress:P0}");
        return status.Phase switch
        {
            ModelPhase.Ready => Loc.T($"Håll in {Settings.Hotkey.Name.ToLowerInvariant()} och prata", $"Hold {Settings.Hotkey.Name} and speak"),
            ModelPhase.Loading => Loc.T("Språkmodellen startar…", "The speech model is starting…"),
            ModelPhase.Failed => Loc.T("Språkmodellen startade inte", "The speech model didn't start"),
            _ => Loc.T("Ladda ned en språkmodell för att börja", "Download a speech model to begin"),
        };
    }

    private void Refresh()
    {
        if (_tray is not null) _tray.Tooltip = "Mindtalk – " + StatusLine();
        Changed?.Invoke();
    }

    private IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var items = new List<TrayMenuItem> { new(StatusLine()), TrayMenuItem.Separator };
        foreach (var model in SpeechModel.All)
        {
            var installed = Speech.IsInstalled(model);
            items.Add(new TrayMenuItem($"{model.Title} – {model.ModelName}",
                installed ? () => UseModel(model) : ShowMain,
                Checked: Speech.Status.Model == model));
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Loc.T("Öppna Mindtalk…", "Open Mindtalk…"), ShowMain));
        items.Add(new TrayMenuItem(Loc.T("Avsluta Mindtalk", "Quit Mindtalk"), Quit));
        return items;
    }

    public void Quit()
    {
        Hook.Dispose();
        _tray?.Dispose();
        Speech.Dispose();
        _single?.ReleaseMutex();
        Exit();
    }
}
