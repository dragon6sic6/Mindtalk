using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.App.Pages;
using Mindtalk.Core;
using Windows.System;

namespace Mindtalk.App;

/// <summary>
/// The main window: a floating tab bar and four pages — Dictation (what's missing,
/// statistics, how to use it, a box to try it in), Recent, Vocabulary and Settings.
/// Closing it hides it; Mindtalk lives on in the tray. (The Mac's MainView.)
/// </summary>
public sealed class MainWindow : Window
{
    private readonly App _app;
    private readonly Grid _root = new();
    private readonly Grid _pageHost = new();
    private readonly Grid _overlay = new();
    private readonly WarmGlow _glow = new(0.32) { Height = 300, VerticalAlignment = VerticalAlignment.Top };
    private FloatingTabBar _tabs = null!;
    private PageView? _current;
    private Page _page = Page.Dictation;
    private OnboardingView? _introduction;

    public MainWindow(App app)
    {
        _app = app;
        Content = _root;
        Build();
        Chrome.Prepare(this, _titleBar);

        var presenter = OverlappedPresenter.Create();
        presenter.PreferredMinimumWidth = Chrome.Scaled(this, 820, 580).Width;
        presenter.PreferredMinimumHeight = Chrome.Scaled(this, 820, 580).Height;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(Chrome.Scaled(this, 960, 680));
        Chrome.Center(this);

        AppWindow.Closing += (_, e) =>
        {
            // Closing hides: Mindtalk lives on in the tray (or, if you like, in the taskbar).
            e.Cancel = true;
            _app.StopPickingKey();
            if (_app.Settings.ShowInTaskbar && AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
            else { CloseIntroduction(); AppWindow.Hide(); }
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Picking a shortcut ends when you turn to another app — left waiting, it would
                // swallow a key there and take the next modifier as your shortcut.
                _app.StopPickingKey();
            }
            else _app.RefreshPermissions();
        };
        _app.ThemeChanged += () => { Chrome.Paint(this); Rebuild(); };
    }

    public bool IsOpen => AppWindow.IsVisible;

    private readonly Grid _titleBar = new() { Height = 32, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

    private void Build()
    {
        _root.Children.Clear();
        _root.RequestedTheme = UI.Theme;
        UI.Bind(_root, Panel.BackgroundProperty, "PaperBrush");

        // The page gets the whole window; a tab bar floats at the leading edge.
        var main = new Grid { Padding = new Thickness(72, 0, 0, 0) };
        main.Children.Add(_glow);
        main.Children.Add(_pageHost);
        _root.Children.Add(main);
        _tabs = new FloatingTabBar(_app, _page, ShowPage) { Margin = new Thickness(16, 0, 0, 0) };
        _root.Children.Add(_tabs);
        _root.Children.Add(_titleBar);
        _root.Children.Add(_overlay);
        Keys();
        ShowPage(_page, animated: false);
    }

    private void Rebuild()
    {
        _glow.Intensity = 0.32;
        _current = null;
        Build();
    }

    /// Ctrl+1/2/3 for the pages, Ctrl+, for Settings — as the Mac's View menu.
    private void Keys()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
            _root.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control, () => ShowPage(Page.Dictation));
        Add(VirtualKey.Number2, VirtualKeyModifiers.Control, () => ShowPage(Page.Recent));
        Add(VirtualKey.Number3, VirtualKeyModifiers.Control, () => ShowPage(Page.Vocabulary));
        Add((VirtualKey)0xBC /* comma */, VirtualKeyModifiers.Control, () => ShowPage(Page.Settings));
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (_overlay.Children.Count > 0 && _introduction is null) CloseSheet();
            else if (_introduction is not null) CloseIntroduction();
        });
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    public void ShowAndActivate(Page? page)
    {
        if (page is { } p) ShowPage(p);
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        // Nothing focused on open — otherwise the "Try it" box grabs focus.
        _root.Focus(FocusState.Programmatic);
    }

    public void ShowPage(Page page) => ShowPage(page, animated: true);

    private void ShowPage(Page page, bool animated)
    {
        if (page == _page && _current is not null) return;
        _page = page;
        _tabs.Page = page;
        _app.StopPickingKey();
        PageView view = page switch
        {
            Page.Dictation => new DictationPage(_app),
            Page.Recent => new RecentPage(_app, this),
            Page.Vocabulary => new VocabularyPage(_app),
            _ => new SettingsPage(_app, this),
        };
        _pageHost.Children.Clear();
        _pageHost.Children.Add(view);
        _current = view;
        if (animated) Motion.PageIn(view);
    }

    /// Settings, scrolled to "About Mindtalk" (credits and licences).
    public void ScrollToAbout()
    {
        ShowPage(Page.Settings);
        if (_current is SettingsPage settings) settings.ScrollToAbout();
    }

    // ---- The introduction, over the whole window -------------------------------------

    /// The introduction again, inside this window — with a way out in the corner.
    public void ShowIntroduction()
    {
        CloseSheet();
        _introduction = new OnboardingView(_app, CloseIntroduction);
        UI.Bind(_introduction, Panel.BackgroundProperty, "PaperBrush");
        var close = UI.PlainButton(CircleIcon(Icons.XMark), CloseIntroduction, Loc.T("Stäng introduktionen", "Close the introduction"));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(16, 40, 16, 16);
        var layer = new Grid();
        layer.Children.Add(_introduction);
        layer.Children.Add(close);
        _overlay.Children.Add(layer);
        Motion.PageIn(layer);
    }

    private static Grid CircleIcon(string glyph)
    {
        var circle = new Grid { Width = 30, Height = 30 };
        var disc = new Microsoft.UI.Xaml.Shapes.Ellipse();
        UI.Bind(disc, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "ChipBrush");
        circle.Children.Add(disc);
        var icon = UI.Icon(glyph, 12, "MutedBrush");
        circle.Children.Add(icon);
        return circle;
    }

    private void CloseIntroduction()
    {
        if (_introduction is null) return;
        _app.StopPickingKey();
        _app.Settings.DidOnboard = true;
        _app.SaveSettings();
        _introduction = null;
        _overlay.Children.Clear();
    }

    // ---- Sheets ---------------------------------------------------------------------

    /// A sheet over the window (the Mac's .sheet): the page dims, the card comes forward.
    public void ShowSheet(FrameworkElement content)
    {
        _overlay.Children.Clear();
        var dim = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0)) };
        dim.Tapped += (_, e) => { if (e.OriginalSource == dim) CloseSheet(); };
        var card = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        UI.Bind(card, Border.BackgroundProperty, "PaperBrush");
        UI.Bind(card, Border.BorderBrushProperty, "FieldStrokeBrush");
        dim.Children.Add(SoftShadow.Around(card, 12, 30, 12, 0.25));
        _overlay.Children.Add(dim);
        Motion.PageIn(card);
    }

    public void CloseSheet()
    {
        if (_introduction is not null) return;
        foreach (var child in _overlay.Children.OfType<FrameworkElement>()) (child as IDisposable)?.Dispose();
        _overlay.Children.Clear();
        SheetClosed?.Invoke();
        SheetClosed = null;
    }

    /// Called once when the sheet that's open now closes.
    public event Action? SheetClosed;
}
