using System.Collections.Concurrent;
using System.Diagnostics;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Mindtalk.Platform;

// Music while you dictate.
//
// Windows tells us two things: which apps have a media session and whether it's playing
// (the same sessions as the media flyout by the volume), and which apps have sound
// running to the speakers right now (the audio sessions on the default output).
//
// - A media app (Spotify, a browser, Media Player…) is paused through its session, and
//   played again afterwards — but only if it's still paused, which says the pause was ours.
//   Pause and play are separate commands, never a toggle, so music can never start by itself.
// - Anything else making sound (a call, a game) can't be paused, so the volume is faded
//   down while you speak and back up after.
// - "Tona ner" fades everything down; "Låt vara" leaves the sound alone.

public sealed record MediaSessionInfo(string App, string Status);
public sealed record AudioPlayerInfo(int ProcessId, string Process, bool Active);

/// <summary>
/// Pauses or fades other sound while you dictate. <see cref="Begin"/> and <see cref="End"/>
/// come from the UI thread and return at once: the work happens in order on a thread of
/// its own. The crash-safety callback is called on that thread.
/// </summary>
public sealed class MediaControl : IMediaControl, IDisposable
{
    private const float DuckLevel = 0f;
    /// A paused app has this long (from the pause) to settle before we stop waiting for it.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(1);

    private readonly Func<MediaMode> _mode;
    private readonly Action<DuckedVolume?> _persist;
    private readonly BlockingCollection<Action> _work = new();

    // Everything below is touched only on the media thread.

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _sessionManagerTried;
    private MMDeviceEnumerator? _enumerator;
    private (string Id, MMDevice Device)? _endpoint;

    /// Media apps we paused, and when.
    private (List<GlobalSystemMediaTransportControlsSession> Sessions, DateTime At)? _paused;
    private bool _resolving;
    private int _resolveGeneration;
    /// The volume we faded from, on which device, and the level we left it at.
    private (string Device, float Volume, float Level)? _ducked;
    private int _fadeGeneration;
    /// The last volume our own fade set — so "did you change it yourself?" can be told
    /// apart from a fade that simply hadn't finished yet.
    private float? _lastWritten;
    /// A restore still fading back up — a new dictation ducks from its target.
    private (string Device, float Volume)? _restoringTo;

