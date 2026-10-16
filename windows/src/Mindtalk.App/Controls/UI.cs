using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace Mindtalk.App.Controls;

/// <summary>
/// Small builders for the pages, mirroring the Mac's SwiftUI components (PageHeader,
/// SectionTitle, CardRow, CardDivider, KeyChip, GrantedLabel…), so the pages read like
/// their SwiftUI originals.
/// </summary>
public static class UI
{
    /// The appearance the windows are drawn in (Light / Dark / System, resolved).
    public static bool Dark { get; set; }

    private static ResourceDictionary? _design;

    /// A colour of the design for the current appearance. (Looked up by hand: code can't
    /// follow {ThemeResource}, and the app's own theme can't change while it runs.)
    public static Brush Brush(string key)
    {
        _design ??= Application.Current.Resources.MergedDictionaries.First(d => d.ThemeDictionaries.ContainsKey("Light"));
        var theme = (ResourceDictionary)_design.ThemeDictionaries[Dark ? "Dark" : "Light"];
        return theme.TryGetValue(key, out var brush) ? (Brush)brush : (Brush)Application.Current.Resources[key];
    }

    public static ElementTheme Theme => Dark ? ElementTheme.Dark : ElementTheme.Light;
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static FontFamily Serif => (FontFamily)Application.Current.Resources["SerifFont"];

    public static void Bind(DependencyObject element, DependencyProperty property, string key) =>
        element.SetValue(property, Brush(key));

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = "PrimaryBrush",
                                 bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetValue(TextBlock.ForegroundProperty, Brush(brush));
        return block;
    }

    public static TextBlock Muted(string text, double size = 13, bool wrap = true) => Text(text, size, brush: "MutedBrush", wrap: wrap);

    public static StackPanel VStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children) if (child is not null) stack.Children.Add(child);
        return stack;
    }

    public static StackPanel HStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) if (child is not null) stack.Children.Add(child);
        return stack;
    }

    /// Serif page title with an optional line under it.
    public static StackPanel PageHeader(string title, string? subtitle = null)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, Style = Style("PageTitleStyle") });
        if (subtitle is not null) stack.Children.Add(new TextBlock { Text = subtitle, Style = Style("PageSubtitleStyle") });
        return stack;
    }

    public static TextBlock SectionTitle(string text) => new() { Text = text, Style = Style("SectionTitleStyle") };

    /// A soft grey card.
    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Style = Style("CardStyle"),
        Child = child,
        Padding = padding ?? new Thickness(0),
    };

    /// A card of rows with dividers between them.
    public static Border Rows(params UIElement?[] rows)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (!first) stack.Children.Add(Divider());
            stack.Children.Add(row);
            first = false;
        }
        return Card(stack);
    }

    public static Rectangle Divider() => new() { Style = Style("CardDividerStyle") };

    /// One row in a card: title, optional explanation, control on the right.
    public static Grid Row(string title, string? detail, UIElement? control, out TextBlock detailText)
    {
        var grid = new Grid { Padding = new Thickness(22, 18, 22, 18), ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Style("RowTitleStyle") });
        detailText = new TextBlock { Text = detail ?? "", Style = Style("RowDetailStyle"), Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible };
        text.Children.Add(detailText);
        grid.Children.Add(text);
        if (control is not null)
        {
            Grid.SetColumn((FrameworkElement)control, 1);
            ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(control);
        }
        return grid;
    }

    public static Grid Row(string title, string? detail, UIElement? control) => Row(title, detail, control, out _);

    public static void SetDetail(TextBlock detail, string? text)
    {
        detail.Text = text ?? "";
        detail.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// A key as a small keycap: "Alt →".
    public static Grid KeyChip(string text)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 0, 0, 0)), Margin = new Thickness(0, 1, 0, -1) });
        var face = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            Child = Text(text, 13, FontWeights.SemiBold),
        };
        face.SetValue(Border.BackgroundProperty, Brush("ControlBrush"));
        face.SetValue(Border.BorderBrushProperty, Brush("FieldStrokeBrush"));
        grid.Children.Add(face);
        return grid;
    }

    /// "✓ Allowed" — ink text, with green only in the check itself.
    public static StackPanel GrantedLabel(string text) =>
        HStack(6, Icon(Icons.CheckCircleFill, 14, "GoodBrush"), Text(text, 13, FontWeights.SemiBold));

    public static FontIcon Icon(string glyph, double size = 16, string brush = "PrimaryBrush")
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size, FontFamily = (FontFamily)Application.Current.Resources["IconFont"] };
        icon.SetValue(IconElement.ForegroundProperty, Brush(brush));
        return icon;
    }

    public static Button InkButton(string title, Action action)
    {
        var button = new Button { Content = title, Style = Style("InkButtonStyle") };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button SoftButton(string title, Action action)
    {
        var button = new Button { Content = title, Style = Style("SoftButtonStyle") };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button PlainButton(UIElement content, Action action, string? help = null)
    {
        var button = new Button { Content = content, Style = Style("PlainButtonStyle") };
        button.Click += (_, _) => action();
        if (help is not null)
        {
            ToolTipService.SetToolTip(button, help);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, help);
        }
        return button;
    }

    /// Mindtalk's switch (see InkSwitchStyle).
    public static ToggleButton Switch(string label, bool isOn, Action<bool> changed)
    {
        var toggle = new ToggleButton { Style = Style("InkSwitchStyle"), IsChecked = isOn };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    /// A link with a small ↗, in the accent.
    public static HyperlinkButton Link(string title, string url)
    {
        var link = new HyperlinkButton
        {
            NavigateUri = new Uri(url),
            Padding = new Thickness(0),
            Content = HStack(2, Text(title, 12, FontWeights.Medium, "AccentBrush"), Icon(Icons.ArrowUpRight, 8, "AccentBrush")),
        };
        return link;
    }
}

/// <summary>Segoe Fluent Icons for the Mac's SF Symbols.</summary>
public static class Icons
{
    public const string Clock = "", Book = "", Sliders = "", HandTap = "";
    public const string Lock = "", Escape = "", Pencil = "", Trash = "";
    public const string XMark = "", Check = "", CheckCircle = "", CheckCircleFill = "";
    public const string Circle = "", ChevronRight = "", ChevronLeft = "", ChevronDown = "", ChevronUp = "";
    public const string Plus = "", Copy = "", Clipboard = "", ArrowUpRight = "", ArrowRight = "";
    public const string Download = "", Globe = "", Mic = "", MicFill = "", Info = "";
    public const string Lightbulb = "", Warning = "", WarningFill = "", Laptop = "";
    public const string Headphones = "", Phone = "", Sparkles = "", Ellipsis = "", Stack = "";
    public const string Seal = "", Ear = "";
}
