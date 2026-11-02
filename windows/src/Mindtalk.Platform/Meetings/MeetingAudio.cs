using System.Diagnostics;
using Mindtalk.Core.Meetings.Engine;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Mindtalk.Platform.Meetings;

/// <summary>
/// A meeting's sound: the chosen microphone, continuously, plus — on a call — the call app's
/// own sound through process loopback, mixed into one 16 kHz mono stream. The microphone sets
/// the pace; the call's sound is mixed into it as it comes.
/// </summary>
public sealed class MeetingAudio : IMeetingAudio
{
    /// The call's sound waits at most this long for the microphone before the oldest is dropped.
    private const int MaxCallBacklog = 16_000;

    private readonly Func<string?> _deviceId;
    private readonly Func<int?> _callProcess;
    private readonly object _gate = new();
    private readonly List<float> _call = new(MaxCallBacklog * 2);
    private StreamCapture? _mic, _loopback;
    private float _level;

    /// <param name="deviceId">The microphone to open (null: Windows' default input), asked at each start.</param>
    /// <param name="callProcess">The call app whose sound to mix in (null: none), asked at each start.</param>
    public MeetingAudio(Func<string?> deviceId, Func<int?> callProcess)
    {
        _deviceId = deviceId;
        _callProcess = callProcess;
    }

    public event Action<float[]>? Samples;
    public event Action? Interrupted;
    public float Level => Volatile.Read(ref _level);
    public bool HearsCall { get { lock (_gate) return _loopback is not null; } }

    public void Start()
    {
        Stop();
        var id = _deviceId();
        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
            device = id is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : enumerator.GetDevice(id);
        StreamCapture mic;
        try { mic = StreamCapture.ForDevice(device, "Mindtalk meeting microphone", OnMic, OnMicStopped); }
        catch
        {
            device.Dispose();
            throw;
        }

        StreamCapture? loopback = null;
        if (_callProcess() is { } pid)
        {
            try { loopback = ProcessLoopback.Open(pid, OnCall, _ => { lock (_gate) _loopback = null; }); }
            catch (Exception ex) { Trace.WriteLine($"MeetingAudio: no call sound ({ex.GetType().Name}: {ex.Message})"); }
        }
        lock (_gate)
        {
            _call.Clear();
            _mic = mic;
            _loopback = loopback;
        }
        try { mic.Start(); }
        catch
        {
            Stop();
            throw;
        }
        try { loopback?.Start(); }
        catch (Exception ex)
        {
            Trace.WriteLine($"MeetingAudio: call sound didn't start ({ex.Message})");
            lock (_gate) _loopback = null;
            loopback?.Dispose();
        }
    }

    public void Stop()
    {
        StreamCapture? mic, loopback;
        lock (_gate)
        {
            mic = _mic;
            loopback = _loopback;
            _mic = _loopback = null;
            _call.Clear();
        }
        mic?.Dispose();
        loopback?.Dispose();
        Volatile.Write(ref _level, 0);
    }

    private void OnCall(float[] samples, float rms)
    {
        lock (_gate)
        {
            _call.AddRange(samples);
            if (_call.Count > MaxCallBacklog) _call.RemoveRange(0, _call.Count - MaxCallBacklog);
        }
    }

    private void OnMic(float[] samples, float rms)
    {
        // The Mac's scale: normal speech fills the meter.
        Volatile.Write(ref _level, Math.Min(1f, rms * 12f));
        lock (_gate)
        {
            var n = Math.Min(samples.Length, _call.Count);
            for (var i = 0; i < n; i++) samples[i] = Math.Clamp(samples[i] + _call[i], -1f, 1f);
            if (n > 0) _call.RemoveRange(0, n);
        }
        Samples?.Invoke(samples);
    }

    private void OnMicStopped(Exception? error)
    {
        lock (_gate) if (_mic is null) return;
        Interrupted?.Invoke();
    }

    public void Dispose() => Stop();
}

/// <summary>What's playing the sound: headphones decide whether a call's sound is taken in.</summary>
public static class OutputDevices
{
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    /// <summary>
    /// Windows' default output is headphones, a headset or Bluetooth audio (where the microphone
    /// can't hear the others). Speakers: the microphone already does.
    /// </summary>
    public static bool DefaultIsHeadphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return false;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var store = device.Properties;
            var form = store.Contains(FormFactor) ? store[FormFactor].Value switch { uint u => (int)u, int i => i, _ => -1 } : -1;
            var bus = (store.Contains(EnumeratorName) ? store[EnumeratorName].Value as string : null)?.ToUpperInvariant() ?? "";
            return form is 3 /* headphones */ or 5 /* headset */ or 6 /* handset */ || bus.StartsWith("BTH");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"OutputDevices: {ex.GetType().Name}");
            return false;
        }
    }

    /// "Headphones (WH-1000XM4)" — for the self-test.
    public static string DefaultName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }
}
