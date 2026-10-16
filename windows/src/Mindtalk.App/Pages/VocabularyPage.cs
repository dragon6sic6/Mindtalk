using Mindtalk.App.Controls;
using Mindtalk.Core;

namespace Mindtalk.App.Pages;

public sealed partial class VocabularyPage : PageView
{
    public VocabularyPage(App app) : base(app)
    {
        Add(UI.PageHeader(Loc.T("Ordlista", "Vocabulary")), 0);
    }
}
