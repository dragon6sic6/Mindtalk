using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mindtalk.App.Controls;
using Mindtalk.App.Onboarding;
using Mindtalk.Core;
using Mindtalk.Core.Speech;
using Windows.System;

namespace Mindtalk.App;

/// <summary>
/// The first run (the Mac's OnboardingView). Five steps — welcome → language (starts the
/// download) → permissions (while it downloads) → your key → try it for real — on a slowly
/// moving warm backdrop. Everything eases in, the logo speaks, the headline types itself, and
/// the first dictation is celebrated. With animation effects off, it's all calm and still.
/// </summary>
public sealed partial class OnboardingView : Grid
{
    private const double StepShift = 40;
    /// The window's caption strip, kept clear above the steps (the Mac's title bar).
    internal const double TitleBar = 32;

    private readonly App _app;
    private readonly Action _finish;
    private readonly HashSet<SpeechModel> _chosen;
    private readonly WarmGlow _glow;
    private Grid _stepHost = new();
    private readonly DispatcherQueueTimerHolder _ticker = new();
    /// Holds the focus at first, so no button opens with a focus ring (as on the Mac).
    private readonly ContentControl _focus = new() { IsTabStop = true, Width = 0, Height = 0, UseSystemFocusVisuals = false };
    private Step _step = FirstStep;
    private StepView? _current;
    private ProgressSegments _segments = null!;
    private Button _back = null!;
    private DownloadStatus _downloads = null!;
    private GlowButton _primary = null!;

    public OnboardingView(App app, Action finish)
    {
        _app = app;
        _finish = finish;
        _chosen = [app.Engine];
        _glow = new WarmGlow(_step == Step.Welcome ? 1 : 0.45);
        Build(entrance: true);

        // Enter is the primary button — except on the last step, where it's a new line in the box.
        var enter = new KeyboardAccelerator { Key = VirtualKey.Enter };
        enter.Invoked += (_, e) =>
        {
            if (_step == Step.TryIt) return;
            e.Handled = true;
            Primary();
        };
        KeyboardAccelerators.Add(enter);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // (Moved to a new parent, WinUI may say Loaded before Unloaded: subscribe once, and
        // let go only when really gone.)
        Loaded += (_, _) =>
        {
            _app.Changed -= Refresh;
            _app.ThemeChanged -= Redraw;
            _app.Changed += Refresh;
            _app.ThemeChanged += Redraw;
            _ticker.Start(DispatcherQueue, 1, () => { _current?.Tick(); _downloads.Update(); });
            if (_step != Step.TryIt) _focus.Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _app.Changed -= Refresh;
            _app.ThemeChanged -= Redraw;
            _ticker.Stop();
            _current?.Leave();
        };
    }

    private static Step FirstStep
    {
        get
        {
#if DEBUG
            if (Enum.TryParse<Step>(Demo.OnboardingStep, ignoreCase: true, out var step)) return step;
#endif
            return Step.Welcome;
        }
    }

