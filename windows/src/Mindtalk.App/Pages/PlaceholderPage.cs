using Mindtalk.App.Controls;

namespace Mindtalk.App.Pages;

/// <summary>A page that isn't built yet: just its title.</summary>
public sealed partial class PlaceholderPage : PageView
{
    public PlaceholderPage(App app, string title) : base(app)
    {
        Add(UI.PageHeader(title), 0);
    }
}
