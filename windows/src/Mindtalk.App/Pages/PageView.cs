using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;

namespace Mindtalk.App.Pages;

/// <summary>
/// A page of the main window: a centred column (so a maximized window stays balanced),
/// generous padding, scrolls. Built once; <see cref="Refresh"/> brings it up to date
/// whenever the app changes.
/// </summary>
public abstract partial class PageView : Grid
{
    /// The page column: comfortable to read, a little roomier on big windows.
    private const double ReadingWidth = 720, WideReadingWidth = 860;

    protected PageView(App app)
    {
        App = app;
        Column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var frame = new Grid
        {
            Padding = new Thickness(48, 44, 48, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = ReadingWidth + 96,
        };
        frame.Children.Add(Column);
        Scroll = new ScrollViewer { Content = frame, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(Scroll);
        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width > 1200 ? WideReadingWidth : ReadingWidth;
            frame.MaxWidth = width + 96;
            frame.Width = Math.Min(width + 96, e.NewSize.Width);
        };
        Loaded += (_, _) => { App.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => App.Changed -= OnChanged;
    }

    protected App App { get; }
    protected StackPanel Column { get; }
    public ScrollViewer Scroll { get; }

    private void OnChanged() => Refresh();

    /// Brings what the page shows up to date.
    public virtual void Refresh() { }

    /// Adds elements to the column, rising into place one after another.
    protected void Add(UIElement element, int stagger, double spacingBefore = 0)
    {
        if (spacingBefore > 0 && element is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + spacingBefore, fe.Margin.Right, fe.Margin.Bottom);
        Column.Children.Add(Motion.Stagger(element, stagger));
    }
}
