using Mindtalk.App.Settings;
using Mindtalk.Core;
using Mindtalk.Core.Input;

namespace Mindtalk.App;

/// <summary>
/// Settings, in five sections (General, Language, Dictation, Meetings, AI apps), and picking
/// the Say a thought keys.
/// </summary>
public partial class App
{
    partial void RegisterSettingsUI()
    {
        MainWindow.SettingsFactory = SettingsPages.Make;
#if DEBUG
        // Screenshots: --page settings --section meetings
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--section");
        if (!Demo.On || at < 0 || at + 1 >= args.Length || !Enum.TryParse<SettingsSection>(args[at + 1], true, out var section)) return;
        var first = true;
        MainWindow.SettingsFactory = (app, window, shown) =>
        {
            if (first && shown != section) { first = false; window.DispatcherQueue.TryEnqueue(() => window.ShowSection(section)); }
            first = false;
            return SettingsPages.Make(app, window, shown);
        };
#endif
    }

    /// Start a meeting or Paste last dictation got a new shortcut (or was turned off) in
    /// Settings: whoever registered them with <see cref="Shortcuts"/> registers them again.
    public event Action? ShortcutsChanged;

    internal void RaiseShortcutsChanged() => ShortcutsChanged?.Invoke();
}
