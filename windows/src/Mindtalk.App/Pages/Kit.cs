using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;

namespace Mindtalk.App.Pages;

/// <summary>
/// The pieces 2.0's Home, Dictation and Thoughts pages are made of: the spaced caption over a
/// serif title, the ⋯ menu, big soft cards, grey chip buttons, ink buttons, the large keycap,
/// fields with an icon. One place, so the three pages read alike.
/// </summary>
internal static class Kit
{
    /// The card radius of 2.0's big cards (the key card, Home's stats card).
    public const double BigRadius = 24;

    /// "SUNDAY, OCTOBER 25", "640 WORDS TODAY": small capitals, spaced.
    public static TextBlock Caption(string text, double size = 13)
    {
        var caption = UI.Text(text.ToUpper(Loc.Culture), size, FontWeights.SemiBold, "MutedBrush");
        caption.CharacterSpacing = 160;
        return caption;
    }

    /// The page's serif title.
    public static TextBlock Title(string text, double size = 44)
    {
        var title = new TextBlock { Text = text, FontFamily = UI.Serif, FontSize = size, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        UI.Bind(title, TextBlock.ForegroundProperty, "PrimaryBrush");
        return title;
    }

    /// Caption, title, and (optional) the ⋯ menu at the top right.
    public static Grid Header(TextBlock caption, TextBlock title, MenuFlyout? menu = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UI.VStack(4, caption, title));
        if (menu is not null)
        {
            var more = MoreButton(menu);
            more.VerticalAlignment = VerticalAlignment.Top;
            more.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetColumn(more, 1);
            grid.Children.Add(more);
        }
        return grid;
    }

