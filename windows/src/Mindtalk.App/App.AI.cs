using Mindtalk.App.AI;
using Mindtalk.Core.AI;
using Mindtalk.Core.AI.Mcp;
using Mindtalk.Writers;

namespace Mindtalk.App;

/// <summary>
/// The AI part: who writes summaries, titles and answers (Gemma 4, Ollama, LM Studio, Windows AI),
/// the summaries and Ask, and AI apps (MCP) with the command line — plus mindtalk:// links, which
/// open a meeting at a moment or a thought.
/// </summary>
public partial class App
{
    /// The pipe AI apps' helper talks to, and the access log (also <see cref="AiApps"/>).
    public AiAppsService? AiAppsHost { get; private set; }

    /// <summary>
    /// A mindtalk:// link was opened (from an AI app's answer, the command line…). The main window
    /// is already showing Meetings or Thoughts; the meetings UI opens the meeting at
    /// <see cref="MindtalkLink.Time"/>, the thoughts UI scrolls to the thought. Raised on the UI thread.
    /// </summary>
    public event Action<MindtalkLink>? LinkOpened;

    partial void StartAIServices()
    {
        var polisher = new OnDevicePolisher();
        var windowsAi = new WindowsAiWriter();
        var writers = new WriterService(Settings, Settings.Save, () => WindowsAiWriter.State(polisher), () => windowsAi, Demo.ModelsDirectory);
        Writers = writers;
        // Vocabulary.Reapply: names and terms fixed in summaries and answers too (without counting them twice).
        Summarizer = new MeetingSummarizer(() => writers.Model, () => writers.CurrentName, text => Vocabulary.Reapply(text));
        Asker = new MeetingAsk(() => writers.Model);

#if DEBUG
        // Demo mode, for trying AI apps and the command line against the demo meetings.
        if (Demo.On && Demo.Has("--ai-apps-on")) Settings.McpEnabled = true;
#endif
        AiAppsHost = new AiAppsService(this, Demo.File("ai-apps-log.json"));
        AiApps = AiAppsHost;
        AiAppsHost.LinkReceived += uri => _ui.TryEnqueue(() => OpenLink(uri));
        AiAppsService.RegisterLinks();

        // After launch: the meeting engine is set up by then, and the window can open.
        _ui.TryEnqueue(() =>
        {
            if (MeetingService is { } meetings) AiAppsHost.Watch(meetings);
            var link = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.StartsWith(MindtalkLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));
            if (link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri)) OpenLink(uri);
        });
    }

    /// <summary>
    /// Opens a mindtalk:// link: the main window on Meetings or Thoughts, then <see cref="LinkOpened"/>
    /// for the page to show the exact moment.
    /// </summary>
    public void OpenLink(Uri uri)
    {
        if (MindtalkLink.Parse(uri) is not { } link) return;
        ShowMain(link.Kind == MindtalkLink.Kinds.Meeting ? Page.Meetings : Page.Thoughts);
        LinkOpened?.Invoke(link);
    }

    /// Started by a mindtalk:// link while Mindtalk is already running: the running one opens it.
    private static void ForwardLink(string[] arguments)
    {
        var link = arguments.Skip(1).FirstOrDefault(a => a.StartsWith(MindtalkLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (link is not null) AiAppsService.Forward(link);
    }
}
