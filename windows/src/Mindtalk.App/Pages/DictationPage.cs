using Mindtalk.App.Controls;
using Mindtalk.Core;

namespace Mindtalk.App.Pages;

public sealed partial class DictationPage : PageView
{
    public DictationPage(App app) : base(app)
    {
        Add(UI.PageHeader(Loc.T("Diktering", "Dictation")), 0);
    }
}
