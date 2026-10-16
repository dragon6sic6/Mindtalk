using Mindtalk.App.Controls;
using Mindtalk.Core;

namespace Mindtalk.App.Pages;

public sealed partial class RecentPage : PageView
{
    public RecentPage(App app, MainWindow window) : base(app)
    {
        Add(UI.PageHeader(Loc.T("Senaste", "Recent")), 0);
    }
}
