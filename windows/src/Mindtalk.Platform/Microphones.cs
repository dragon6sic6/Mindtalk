using System.Diagnostics;
using Mindtalk.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Mindtalk.Platform;

// Choosing the microphone (as on the Mac): the built-in mic is the default — a Bluetooth
// headset drops to a low-quality call profile the moment its mic opens, which hurts
// recognition and your music. "Automatiskt" follows Windows' default input instead, or
// pick any other device. A chosen device that's gone falls back to the system input.

public enum InputKind
{
    /// Part of the PC: a laptop's mic array, or the sound card's own mic jack.
    BuiltIn,
    Bluetooth,
    Usb,
    /// From another app (meeting apps, virtual cables) — hidden by default.
    Virtual,
    Other,
}

public sealed record InputDevice(string Id, string Name, InputKind Kind)
{
    public bool IsBuiltIn => Kind == InputKind.BuiltIn;
    public bool IsBluetooth => Kind == InputKind.Bluetooth;
    public bool IsVirtual => Kind == InputKind.Virtual;
}

/// <summary>
/// The capture endpoints, which one the settings' choice means, and a live level preview
/// for the picker. <see cref="Changed"/> and <see cref="LevelsChanged"/> are raised on
/// background threads — marshal to the UI thread.
/// </summary>
public sealed class Microphones : IDisposable
{
    /// The settings' choice strings; anything else is an endpoint ID.
    public const string BuiltInChoice = "builtin", SystemChoice = "system";
    public const int HistoryLength = 48;

    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey JackSubType = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 8);
    /// Devices that are part of the PC itself share this container.
    private static readonly Guid MachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    /// KSNODETYPE_MICROPHONE_ARRAY: a laptop's own mics — preferred over a jack.
    private static readonly Guid MicrophoneArray = new("dff21be5-f70f-11d0-b917-00a0c9223196");

    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _notifier = new();
    private readonly Notifications _notifications;
    private IReadOnlyList<InputDevice> _devices = [];
    private InputDevice? _systemDefault;
    private InputDevice? _builtIn;

    public Microphones()
    {
        Refresh();
        _notifications = new Notifications(this);
        _notifier.RegisterEndpointNotificationCallback(_notifications);
    }

    public IReadOnlyList<InputDevice> Devices { get { lock (_gate) return _devices; } }
    /// Windows' default input right now.
    public InputDevice? SystemDefault { get { lock (_gate) return _systemDefault; } }
    /// The PC's own microphone, if it has one.
    public InputDevice? BuiltIn { get { lock (_gate) return _builtIn; } }

    /// A mic was plugged in or removed, or Windows' default input changed.
    public event Action? Changed;

    public void Refresh()
    {
        var devices = new List<InputDevice>();
        var builtIns = new List<(InputDevice Device, bool Array)>();
        string? defaultId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var input = new InputDevice(device.ID, device.FriendlyName, Classify(device));
                    devices.Add(input);
                    if (input.IsBuiltIn) builtIns.Add((input, Read(device, JackSubType) is Guid jack && jack == MicrophoneArray));
                }
            }
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultId = device.ID;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Microphones: listing failed ({ex.GetType().Name})");
        }
        lock (_gate)
        {
            _devices = devices;
            _systemDefault = devices.FirstOrDefault(d => d.Id == defaultId);
            _builtIn = builtIns.OrderByDescending(b => b.Array).Select(b => b.Device).FirstOrDefault();
        }
    }

    /// <summary>The endpoint to record from; null means Windows' default input.</summary>
    public string? SelectedDeviceId(string? choice) => choice switch
    {
        null or BuiltInChoice => BuiltIn?.Id,
        SystemChoice => null,
        _ => Devices.FirstOrDefault(d => d.Id == choice)?.Id,
    };

    /// <summary>"Inbyggd mikrofon", "Automatiskt (Headset)", or the chosen mic's name.</summary>
    public string SelectionName(string? choice) => choice switch
    {
        null or BuiltInChoice => BuiltIn is null ? SystemName : Loc.T("Inbyggd mikrofon", "Built-in microphone"),
        SystemChoice => SystemName,
        _ => Devices.FirstOrDefault(d => d.Id == choice)?.Name
             ?? Loc.T($"{SystemName} – vald mikrofon saknas", $"{SystemName} – chosen microphone missing"),
    };

    public string SystemName => SystemDefault is { } device
        ? Loc.T($"Automatiskt ({device.Name})", $"Automatic ({device.Name})")
        : Loc.T("Automatiskt", "Automatic");

    // ---- Level preview ---------------------------------------------------------------

    private readonly object _monitorGate = new();
    private Task _monitorWork = Task.CompletedTask;
    private int _monitorGeneration;
    private WasapiRecorder? _monitor;
    private readonly float[] _history = new float[HistoryLength];
    private long _lastPushTicks;
    private float _level;

    /// Live level of the previewed mic (0…1).
    public float Level => Volatile.Read(ref _level);

    /// The last few seconds of levels, oldest first — drawn as a waveform.
    public IReadOnlyList<float> History { get { lock (_history) return (float[])_history.Clone(); } }

    /// The history moved on (at most ~16 times a second).
    public event Action? LevelsChanged;

    /// <summary>
    /// Listens to the mic <paramref name="choice"/> means, for the picker. Opening a mic can
    /// take a moment (a Bluetooth headset switches profile), so it happens in the background;
    /// a mic that can't be opened just shows no level.
    /// </summary>
    public void StartMonitor(string? choice)
    {
        var id = SelectedDeviceId(choice);
        Enqueue(generation =>
        {
            StopRecorder();
            if (generation != Volatile.Read(ref _monitorGeneration)) return;
            var recorder = new WasapiRecorder(keepSamples: false);
            recorder.Level += Push;
            try
            {
                recorder.Start(id);
                _monitor = recorder;
            }
            catch (Exception ex)
            {
                recorder.Level -= Push;
                Trace.WriteLine($"Microphones: preview couldn't open the mic ({ex.GetType().Name})");
            }
        });
    }

    public void StopMonitor()
    {
        Enqueue(_ => StopRecorder());
        Volatile.Write(ref _level, 0);
        lock (_history) Array.Clear(_history);
        LevelsChanged?.Invoke();
    }

    /// <summary>How well the chosen mic hears you, from the last second of levels.</summary>
    public (string Text, bool? Good) Hearing
    {
        get
        {
            float peak;
            lock (_history) peak = _history.Skip(HistoryLength - 16).Max();
            if (peak > 0.3f) return (Loc.T("Vi hör dig tydligt", "We hear you clearly"), true);
            if (peak > 0.07f) return (Loc.T("Lite svagt – prata närmare mikrofonen", "A bit faint – move closer to the mic"), false);
            return (Loc.T("Säg något för att testa", "Say something to test"), null);
        }
    }

    /// One monitor change at a time, in order; a newer request makes an older start moot.
    private void Enqueue(Action<int> work)
    {
        lock (_monitorGate)
        {
            var generation = ++_monitorGeneration;
            _monitorWork = _monitorWork.ContinueWith(_ => work(generation), TaskScheduler.Default);
        }
    }

    private void StopRecorder()
    {
        if (_monitor is not { } recorder) return;
        _monitor = null;
        recorder.Level -= Push;
        recorder.Stop();
        recorder.Dispose();
    }

    private void Push(float value)
    {
        Volatile.Write(ref _level, value);
        // ~16 values a second, so the waveform scrolls at a calm pace.
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPushTicks), now) < TimeSpan.FromSeconds(0.06)) return;
        Interlocked.Exchange(ref _lastPushTicks, now);
        lock (_history)
        {
            Array.Copy(_history, 1, _history, 0, HistoryLength - 1);
            _history[^1] = value;
        }
        LevelsChanged?.Invoke();
    }

    // ---- What kind of mic ------------------------------------------------------------

    /// <summary>
    /// From the endpoint's properties: the bus its driver hangs off (HDAUDIO, USB, BTHENUM…),
    /// whether Windows counts it as part of the PC, and its form factor.
    /// </summary>
    private static InputKind Classify(MMDevice device)
    {
        var bus = (Read(device, EnumeratorName) as string ?? "").ToUpperInvariant();
        var form = Read(device, FormFactor) switch { uint u => (int)u, int i => i, _ => -1 };
        // Part of the PC per Windows (a laptop's internal USB camera, too). Software devices
        // without a container of their own land there as well, so it's never the only sign.
        var integrated = Read(device, ContainerId) is Guid container && container == MachineContainer;
        // Line in, stereo mix, S/PDIF: an input, but not a microphone.
        var microphone = form is 3 /* headphones */ or 4 /* microphone */ or 5 /* headset */ or 6 /* handset */;

        if (bus.StartsWith("BTH")) return InputKind.Bluetooth;   // BTHENUM, BTHHFENUM, BTHLE, BTHLEDEVICE…
        if (bus == "ROOT" || bus.StartsWith("VIRTUAL")) return InputKind.Virtual;
        // Software devices: meeting apps' mics — or, with some newer drivers, the real thing.
        if (bus is "SWD" or "SW") return integrated && microphone ? InputKind.BuiltIn : InputKind.Virtual;
        if (bus == "USB") return integrated && microphone ? InputKind.BuiltIn : InputKind.Usb;
        // The onboard codec or DSP: a laptop's mic array, or the PC's own mic jack.
        if (bus is "HDAUDIO" or "INTELAUDIO" or "ACPI" or "PCI" or "SOUNDWIRE" or "ACX" or "AMDACP")
            return microphone ? InputKind.BuiltIn : InputKind.Other;
        return integrated && microphone ? InputKind.BuiltIn : InputKind.Other;
    }

    /// <summary>The endpoint properties behind the kind, for the self-test.</summary>
    public static string Describe(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return $"bus={Read(device, EnumeratorName) ?? "?"} form={Read(device, FormFactor) ?? "?"} " +
                   $"container={Read(device, ContainerId) ?? "?"} jack={Read(device, JackSubType) ?? "?"}";
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static object? Read(MMDevice device, PropertyKey key)
    {
        try
        {
            var store = device.Properties;
            if (!store.Contains(key)) return null;
            var value = store[key].Value;
            return value is string s && Guid.TryParse(s, out var guid) ? guid : value;
        }
        catch { return null; }   // a value type NAudio can't convert
    }

    public void Dispose()
    {
        try { _notifier.UnregisterEndpointNotificationCallback(_notifications); } catch { /* already gone */ }
        _notifier.Dispose();
        lock (_monitorGate) _monitorGeneration++;
        Enqueue(_ => StopRecorder());
    }

    /// <summary>
    /// Device notifications arrive on a Windows audio thread that mustn't be held up (or call
    /// back into the enumerator), so the work happens on the thread pool.
    /// </summary>
    private sealed class Notifications(Microphones owner) : IMMNotificationClient
    {
        /// Capture endpoint IDs start like this; render ones with {0.0.0.
        private static bool IsCapture(string? id) => id?.StartsWith("{0.0.1.", StringComparison.Ordinal) ?? true;

        private void Update() => Task.Run(() =>
        {
            owner.Refresh();
            owner.Changed?.Invoke();
        });

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { if (IsCapture(deviceId)) Update(); }
        public void OnDeviceAdded(string pwstrDeviceId) { if (IsCapture(pwstrDeviceId)) Update(); }
        public void OnDeviceRemoved(string deviceId) { if (IsCapture(deviceId)) Update(); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console) Update();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
