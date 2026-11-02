using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Platform;

namespace Mindtalk.App.Pages;

/// <summary>
/// "Microphone" sheet: one card per mic. The chosen one opens up into a live waveform with a
/// plain-words check that we can hear you. (The Mac's MicrophonePicker.)
/// </summary>
public sealed partial class MicrophonePicker : Grid, IDisposable
{
    private readonly App _app;
    private readonly StackPanel _options = new() { Spacing = 10, Padding = new Thickness(0, 22, 0, 22) };
    private readonly Waveform _wave = new() { Height = 34 };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7 };
    private readonly TextBlock _hearing = UI.Text("", 12, brush: "MutedBrush");
    private bool _showOthers;

    public MicrophonePicker(App app, Action close)
    {
        _app = app;
        Width = 520;
        Height = 580;
        Padding = new Thickness(28);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Text = Loc.T("Mikrofon", "Microphone"), FontFamily = UI.Serif, FontSize = 30 };
        UI.Bind(title, TextBlock.ForegroundProperty, "PrimaryBrush");
        var x = new Grid { Width = 28, Height = 28 };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, "ChipBrush");
        x.Children.Add(disc);
        x.Children.Add(UI.Icon(Icons.XMark, 11));
        var closeButton = UI.PlainButton(x, close, Loc.T("Stäng", "Close"));
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;
        var head = new Grid();
        head.Children.Add(title);
        head.Children.Add(closeButton);
        Children.Add(head);

        var subtitle = UI.Muted(Loc.T("Välj vad Mindtalk lyssnar med. Prata för att testa.", "Choose what Mindtalk listens with. Talk to test it."), 14);
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        SetRow(subtitle, 1);
        Children.Add(subtitle);

        var scroll = new ScrollViewer { Content = _options };
        SetRow(scroll, 2);
        Children.Add(scroll);

        var foot = new Grid();
        foot.Children.Add(UI.HStack(5, UI.Icon(Icons.Lock, 11, "MutedBrush"), UI.Text(Loc.T("Ljudet stannar på din dator.", "The audio stays on your PC."), 12, brush: "MutedBrush")));
        var done = UI.InkButton(Loc.T("Klar", "Done"), close);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(done);
        SetRow(foot, 3);
        Children.Add(foot);

        _app.Microphones.Refresh();
        var choice = _app.Settings.Microphone;
        if (_app.Microphones.Devices.Any(d => d.IsVirtual && d.Id == choice)) _showOthers = true;
        Build();
        _app.Microphones.LevelsChanged += OnLevels;
        _app.Microphones.Changed += OnDevices;
        _app.Microphones.StartMonitor(choice);
    }

    private void OnLevels() => DispatcherQueue.TryEnqueue(() =>
    {
        _wave.SetLevels(_app.Microphones.History.ToArray());
        var (text, good) = _app.Microphones.Hearing;
        _hearing.Text = text;
        UI.Bind(_dot, Shape.FillProperty, good == true ? "GoodBrush" : good == false ? "OrangeBrush" : "FieldStrokeBrush");
    });

    private void OnDevices() => DispatcherQueue.TryEnqueue(Build);

    private void Build()
    {
        var mics = _app.Microphones;
        _options.Children.Clear();
        if (mics.BuiltIn is { } builtIn)
            _options.Children.Add(Option(Microphones.BuiltInChoice, Icons.Laptop, Loc.T("Inbyggd mikrofon", "Built-in microphone"), builtIn.Name, Loc.T("Rekommenderas", "Recommended")));
        _options.Children.Add(Option(Microphones.SystemChoice, Icons.Sparkles, mics.SystemName,
                                     Loc.T("Följer ljudinställningarna på din dator", "Follows your PC’s sound settings")));
        foreach (var device in mics.Devices.Where(d => !d.IsBuiltIn && !d.IsVirtual))
            _options.Children.Add(Option(device.Id, IconFor(device), device.Name,
                device.IsBluetooth ? Loc.T("Bluetooth – ljudet blir sämre medan mikrofonen används", "Bluetooth – sound quality drops while the mic is in use") : null));
        var virtualDevices = mics.Devices.Where(d => d.IsVirtual).ToList();
        if (virtualDevices.Count > 0)
        {
            var chevron = UI.Icon(_showOthers ? Icons.ChevronDown : Icons.ChevronRight, 11, "MutedBrush");
            var toggle = UI.PlainButton(UI.HStack(6, chevron, UI.Text(Loc.T($"Virtuella enheter ({virtualDevices.Count})", $"Virtual devices ({virtualDevices.Count})"), 13, brush: "MutedBrush")),
                                        () => { _showOthers = !_showOthers; Build(); });
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 4, 0, 0);
            _options.Children.Add(toggle);
            if (_showOthers)
                foreach (var device in virtualDevices)
                    _options.Children.Add(Option(device.Id, Icons.Stack, device.Name, Loc.T("Från en annan app, t.ex. ett mötesprogram", "From another app, e.g. a meeting app")));
        }
    }

    private static string IconFor(InputDevice device) => device.Kind switch
    {
        InputKind.BuiltIn => Icons.Laptop,
        InputKind.Bluetooth => Icons.Headphones,
        InputKind.Virtual => Icons.Stack,
        _ => Icons.Mic,
    };

    private Button Option(string choice, string glyph, string title, string? detail, string? badge = null)
    {
        var selected = _app.Settings.Microphone == choice
            || (choice == Microphones.SystemChoice && _app.Settings.Microphone == Microphones.BuiltInChoice && _app.Microphones.BuiltIn is null);
        var icon = new Grid { Width = 38, Height = 38 };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, selected ? "AccentTintBrush" : "ChipBrush");
        icon.Children.Add(disc);
        icon.Children.Add(UI.Icon(glyph, 16, selected ? "AccentBrush" : "MutedBrush"));

        var name = UI.HStack(8, UI.Text(title, 15, FontWeights.SemiBold));
        if (badge is not null)
        {
            var tag = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 2, 7, 2), Child = UI.Text(badge, 11, FontWeights.SemiBold, "AccentBrush") };
            UI.Bind(tag, Border.BackgroundProperty, "AccentTintBrush");
            name.Children.Add(tag);
        }
        var text = UI.VStack(3, name);
        if (detail is not null)
        {
            var line = UI.Text(detail, 13, brush: "MutedBrush");
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(line);
        }
        var check = UI.Icon(selected ? Icons.CheckCircleFill : Icons.Circle, 18, selected ? "AccentBrush" : "FieldStrokeBrush");
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Grid.SetColumn(check, 2);
        row.Children.Add(check);

        var content = UI.VStack(14, row);
        if (selected)
        {
            // The chosen mic, live: a waveform and whether we hear you.
            _wave.Color = UI.Dark ? Windows.UI.Color.FromArgb(255, 242, 242, 242) : Windows.UI.Color.FromArgb(255, 17, 17, 17);
            (_wave.Parent as Panel)?.Children.Remove(_wave);
            (_dot.Parent as Panel)?.Children.Remove(_dot);
            (_hearing.Parent as Panel)?.Children.Remove(_hearing);
            content.Children.Add(UI.VStack(8, _wave, UI.HStack(6, _dot, _hearing)));
        }
        var card = new Border
        {
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(selected ? 1.5 : 0),
            Child = content,
        };
        UI.Bind(card, Border.BackgroundProperty, selected ? "FieldBrush" : "CardBrush");
        if (selected) card.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(((Microsoft.UI.Xaml.Media.SolidColorBrush)UI.Brush("AccentBrush")).Color) { Opacity = 0.7 };
        var button = new Button { Style = UI.Style("PlainButtonStyle"), Content = card, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) =>
        {
            if (_app.Settings.Microphone == choice) return;
            _app.Settings.Microphone = choice;
            _app.SaveSettings();
            _app.Microphones.StartMonitor(choice);
            Build();
        };
        return button;
    }

    public void Dispose()
    {
        _app.Microphones.LevelsChanged -= OnLevels;
        _app.Microphones.Changed -= OnDevices;
        _app.Microphones.StopMonitor();
    }
}