    /// ⋯ in a thin circle, opening a menu.
    public static Button MoreButton(MenuFlyout menu)
    {
        var circle = new Grid { Width = 22, Height = 22 };
        var ring = new Ellipse { StrokeThickness = 1.3 };
        UI.Bind(ring, Shape.StrokeProperty, "PrimaryBrush");
        circle.Children.Add(ring);
        var dots = UI.HStack(2.2);
        dots.HorizontalAlignment = HorizontalAlignment.Center;
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 2.6, Height = 2.6 };
            UI.Bind(dot, Shape.FillProperty, "PrimaryBrush");
            dots.Children.Add(dot);
        }
        circle.Children.Add(dots);
        var button = UI.PlainButton(circle, () => { }, Loc.T("Mer", "More"));
        button.Padding = new Thickness(4);
        button.Flyout = menu;
        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight;
        return button;
    }

    public static MenuFlyoutItem Item(string text, Action action, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"] };
        item.Click += (_, _) => action();
        return item;
    }

    /// A big soft card with a hairline edge (the key card, Home's stats card).
    public static Border BigCard(UIElement child, Thickness padding)
    {
        var card = new Border { Child = child, Padding = padding, CornerRadius = new CornerRadius(BigRadius), BorderThickness = new Thickness(1) };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        card.BorderBrush = Tint("DividerBrush", UI.Dark ? 1 : 0.9);
        return card;
    }

    /// A soft card without an edge (the stats row, the lists).
    public static Border Card(UIElement child, Thickness padding, double radius = 20)
    {
        var card = new Border { Child = child, Padding = padding, CornerRadius = new CornerRadius(radius) };
        UI.Bind(card, Border.BackgroundProperty, "CardBrush");
        return card;
    }

    public static SolidColorBrush Tint(string key, double opacity)
    {
        var brush = (SolidColorBrush)UI.Brush(key);
        return new SolidColorBrush(brush.Color) { Opacity = brush.Opacity * opacity };
    }

    /// The Mac's grey bordered-less button: "Change", "Try It", "Say a Thought".
    public static Button Chip(string title, Action action, string? glyph = null, double size = 14)
    {
        var label = UI.Text(title, size, FontWeights.SemiBold);
        UIElement content = glyph is null ? label : UI.HStack(7, UI.Icon(glyph, size - 1), label);
        var face = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(17, 9, 17, 9), Child = content };
        UI.Bind(face, Border.BackgroundProperty, "ChipBrush");
        var button = UI.PlainButton(face, action);
        AutomationProperties.SetName(button, title);
        button.CornerRadius = face.CornerRadius;
        button.VerticalAlignment = VerticalAlignment.Center;
        Hover(button, face);
        return button;
    }

    /// The ink button, with something before its title (a red dot, a mic).
    public static Button Ink(string title, Action action, UIElement? leading = null, double size = 14)
    {
        var label = UI.Text(title, size, FontWeights.SemiBold, "OnInkBrush");
        var content = leading is null ? (UIElement)label : UI.HStack(8, leading, label);
        var face = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 9, 18, 9), Child = content };
        UI.Bind(face, Border.BackgroundProperty, "InkBrush");
        var button = UI.PlainButton(face, action);
        AutomationProperties.SetName(button, title);
        button.CornerRadius = face.CornerRadius;
        button.VerticalAlignment = VerticalAlignment.Center;
        Hover(button, face);
        return button;
    }

    /// A small red dot, for Start Meeting.
    public static Ellipse RedDot(double size = 9)
    {
        var dot = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        UI.Bind(dot, Shape.FillProperty, "RecordingBrush");
        return dot;
    }

    /// A gentle lift on hover, as the Mac's buttons brighten.
    private static void Hover(Button button, UIElement face)
    {
        button.PointerEntered += (_, _) => face.Opacity = 0.86;
        button.PointerExited += (_, _) => face.Opacity = 1;
        button.PointerCanceled += (_, _) => face.Opacity = 1;
    }

    /// "Latest meetings" on the left, "All meetings →" on the right.
    public static Grid SectionHeader(string title, string? link = null, Action? open = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UI.Text(title, 18, FontWeights.SemiBold));
        if (link is not null && open is not null)
        {
            var button = UI.PlainButton(UI.HStack(5, UI.Text(link, 15, FontWeights.Medium, "MutedBrush"), UI.Icon(Icons.ArrowRight, 12, "MutedBrush")), open);
            AutomationProperties.SetName(button, link);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        return grid;
    }

    /// A large keycap, as on the key cards: "Alt →".
    public static Grid KeyCap(string text, double minWidth = 120)
    {
        var face = new Border
        {
            MinWidth = minWidth,
            Height = 62,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 0, 18, 0),
            Child = new TextBlock
            {
                Text = text, FontSize = 23, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = UI.Brush("PrimaryBrush"),
            },
        };
        UI.Bind(face, Border.BackgroundProperty, "ControlBrush");
        face.BorderBrush = Tint("FieldStrokeBrush", 0.8);
        var cap = SoftShadow.Around(face, 13, 4, 2, UI.Dark ? 0.5 : 0.16);
        cap.VerticalAlignment = VerticalAlignment.Center;
        return cap;
    }

    /// A field with an icon at its start and a quiet placeholder: "✎ Or type a thought…".
    public static TextBox Field(string placeholder, string glyph, out Grid host)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            FontSize = 16,
            Padding = new Thickness(44, 11, 12, 11),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        UI.Bind(box, Control.BackgroundProperty, "FieldBrush");
        UI.Bind(box, Control.BorderBrushProperty, "FieldStrokeBrush");
        AutomationProperties.SetName(box, placeholder);
        var icon = UI.Icon(glyph, 15, "MutedBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(17, 0, 0, 0);
        icon.IsHitTestVisible = false;
        host = new Grid();
        host.Children.Add(box);
        host.Children.Add(icon);
        return box;
    }

    /// Tooltip and screen-reader name in one go.
    public static T Help<T>(T element, string help) where T : DependencyObject
    {
        ToolTipService.SetToolTip(element, help);
        AutomationProperties.SetName(element, help);
        return element;
    }

    /// A small icon button for a row's hover actions.
    public static Button IconButton(string glyph, string help, Action action, double size = 15)
    {
        var icon = UI.Icon(glyph, size, "MutedBrush");
        var button = UI.PlainButton(new Grid { Width = 28, Height = 28, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { icon } }, action, help);
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    /// "Today", "Yesterday", "Wednesday, October 14" — a day's heading.
    public static string Day(DateTime date, DateTime now)
    {
        if (date.Date == now.Date) return Loc.T("I dag", "Today");
        if (date.Date == now.Date.AddDays(-1)) return Loc.T("I går", "Yesterday");
        var format = date.Year == now.Year ? Loc.T("dddd d MMMM", "dddd, MMMM d") : Loc.T("d MMMM yyyy", "MMMM d, yyyy");
        return date.ToString(format, Loc.Culture);
    }

    /// "9:30 AM" / "9:30".
    public static string Time(DateTime date) => Loc.IsSwedish ? date.ToString("H:mm", Loc.Culture) : date.ToString("t", Loc.Culture);

    /// Shown and hidden at once.
    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// Runs on Enter in a text box.
    public static void OnEnter(TextBox box, Action action) => box.KeyDown += (_, e) =>
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        action();
    };

    /// Esc in a text box.
    public static void OnEscape(UIElement element, Action action) => element.KeyDown += (object _, KeyRoutedEventArgs e) =>
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        action();
    };
}
