using Mindtalk.App.Controls;
using Mindtalk.Core;

namespace Mindtalk.App.Pages;

public sealed partial class SettingsPage : PageView
{
    public SettingsPage(App app, MainWindow window) : base(app)
    {
        Add(UI.PageHeader(Loc.T("Inställningar", "Settings")), 0);
    }
    public void ScrollToAbout() { }
}
