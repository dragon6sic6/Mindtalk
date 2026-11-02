using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.App.Pages;
using Mindtalk.Core;

namespace Mindtalk.App.Settings;

/// <summary>Builds the page for a section of Settings (MainWindow.SettingsFactory).</summary>
public static class SettingsPages
{
    public static PageView Make(App app, MainWindow window, SettingsSection section) => section switch
    {
        SettingsSection.Language => new LanguageSettings(app, window),
        SettingsSection.Dictation => new DictationSettings(app, window),
        SettingsSection.Meetings => new MeetingsSettings(app, window),
        SettingsSection.AiApps => new AiAppsSettings(app, window),
        _ => new GeneralSettings(app, window),
    };
}

/// <summary>
/// A section of Settings, as the Mac's: a big serif title, then soft grey cards of rows —
/// a title, a line or two of explanation, and the control on the right.
/// </summary>
public abstract partial class SettingsSectionPage : PageView
{
    private int _stagger = 1;

    protected SettingsSectionPage(App app, MainWindow window, string title) : base(app)
    {
        Window = window;
        Column.Spacing = 16;
        var heading = new TextBlock { Text = title, FontFamily = UI.Serif, FontSize = 46, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        UI.Bind(heading, TextBlock.ForegroundProperty, "PrimaryBrush");
        Add(heading, 0);
    }

    protected MainWindow Window { get; }

    /// A card of rows, rising into place after the ones before it.
    protected Border Group(params UIElement?[] rows)
    {
        var card = Card(rows);
        Add(card, _stagger++);
        return card;
    }

    /// A small heading above a card ("Summaries and answers").
    protected void Heading(string text, string? detail = null)
    {
        var title = UI.Text(text, 15, FontWeights.SemiBold);
        title.Margin = new Thickness(4, 10, 0, detail is null ? -6 : 0);
        Add(title, _stagger);
        if (detail is null) return;
        var line = UI.Muted(detail, 13);
        line.Margin = new Thickness(4, -12, 0, -6);
        Add(line, _stagger);
    }

    /// A note under the cards, in small muted type.
    protected TextBlock Note(string text)
    {
        var note = UI.Muted(text, 12.5);
        note.Margin = new Thickness(4, -6, 0, 0);
        Add(note, _stagger);
        return note;
    }

    /// Tag for a list inside a card that draws its own lines (it may be empty).
    public const string OwnDividers = "own-dividers";

    /// The card itself: soft grey, rounded, rows with inset lines between.
    public static Border Card(params UIElement?[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (stack.Children.Count > 0 && (row as FrameworkElement)?.Tag as string != OwnDividers) stack.Children.Add(Divider());
            stack.Children.Add(row);
        }
        var card = new Border { CornerRadius = new CornerRadius(20), Child = stack };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    public static Rectangle Divider()
    {
        var line = new Rectangle { Height = 1, Margin = new Thickness(24, 0, 24, 0) };
        UI.Bind(line, Shape.FillProperty, "DividerBrush");
        return line;
    }

    /// One row: title, explanation, control on the right.
    public static Grid Row(string title, string? detail, UIElement? control, out TextBlock detailText)
    {
        var grid = new Grid { Padding = new Thickness(24, 19, 24, 19), ColumnSpacing = 24, MinHeight = 64 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = UI.Text(title, 15, FontWeights.SemiBold, wrap: true);
        text.Children.Add(heading);
        detailText = UI.Muted(detail ?? "", 13.5);
        detailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        text.Children.Add(detailText);
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            Grid.SetColumn(element, 1);
            element.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(element);
        }
        return grid;
    }

    public static Grid Row(string title, string? detail, UIElement? control) => Row(title, detail, control, out _);

    /// A switch row that saves as you flip it.
    protected Grid SwitchRow(string title, string? detail, bool isOn, Action<bool> changed) =>
        Row(title, detail, UI.Switch(title, isOn, on => { changed(on); App.SaveSettings(); }));

    /// A control greyed out and not clickable. Buttons grey themselves; the switch needs help.
    public static T Disabled<T>(T control, bool disabled = true) where T : Control
    {
        control.IsEnabled = !disabled;
        if (control is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton) control.Opacity = disabled ? 0.4 : 1;
        return control;
    }

    /// "✓ In use" and similar: a check and a few words in ink.
    public static StackPanel Check(string text) =>
        UI.HStack(6, UI.Icon(Icons.CheckCircleFill, 13, "AccentBrush"), UI.Text(text, 13, FontWeights.SemiBold, "AccentBrush"));
}
