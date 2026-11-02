using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;
using Mindtalk.Core.Voices;

namespace Mindtalk.App;

/// <summary>
/// 2.x's data: thoughts, meetings, remembered voices — and the services that record,
/// summarize and answer. Each store raises Changed; the windows refresh on the UI thread.
/// </summary>
public partial class App
{
    public ThoughtStore Thoughts { get; private set; } = null!;
    public MeetingStore Meetings { get; private set; } = null!;
    public VoiceStore Voices { get; private set; } = null!;

    /// Records and imports meetings (set up by <see cref="StartMeetingServices"/>).
    public IMeetingService? MeetingService { get; set; }
    public IMeetingSummarizer? Summarizer { get; set; }
    public IMeetingAsk? Asker { get; set; }
    /// Who writes summaries and answers (Gemma, Ollama, LM Studio…).
    public Core.AI.IWriterService? Writers { get; set; }
    /// AI apps (MCP) and the command line.
    public Core.AI.IAiApps? AiApps { get; set; }
    /// App-wide shortcuts (Ctrl+Alt+M…). Their actions run off the UI thread: post with UI.TryEnqueue.
    public Platform.GlobalShortcuts Shortcuts { get; } = new();

    public bool IsRecordingMeeting => MeetingService?.Current is { State: MeetingState.Recording or MeetingState.Paused };

    private void CreateStores()
    {
        RegisterHomeUI();
        RegisterMeetingsUI();
        RegisterSettingsUI();
        Thoughts = new ThoughtStore(Demo.File("thoughts.json"));
        Meetings = new MeetingStore(Demo.File("Meetings"))
        {
            Trash = folder => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin),
        };
        Voices = new VoiceStore(Demo.File("voices.json"));
        Thoughts.Changed += () => _ui.TryEnqueue(Refresh);
        Meetings.Changed += () => _ui.TryEnqueue(Refresh);
        Voices.Changed += () => _ui.TryEnqueue(Refresh);
        StartAIServices();
        StartMeetingServices();
    }

    /// The meeting engine and the writers. Filled in by the meetings and AI parts.
    partial void StartMeetingServices();

    /// The writers (Summarizer, Asker) and AI apps (MCP). Filled in by the AI part.
    partial void StartAIServices();

    // Each part sets its pages on MainWindow's factories (MainWindow.HomeFactory…).
    partial void RegisterHomeUI();
    partial void RegisterMeetingsUI();
    partial void RegisterSettingsUI();
}