    /// <param name="mode">The setting, read when a recording starts.</param>
    /// <param name="persist">Keep this faded volume (or forget it: null) so it can be put back
    /// with <see cref="RestoreAfterCrash"/> if Mindtalk quits before it could.</param>
    public MediaControl(Func<MediaMode> mode, Action<DuckedVolume?> persist)
    {
        _mode = mode;
        _persist = persist;
        var thread = new Thread(Run) { IsBackground = true, Name = "Mindtalk media" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// Recording started.
    public void Begin()
    {
        var mode = _mode();
        if (mode != MediaMode.Off) Post(() => BeginNow(mode));
    }

    /// Recording stopped (or was cancelled).
    public void End() => Post(EndNow);

    /// <summary>Quitting: the volume back at once, no fade. Waits (briefly) until it's done.</summary>
    public void RestoreNow()
    {
        using var done = new ManualResetEventSlim();
        Post(() =>
        {
            _fadeGeneration++;
            var back = _ducked is { } ducked ? (ducked.Device, ducked.Volume) : _restoringTo;
            _ducked = null;
            _restoringTo = null;
            if (back is { } target)
            {
                SetVolume(target.Device, target.Volume);
                ClearSaved();
            }
            done.Set();
        });
        done.Wait(CallLimit);
    }

    /// <summary>
    /// At launch: if Mindtalk quit while the volume was down, put it back — unless you've
    /// turned it up yourself since.
    /// </summary>
    public void RestoreAfterCrash(DuckedVolume? saved)
    {
        if (saved is null) return;
        Post(() =>
        {
            if (Volume(saved.DeviceId) is { } now && now < saved.Volume - 0.01f) SetVolume(saved.DeviceId, saved.Volume);
            _persist(null);
        });
    }

    // ---- The media thread ----------------------------------------------------------------

    private void Run()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Trace.WriteLine($"MediaControl: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
        }
        _endpoint?.Device.Dispose();
        _enumerator?.Dispose();
    }

    private void Post(Action work)
    {
        try { _work.Add(work); }
        catch (InvalidOperationException) { /* disposed */ }
    }

    private void PostAfter(TimeSpan delay, Action work) =>
        Task.Delay(delay).ContinueWith(_ => Post(work), TaskScheduler.Default);

    private void BeginNow(MediaMode mode)
    {
        var playing = Players();
        // A pause from the last dictation still settling carries over.
        var carried = _paused;
        _resolveGeneration++;
        _resolving = false;

        var paused = new List<GlobalSystemMediaTransportControlsSession>(carried?.Sessions ?? []);
        if (mode == MediaMode.Pause)
        {
            foreach (var session in PlayingSessions())
            {
                if (!Call(session.TryPauseAsync())) continue;   // can't be paused: it's faded instead
                if (!paused.Any(p => ReferenceEquals(p, session) || App(p) == App(session))) paused.Add(session);
                Trace.WriteLine($"MediaControl: paused {App(session)}");
            }
        }
        if (paused.Count > 0) _paused = (paused, carried?.At ?? DateTime.UtcNow);

        var mediaApps = paused.Select(App).ToList();
        var other = playing.Where(p => !mediaApps.Any(app => Belongs(p, app))).ToList();
        if (other.Count > 0)
        {
            Trace.WriteLine($"MediaControl: fading for {string.Join(", ", other.Select(p => p.Process).Distinct())}");
            Duck();
        }
    }

    private void EndNow()
    {
        RestoreVolume();
        if (_paused is null || _resolving) return;
        _resolving = true;
        Resolve(_resolveGeneration);
    }

    /// <summary>
    /// Plays each paused app again once its session says "paused" — the pause was ours.
    /// One that's still playing may not have settled yet (it's asked again shortly); one that
    /// stopped or closed is left be. After a few seconds we stop waiting.
    /// </summary>
    private void Resolve(int generation)
    {
        if (generation != _resolveGeneration || _paused is not { } paused) return;
        var waiting = new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var session in paused.Sessions)
        {
            switch (Status(session))
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    Call(session.TryPlayAsync());
                    Trace.WriteLine($"MediaControl: resumed {App(session)}");
                    break;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                    waiting.Add(session);
                    break;
            }
        }
        if (waiting.Count == 0 || DateTime.UtcNow >= paused.At + SettleTime)
        {
            foreach (var session in waiting) Trace.WriteLine($"MediaControl: not resumed {App(session)}: it never paused");
            _paused = null;
            _resolving = false;
            return;
        }
        _paused = (waiting, paused.At);
        PostAfter(PollInterval, () => Resolve(generation));
    }

    // ---- Volume ------------------------------------------------------------------------

    private void Duck()
    {
        if (_ducked is not null || DefaultOutputId() is not { } device || Volume(device) is not { } current) return;
        var volume = _restoringTo is { } restoring && restoring.Device == device ? restoring.Volume : current;
        _restoringTo = null;
        if (volume <= DuckLevel + 0.01f) return;
        _ducked = (device, volume, DuckLevel);
        _persist(new DuckedVolume(device, volume));
        Fade(device, from: current, to: DuckLevel, TimeSpan.FromMilliseconds(250));
    }

    private void RestoreVolume()
    {
        if (_ducked is not { } ducked) return;
        _ducked = null;
        _fadeGeneration++;
        // Changed the volume yourself meanwhile? Then it's yours. Otherwise — even mid-fade,
        // after a quick tap — it goes back to where it was.
        if (Volume(ducked.Device) is not { } now) { ClearSaved(); return; }
        var ours = _lastWritten ?? ducked.Level;
        if (Math.Abs(now - ours) >= 0.03f) { ClearSaved(); return; }
        _restoringTo = (ducked.Device, ducked.Volume);
        Fade(ducked.Device, from: now, to: ducked.Volume, TimeSpan.FromMilliseconds(400), () =>
        {
            _restoringTo = null;
            ClearSaved();
        });
    }

    /// The crash-restore note goes only once the volume is really back.
    private void ClearSaved()
    {
        _persist(null);
        _lastWritten = null;
    }

    /// Ten smoothstep steps; a newer fade (or a restore) cancels this one.
    private void Fade(string device, float from, float to, TimeSpan duration, Action? done = null)
    {
        const int steps = 10;
        var generation = ++_fadeGeneration;
        void Step(int i)
        {
            if (generation != _fadeGeneration) return;
            var t = i / (float)steps;
            var value = from + (to - from) * t * t * (3 - 2 * t);
            SetVolume(device, value);
            _lastWritten = value;
            if (i < steps) PostAfter(duration / steps, () => Step(i + 1));
            else done?.Invoke();
        }
        Step(1);
    }

    private string? DefaultOutputId()
    {
        _enumerator ??= new MMDeviceEnumerator();
        if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return null;
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        return device.ID;
    }

    /// The endpoint by ID, kept while we keep fading the same one.
    private MMDevice? Endpoint(string id)
    {
        if (_endpoint is { } cached && cached.Id == id) return cached.Device;
        _endpoint?.Device.Dispose();
        _endpoint = null;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var device = _enumerator.GetDevice(id);
            if (device.State != DeviceState.Active) { device.Dispose(); return null; }
            _endpoint = (id, device);
            return device;
        }
        catch { return null; }   // unplugged
    }

    private float? Volume(string device)
    {
        try { return Endpoint(device)?.AudioEndpointVolume.MasterVolumeLevelScalar; }
        catch { return null; }
    }

    private void SetVolume(string device, float volume)
    {
        try
        {
            if (Endpoint(device) is { } endpoint) endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
        }
        catch (Exception ex) { Trace.WriteLine($"MediaControl: couldn't set the volume ({ex.GetType().Name})"); }
    }

    // ---- Who is playing ------------------------------------------------------------------

    private sealed record Player(int ProcessId, string Process, string? Package);

    /// Other apps with sound running to the default output right now.
    private static List<Player> Players() =>
        AudioSessions().Where(s => s.Active && s.Player.ProcessId != Environment.ProcessId).Select(s => s.Player).ToList();

    private static List<(Player Player, bool Active)> AudioSessions()
    {
        var result = new List<(Player, bool)>();
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return result;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            // Windows' own notification sounds aren't something to fade for.
            if (session.IsSystemSoundsSession) continue;
            var pid = (int)session.GetProcessID;
            result.Add((new Player(pid, ProcessName(pid), PackageFamily(pid)),
                        session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive));
        }
        return result;
    }

    private GlobalSystemMediaTransportControlsSessionManager? SessionManager()
    {
        if (_sessionManagerTried) return _sessionManager;
        _sessionManagerTried = true;
        _sessionManager = Get(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        return _sessionManager;
    }

    private List<GlobalSystemMediaTransportControlsSession> PlayingSessions()
    {
        var result = new List<GlobalSystemMediaTransportControlsSession>();
        if (SessionManager() is not { } manager) return result;
        foreach (var session in manager.GetSessions())
            if (Status(session) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) result.Add(session);
        return result;
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? Status(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo().PlaybackStatus; }
        catch { return null; }   // the app has gone
    }

    private static string App(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId; }
        catch { return ""; }
    }

    /// <summary>
    /// Is this sound from the app behind a media session? A store app is known by its package;
    /// a desktop app's session is named after its .exe ("Spotify.exe") or its own app ID
    /// ("Chrome", "MSEdge", Firefox's install hash).
    /// </summary>
    private static bool Belongs(Player player, string app)
    {
        if (app.Length == 0) return false;
        var bang = app.IndexOf('!');
        if (bang > 0) return player.Package is { } package && app[..bang].Equals(package, StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(app);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var first = name.Split('.')[0];
        var alias = first.ToLowerInvariant() switch
        {
            "308046b0af4a39cb" or "firefox" => "firefox",
            _ => first,
        };
        return player.Process.Equals(name, StringComparison.OrdinalIgnoreCase)
            || player.Process.Equals(alias, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProcessName(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.ProcessName; }
        catch { return "?"; }
    }

    private static string? PackageFamily(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[256];
            var length = (uint)buffer.Length;
            return Native.GetPackageFamilyName(handle, ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
        }
        finally { Native.CloseHandle(handle); }
    }

    /// WinRT calls, waited for on this thread — but never for long.
    private static bool Call(Windows.Foundation.IAsyncOperation<bool> operation) => Get(operation);

    private static T? Get<T>(Windows.Foundation.IAsyncOperation<T> operation)
    {
        try
        {
            var task = operation.AsTask();
            return task.Wait(CallLimit) ? task.Result : default;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MediaControl: media session call failed ({ex.GetType().Name})");
            return default;
        }
    }

    // ---- For the self-test: look, don't touch ---------------------------------------------

    /// <summary>The media sessions and their state, the sound sessions on the default output,
    /// and its volume. Changes nothing.</summary>
    public static (IReadOnlyList<MediaSessionInfo> Media, IReadOnlyList<AudioPlayerInfo> Audio, float? Volume) Inspect()
    {
        var media = new List<MediaSessionInfo>();
        try
        {
            var task = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            if (task.Wait(CallLimit))
                foreach (var session in task.Result.GetSessions())
                    media.Add(new MediaSessionInfo(App(session), Status(session)?.ToString() ?? "?"));
        }
        catch (Exception ex) { media.Add(new MediaSessionInfo("(unavailable)", ex.GetType().Name)); }

        var audio = AudioSessions().Select(s => new AudioPlayerInfo(s.Player.ProcessId, s.Player.Process, s.Active)).ToList();

        float? volume = null;
        using (var enumerator = new MMDeviceEnumerator())
        {
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                volume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
        }
        return (media, audio, volume);
    }

    public void Dispose() => _work.CompleteAdding();
}