    /// The steps keep their size (760 × 640, below the title bar); the light fills whatever
    /// holds them — its own window at first launch, the main window when shown again.
    private void Build(bool entrance)
    {
        Children.Clear();
        RequestedTheme = UI.Theme;
        Children.Add(_glow);
        Children.Add(_focus);

        var column = new Grid
        {
            Width = 760,
            Height = 640,
            Margin = new Thickness(0, TitleBar, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _segments = new ProgressSegments(Enum.GetValues<Step>().Length, (int)_step) { Margin = new Thickness(0, 20, 0, 0) };
        column.Children.Add(_segments);

        _stepHost = new Grid { Margin = new Thickness(60, 0, 60, 0) };
        Grid.SetRow(_stepHost, 1);
        column.Children.Add(_stepHost);

        var footer = Footer();
        Grid.SetRow(footer, 2);
        column.Children.Add(footer);
        Children.Add(column);

        ShowStep(animated: entrance, entrance);
    }

    /// The other appearance: drawn again as it is, without the entrances.
    private void Redraw()
    {
        _current?.Leave();
        _current = null;
        Build(entrance: false);
    }

    // MARK: Footer

    private Grid Footer()
    {
        var footer = new Grid { Height = 44, Margin = new Thickness(32, 0, 32, 26) };
        var back = UI.HStack(8, UI.Icon(Icons.ChevronLeft, 13, "MutedBrush"), UI.Text(Loc.T("Tillbaka", "Back"), 13, FontWeights.Medium, "MutedBrush"));
        _back = UI.PlainButton(back, () => Go(-1), Loc.T("Tillbaka", "Back"));
        _back.VerticalAlignment = VerticalAlignment.Center;
        // While you go through the steps; "Prova" shows it in the box itself.
        _downloads = new DownloadStatus(_app);
        footer.Children.Add(UI.HStack(14, _back, _downloads));
        _primary = new GlowButton(Primary) { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_primary);
        return footer;
    }

    private void UpdateFooter(bool animated)
    {
        var back = _step != Step.Welcome;
        if (back != (_back.Visibility == Visibility.Visible) || !animated)
        {
            _back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
            if (back && animated)
            {
                var visual = Anim.Of(_back);
                visual.Opacity = 0;
                Anim.EaseOut(visual, "Opacity", 1, 0.25);
            }
        }
        _downloads.Visibility = _step is Step.Permissions or Step.Key ? Visibility.Visible : Visibility.Collapsed;
        _downloads.Update();
        _primary.Title = PrimaryTitle;
    }

    private string PrimaryTitle => _step switch
    {
        Step.Welcome => Loc.T("Kom igång", "Get Started"),
        Step.Language => _chosen.All(_app.IsInstalled) ? Loc.T("Fortsätt", "Continue") : Loc.T("Ladda ned och fortsätt", "Download and Continue"),
        Step.TryIt => Loc.T("Börja använda Mindtalk", "Start Using Mindtalk"),
        _ => Loc.T("Fortsätt", "Continue"),
    };

    private void Primary()
    {
        switch (_step)
        {
            case Step.Language:
                // Swedish first if chosen — it becomes the active language.
                var order = SpeechModel.All.Where(_chosen.Contains).ToList();
                if (order.Count > 0) _app.SetEngine(order[0]);
                foreach (var model in order.Where(m => !_app.IsInstalled(m))) _app.DownloadModel(model);
                Go(1);
                break;
            case Step.TryIt:
                _finish();
                break;
            default:
                Go(1);
                break;
        }
    }

    // MARK: Steps

    private void Go(int delta)
    {
        var next = (int)_step + delta;
        if (next < 0 || next > (int)Step.TryIt) return;
        _forward = delta > 0;
        _step = (Step)next;
        ShowStep(animated: true, entrance: true);
    }

    private bool _forward = true;

    private StepView MakeStep(bool entrance) => _step switch
    {
        Step.Welcome => new WelcomeStep(_app, entrance),
        Step.Language => new LanguageStep(_app, entrance, _chosen, () => _primary.Title = PrimaryTitle),
        Step.Permissions => new PermissionsStep(_app, entrance),
        Step.Key => new KeyStep(_app, entrance),
        _ => new TryStep(_app, entrance),
    };

    /// Steps slide 40 points and come into focus: spring(duration 0.55, bounce 0.12).
    private void ShowStep(bool animated, bool entrance)
    {
        var old = _current;
        _current = MakeStep(entrance);
        _stepHost.Children.Add(_current);
        _glow.Intensity = _step == Step.Welcome ? 1 : 0.45;
        if (animated && old is not null) _segments.Current = (int)_step;
        UpdateFooter(animated);

        if (old is not null)
        {
            old.Leave();
            old.IsHitTestVisible = false;
            var gone = Anim.Movable(old);
            Anim.Spring(gone, "Translation", new Vector3((float)(_forward ? -StepShift : StepShift), 0, 0), 0.55, 0.12);
            var host = _stepHost;
            Anim.Spring(gone, "Opacity", 0f, 0.55, 0.12, done: () => host.Children.Remove(old));
        }
        if (!animated || old is null) return;
        var step = _current;
        var visual = Anim.Movable(step, new Vector3((float)(_forward ? StepShift : -StepShift), 0, 0));
        visual.Opacity = 0;
        Anim.WhenLoaded(step, () =>
        {
            Anim.Spring(visual, "Translation", Vector3.Zero, 0.55, 0.12);
            Anim.Spring(visual, "Opacity", 1f, 0.55, 0.12);
        });
    }

    /// Look again (permissions may have changed while Settings was in front).
    internal void Refresh()
    {
        _current?.Update();
        UpdateFooter(animated: true);
    }
}
