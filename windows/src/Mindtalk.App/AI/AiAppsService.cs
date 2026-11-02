using System.IO.Pipes;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Mindtalk.Core;
using Mindtalk.Core.AI;
using Mindtalk.Core.AI.Mcp;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;

namespace Mindtalk.App.AI;

/// <summary>
/// AI apps (MCP) and the command line. The app hosts a named pipe that only your user account
/// can open; mindtalk-mcp.exe, which AI apps start, asks it. Off until you turn it on — while
/// off, the pipe only says so. Read-only; dictations, audio and voiceprints are never shared;
/// a meeting can't be read while it's recorded or in the two minutes after; every read is logged.
/// The same pipe lets a second Mindtalk started by a mindtalk:// link hand the link over.
/// </summary>
public sealed class AiAppsService : IAiApps, IAiAppsData, IDisposable
{
    private readonly App _app;
    private readonly AccessLog _log;
    private readonly AiAppsRouter _router;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Guid, DateTime> _ended = [];
    private readonly object _gate = new();
    private Guid? _live;

    public AiAppsService(App app, string? logPath)
    {
        _app = app;
        _log = new AccessLog(logPath ?? AccessLog.DefaultPath);
        _log.Changed += () => Changed?.Invoke();
        _router = new AiAppsRouter(this, _log, () => Enabled);
        PipeName = AiPipe.NameFor(AppContext.BaseDirectory);
        for (var i = 0; i < 4; i++) _ = ServeAsync(_stop.Token);
    }

    public string PipeName { get; }

    /// A mindtalk:// link from a second Mindtalk (started by the link): open it here.
    public event Action<Uri>? LinkReceived;
    public event Action? Changed;

    // ---- IAiApps ------------------------------------------------------------------------

    public bool Enabled => _app.Settings.McpEnabled;

    public void SetEnabled(bool enabled)
    {
        if (_app.Settings.McpEnabled == enabled) return;
        _app.Settings.McpEnabled = enabled;
        _app.Settings.Save();
        Changed?.Invoke();
    }

    public IReadOnlyList<AccessLogEntry> Log => _log.Entries;
    public void ClearLog() => _log.Clear();

    public string HelperPath => Path.Combine(AppContext.BaseDirectory, "mindtalk-mcp.exe");

    public IReadOnlyList<AiAppSetup> Setups
    {
        get
        {
            var path = HelperPath;
            var json = $$"""
                {
                  "mcpServers": {
                    "mindtalk": {
                      "command": "{{path.Replace("\\", "\\\\")}}"
                    }
                  }
                }
                """;
            return
            [
                new("Claude", Loc.T(
                    "I Claude: Inställningar → Utvecklare → Redigera konfiguration. Lägg till det här i claude_desktop_config.json och starta om Claude.",
                    "In Claude: Settings → Developer → Edit Config. Add this to claude_desktop_config.json and restart Claude."), json),
                new("Claude Code", Loc.T(
                    "Kör det här i en terminal. Sedan kan Claude Code läsa dina möten i alla projekt.",
                    "Run this in a terminal. Claude Code can then read your meetings in every project."),
                    $"claude mcp add --scope user mindtalk -- \"{path}\""),
                new("ChatGPT (Codex)", Loc.T(
                    "Lägg till det här i %USERPROFILE%\\.codex\\config.toml och starta om Codex.",
                    "Add this to %USERPROFILE%\\.codex\\config.toml and restart Codex."),
                    $"[mcp_servers.mindtalk]\ncommand = '{path}'"),
                new("Cursor", Loc.T(
                    "Lägg till det här i %USERPROFILE%\\.cursor\\mcp.json (eller under Cursor Settings → MCP) och starta om Cursor.",
                    "Add this to %USERPROFILE%\\.cursor\\mcp.json (or under Cursor Settings → MCP) and restart Cursor."), json),
                new(Loc.T("Andra MCP-appar", "Other MCP apps"), Loc.T(
                    "Starta det här programmet utan argument; det talar MCP över stdio. Samma program är kommandoraden: mindtalk-mcp meetings, search, thoughts och tasks.",
                    "Start this program with no arguments; it speaks MCP over stdio. The same program is the command line: mindtalk-mcp meetings, search, thoughts and tasks."),
                    path),
            ];
        }
    }

