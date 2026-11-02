using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Voices;
using Windows.System;

namespace Mindtalk.App.Meetings;

/// <summary>
/// Naming a speaker (2.3): type and the rest of a known name is filled in, selected, so you
/// can type on or press Return; ▾ lists every name Mindtalk knows — remembered voices, names
/// from earlier meetings, your own. Return saves, Esc cancels.
/// </summary>
internal sealed partial class NameField : Grid
{
    private readonly TextBox _box;
    private readonly IReadOnlyList<string> _known;
    private readonly CheckBox? _remember;
    private bool _suppress, _deleting;

    /// The name (null: cleared) and whether to remember the voice.
    public event Action<string?, bool>? Saved;
    public event Action? Cancelled;

    public NameField(IReadOnlyList<string> known, string? current, bool offerRemember, double width = 240, bool focus = true)
    {
        _known = known;
        RowSpacing = 10;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var field = new Grid { Width = width };
        _box = new TextBox
        {
            Text = current ?? "",
            PlaceholderText = Loc.T("Namn", "Name"),
            Style = UI.Style("FieldStyle"),
            Padding = new Thickness(12, 7, 36, 7),
            IsSpellCheckEnabled = false,
        };
        AutomationProperties.SetName(_box, Loc.T("Namn", "Name"));
        field.Children.Add(_box);
        var list = UI.PlainButton(UI.Icon(Icons.ChevronDown, 10, "MutedBrush"), ShowList, Loc.T("Alla namn", "All names"));
        list.HorizontalAlignment = HorizontalAlignment.Right;
        list.Width = 32;
        list.Height = 32;
        list.HorizontalContentAlignment = HorizontalAlignment.Center;
        list.IsEnabled = known.Count > 0;
        field.Children.Add(list);
        Children.Add(field);

        if (offerRemember)
        {
            _remember = new CheckBox { Content = UI.Text(Loc.T("Kom ihåg rösten", "Remember this voice"), 13), IsChecked = true, MinHeight = 24 };
            Grid.SetRow(_remember, 1);
            Children.Add(_remember);
        }

        _box.KeyDown += OnKey;
        _box.TextChanged += (_, _) => Complete();
        Loaded += (_, _) =>
        {
            if (!focus) return;
            _box.Focus(FocusState.Programmatic);
            _box.SelectAll();
        };
    }

    public string Text => _box.Text.Trim();

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Back or VirtualKey.Delete:
                _deleting = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                Save(_box.Text);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Cancelled?.Invoke();
                break;
            case VirtualKey.Tab or VirtualKey.Right when _box.SelectionLength > 0 && _box.SelectionStart + _box.SelectionLength == _box.Text.Length:
                // Takes the suggestion.
                e.Handled = true;
                _box.Select(_box.Text.Length, 0);
                break;
        }
    }

    /// Fills in the rest of the first known name that starts with what's typed.
    private void Complete()
    {
        if (_suppress) return;
        if (_deleting) { _deleting = false; return; }
        var typed = _box.Text;
        if (_box.SelectionStart != typed.Length || typed.Length == 0) return;
        var name = NameSuggestions.Complete(typed, _known);
        if (name is null) return;
        _suppress = true;
        _box.Text = name;
        _box.Select(typed.Length, name.Length - typed.Length);
        _suppress = false;
    }

    private void ShowList()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var name in _known)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => Save(name);
            menu.Items.Add(item);
        }
        menu.ShowAt(_box);
    }

    private void Save(string text)
    {
        var name = text.Trim();
        Saved?.Invoke(name.Length == 0 ? null : name, _remember?.IsChecked == true);
    }

    /// Every name Mindtalk knows, for the suggestions.
    public static IReadOnlyList<string> Known(App app) => NameSuggestions.Known(
        app.Voices?.All.Select(v => v.Name) ?? [],
        app.Meetings?.All.SelectMany(m => m.Speakers.Where(s => !s.IsBackground).Select(s => s.Name)) ?? [],
        app.Settings.UserName);

    /// The flyout under a speaker's name: name them, remember the voice, or call it background sound.
    public static void ShowSpeakerFlyout(App app, FrameworkElement anchor, Speaker speaker, bool live,
                                         Action<string?, bool> name, Action<bool> background)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var offerRemember = live && app.Settings.RememberVoices;
        var field = new NameField(Known(app), speaker.Name, offerRemember);
        field.Saved += (n, remember) => { flyout.Hide(); name(n, remember); };
        field.Cancelled += flyout.Hide;
        var title = UI.Text(Loc.T("Vem är det här?", "Who is this?"), 14, FontWeights.SemiBold);
        var hint = UI.Text(Loc.T("Return sparar · Esc avbryter", "Return saves · Esc cancels"), 11.5, brush: "FaintBrush");
        var backgroundButton = UI.PlainButton(
            UI.HStack(8, UI.Icon(Icons.Volume, 12, "MutedBrush"),
                UI.Text(speaker.IsBackground ? Loc.T("Det är en person", "This is a person") : Loc.T("Markera som bakgrundsljud", "Mark as background sound"), 13, brush: "MutedBrush")),
            () => { flyout.Hide(); background(!speaker.IsBackground); });
        backgroundButton.HorizontalAlignment = HorizontalAlignment.Left;
        var stack = UI.VStack(10, title, field, hint, UI.Divider(), backgroundButton);
        ((FrameworkElement)stack.Children[3]).Margin = new Thickness(0);
        flyout.Content = stack;
        flyout.FlyoutPresenterStyle = PresenterStyle();
        flyout.ShowAt(anchor);
    }

    /// Flyouts in Mindtalk's look: paper, rounded, a soft edge.
    public static Style PresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, UI.Brush("PaperBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, UI.Brush("FieldStrokeBrush")));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 14, 16, 14)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        return style;
    }
}
