using Microsoft.UI.Xaml.Controls;

namespace Mindtalk.App;

/// <summary>The introduction in a window of its own (first launch). Placeholder until it's built.</summary>
public sealed class OnboardingWindow(App app)
{
    public event Action? Finished;
    public bool IsOpen { get; private set; }

    public void Activate()
    {
        IsOpen = false;
        app.ShowMain();
        Finished?.Invoke();
    }
}

/// <summary>The five steps of the introduction, for a window of its own or over the main window.</summary>
public sealed partial class OnboardingView : Grid
{
    public OnboardingView(App app, Action finish) { }
}