    // ---- IAiAppsData: what the router may read ---------------------------------------

    IReadOnlyList<Meeting> IAiAppsData.Meetings => _app.Meetings.All;
    IReadOnlyList<Thought> IAiAppsData.Thoughts => _app.Thoughts.All;

    Guid? IAiAppsData.RecordingMeetingId
    {
        get { lock (_gate) return _live; }
    }

    DateTime? IAiAppsData.EndedAt(Guid meetingId)
    {
        lock (_gate) return _ended.TryGetValue(meetingId, out var t) ? t : null;
    }

    /// <summary>Follows the meeting being recorded, so it can't be read until two minutes after it ends.</summary>
    public void Watch(IMeetingService meetings)
    {
        void Update()
        {
            var current = meetings.Current;
            var recording = current is { State: MeetingState.Recording or MeetingState.Paused } ? current.Meeting.Id : (Guid?)null;
            lock (_gate)
            {
                if (_live is { } was && was != recording) _ended[was] = DateTime.Now;
                _live = recording;
            }
        }
        meetings.Changed += Update;
        Update();
    }

    // ---- The pipe ---------------------------------------------------------------------

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // CurrentUserOnly: the pipe's ACL lets only this user account in.
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                 PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await AiPipe.ReadAsync(pipe, timeout.Token);
                if (request is not null)
                {
                    var reply = Answer(request);
                    await AiPipe.WriteAsync(pipe, reply, timeout.Token);
                    pipe.WaitForPipeDrain();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100, CancellationToken.None); }
            catch (UnauthorizedAccessException) { await Task.Delay(1000, CancellationToken.None); }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
    }

    private JsonObject Answer(JsonObject request)
    {
        if ((string?)request["open"] is { } link)
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && MindtalkLink.Parse(uri) is not null) LinkReceived?.Invoke(uri);
            return new JsonObject { ["ok"] = true };
        }
        return _router.Handle(request);
    }

    /// <summary>Hands a link to the Mindtalk already running (from a second one, started by the link).</summary>
    public static bool Forward(string link)
    {
        var request = new JsonObject { ["open"] = link };
        try
        {
            return Task.Run(() => AiPipe.SendAsync(AiPipe.NameFor(AppContext.BaseDirectory), request, TimeSpan.FromSeconds(2))).Result is not null;
        }
        catch (AggregateException) { return false; }
    }

    // ---- mindtalk:// ------------------------------------------------------------------

    /// <summary>
    /// Registers mindtalk:// for this user (HKCU\Software\Classes\mindtalk), pointing at this
    /// Mindtalk.exe. A development build only takes it over when no working one is registered.
    /// </summary>
    public static void RegisterLinks()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            using var existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\mindtalk\shell\open\command");
            var current = existing?.GetValue(null) as string;
            var wanted = $"\"{exe}\" \"%1\"";
            if (current == wanted) return;
#if DEBUG
            var registeredExe = current?.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (registeredExe is not null && File.Exists(registeredExe)) return;
#endif
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\mindtalk");
            key.SetValue(null, "URL:Mindtalk");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, wanted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    /// Uninstalling: mindtalk:// goes too, unless another Mindtalk.exe has taken it over.
    public static void UnregisterLinks()
    {
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\mindtalk\shell\open\command"))
            {
                var current = existing?.GetValue(null) as string;
                if (current is null || Environment.ProcessPath is not { } exe || !current.Contains(exe, StringComparison.OrdinalIgnoreCase)) return;
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\mindtalk", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
