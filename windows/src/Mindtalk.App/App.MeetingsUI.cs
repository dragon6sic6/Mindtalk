using Microsoft.UI.Dispatching;
using Mindtalk.App.Meetings;
using Windows.System;

namespace Mindtalk.App;

/// <summary>
/// The meetings part of the windows: the Meetings page, a meeting, the live meeting, the
/// floating panel and the call box — and the keys: Ctrl+Alt+M from any app, Ctrl+Shift+P
/// and Ctrl+Shift+1/2/3 while recording, Ctrl+K to ask and Ctrl+O to import in the window.
/// </summary>
public partial class App
{
    /// The live meeting, the floating windows and going to a meeting.
    internal MeetingHub MeetingHub { get; private set; } = null!;

    /// The main window, if it has been opened.
    internal MainWindow? MainWindowIfOpen => _main;

    /// The main window, opened and in front.
    internal MainWindow OpenMainWindow()
    {
        ShowMain();
        return _main!;
    }

    partial void RegisterMeetingsUI()
    {
        MeetingHub = new MeetingHub(this);
        MainWindow.MeetingsFactory = (app, main) => new MeetingsPage(app, main, app.MeetingHub);
        // Home and the tray panel open meetings (and the live one) through here.
        OpenMeeting = (id, at) => MeetingHub.OpenMeeting(id, at);
        // mindtalk://meeting/<id>?t=… from an AI app's answer or the command line: that moment.
        LinkOpened += link =>
        {
            if (link.Kind == Core.AI.Mcp.MindtalkLink.Kinds.Meeting) MeetingHub.OpenMeeting(link.Id, link.Time);
        };
        ShortcutsChanged += () =>
        {
            MeetingHub.RegisterMeetingKey();
            RegisterPasteLastKey();
        };
        var previous = MainWindow.ExtraKeys;
        MainWindow.ExtraKeys = add =>
        {
            previous?.Invoke(add);
            add(VirtualKey.K, VirtualKeyModifiers.Control, () => OnMeetingsPage(page => page.FocusAsk()));
            add(VirtualKey.O, VirtualKeyModifiers.Control, () => OnMeetingsPage(page => page.ChooseFile()));
        };
        // The services start after the windows are registered: hook up once they're there.
        _ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            ConnectMeetings();
            Changed += ConnectMeetings;
#if DEBUG
            DemoMeetings.Start(this);
#endif
        });
    }

    private void ConnectMeetings()
    {
        MeetingHub.Attach();
        MeetingHub.RegisterMeetingKey();
        RegisterPasteLastKey();
    }

    private Core.Input.Shortcut? _pasteLastKey;
    private int _pasteLastKeyId;

    /// Paste last dictation (Ctrl+Alt+V) from any app, or what Settings changed it to.
    private void RegisterPasteLastKey()
    {
        var wanted = Settings.PasteLastShortcut;
        if (Equals(wanted, _pasteLastKey) && (_pasteLastKeyId != 0 || wanted is null)) return;
        if (_pasteLastKeyId != 0) Shortcuts.Unregister(_pasteLastKeyId);
        _pasteLastKey = wanted;
        _pasteLastKeyId = wanted is null ? 0 : Shortcuts.Register(wanted, () => _ui.TryEnqueue(() => Dictation.PasteLast()));
    }

    /// The Meetings page in front (from a meeting or another page), then <paramref name="action"/> on it.
    private void OnMeetingsPage(Action<MeetingsPage> action)
    {
        if (_main is not { } main) return;
        if (main.CurrentPage != Page.Meetings || main.Pushed is not null) main.ShowPage(Page.Meetings);
        _ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (MeetingsPage.Shown is { } page) action(page); });
    }
}
