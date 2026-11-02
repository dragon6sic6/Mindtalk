using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// "Take notes in this Teams call?" — a small box near the tray when a call app starts using
/// the microphone. Start (right away or with a round of names) or Not now; it goes away by
/// itself when the call ends. Never takes the keyboard from the call.
/// </summary>
internal sealed class CallBox : FloatingWindow
{
    private readonly MeetingHub _hub;
    private DetectedCall? _call;

    public CallBox(App app, MeetingHub hub) : base(app, 356) => _hub = hub;

    protected override Corner Where => Corner.BottomRight;

    public void Show(DetectedCall call)
    {
        _call = call;
        Refill();
        ShowCard();
    }

    /// The call ended (or null: the box was answered).
    public void Hide(DetectedCall? call)
    {
        if (call is not null && _call is not null && call.ProcessId != _call.ProcessId) return;
        _call = null;
        Hide();
    }

    protected override UIElement Content()
    {
        if (_call is not { } call) return new Grid();
        var stack = new StackPanel { Spacing = 0 };
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top };
        UI.Bind(tile, Border.BackgroundProperty, "InkBrush");
        tile.Child = new Orb(26, onDark: !UI.Dark, count: 260) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(tile);
        var text = UI.VStack(3,
            UI.Text(Loc.T($"Anteckna i det här {call.App}-samtalet?", $"Take notes in this {call.App} call?"), 14, FontWeights.SemiBold, wrap: true),
            UI.Text(Loc.T("Mindtalk skriver ned vad som sägs och vem som säger det. Ingen bot släpps in i samtalet.",
                          "Mindtalk writes down what's said and who says it. No bot joins the call."), 12.5, brush: "MutedBrush", wrap: true));
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        var close = UI.PlainButton(UI.Icon(Icons.XMark, 10, "MutedBrush"), () => Hide(null), Loc.T("Inte nu", "Not now"));
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        head.Children.Add(close);
        stack.Children.Add(head);

        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var start = Parts.SoftPill(null, Loc.T("Starta", "Start"), () => _hub.Start(App.Settings.RoundOfNamesFirst, call, showWindow: false), ink: true, height: 30);
        buttons.Children.Add(start);
        var names = Parts.SoftPill(null, Loc.T("Med namnrunda", "With a Round of Names"), () => _hub.Start(true, call, showWindow: false), height: 30);
        Grid.SetColumn(names, 1);
        buttons.Children.Add(names);
        var later = UI.PlainButton(UI.Text(Loc.T("Inte nu", "Not Now"), 13, FontWeights.SemiBold, "MutedBrush"), () => Hide(null));
        later.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(later, 2);
        buttons.Children.Add(later);
        stack.Children.Add(buttons);
        return stack;
    }
}
