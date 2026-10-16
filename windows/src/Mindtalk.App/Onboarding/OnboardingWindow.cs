using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;

namespace Mindtalk.App;

/// <summary>
/// The introduction in a window of its own (first launch): 760 × 640 of steps below the
/// title bar, not resizable, centred, the paper and its light reaching up under the caption
/// buttons. Closing it — or finishing — means it's done.
/// </summary>
public sealed class OnboardingWindow(App app)
{
    private Window? _window;
    private Grid? _root;
    private OnboardingView? _view;
    private bool _done;

    public event Action? Finished;
    public bool IsOpen { get; private set; }

    public void Activate()
    {
        if (_done) return;
        if (_window is null) Create();
        IsOpen = true;
        var window = _window!;
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        window.Activate();
    }

    private void Create()
    {
        var titleBar = new Grid { Height = OnboardingView.TitleBar, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _view = new OnboardingView(app, () => _window?.Close());
        _root = new Grid { Children = { _view, titleBar } };
        UI.Bind(_root, Panel.BackgroundProperty, "PaperBrush");
        var window = new Window { Content = _root };
        _window = window;
        Chrome.Prepare(window, titleBar);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        window.AppWindow.SetPresenter(presenter);
        // The title bar is part of the content here, so the frame is only the thin border.
        var size = Chrome.Scaled(window, 760, 640 + OnboardingView.TitleBar);
        var outer = window.AppWindow.Size;
        var client = window.AppWindow.ClientSize;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width + outer.Width - client.Width, size.Height + outer.Height - client.Height));
        Chrome.Center(window);

        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Picking a key ends when you turn to another app — left waiting, it would
                // swallow a key there and take the next modifier as your key.
                app.StopPickingKey();
            }
            else
            {
                // Windows doesn't announce a changed privacy setting: look when you come back.
                app.RefreshPermissions();
                _view?.Refresh();
            }
        };
        window.Closed += (_, _) => Done();
        app.ThemeChanged += Repaint;
    }

    private void Repaint()
    {
        if (_window is null || _root is null) return;
        Chrome.Paint(_window);
        UI.Bind(_root, Panel.BackgroundProperty, "PaperBrush");
    }

    /// Closing the introduction — however it's closed — means it's done. Said once.
    private void Done()
    {
        if (_done) return;
        _done = true;
        IsOpen = false;
        app.ThemeChanged -= Repaint;
        app.StopPickingKey();
        _window = null;
        Finished?.Invoke();
    }
}
