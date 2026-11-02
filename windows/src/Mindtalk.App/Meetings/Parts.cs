using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Input;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// The small pieces the meeting pages share: the grey pills ("‹ Meetings", "Share"), the
/// round ⋯ button, letter-spaced captions, clickable times, the ask bar, the start buttons.
/// Sizes are the Mac's divided by about 1.27 — the same ratio as the rest of the Windows app.
/// </summary>
internal static class Parts
{
    /// A grey capsule with an optional icon: "‹ Meetings", "Share".
    public static Button Pill(string text, string? glyph, Action action, bool iconAfter = false)
    {
        var label = UI.Text(text, 13, FontWeights.SemiBold);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null && !iconAfter) content.Children.Add(UI.Icon(glyph, 11));
        content.Children.Add(label);
        if (glyph is not null && iconAfter) content.Children.Add(UI.Icon(glyph, 9, "MutedBrush"));
        var face = new Border { CornerRadius = new CornerRadius(100), Height = 28, Padding = new Thickness(12, 0, 13, 0), Child = content };
        UI.Bind(face, Border.BackgroundProperty, "ChipBrush");
        var button = UI.PlainButton(face, action);
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// The round grey button with ⋯ (or another icon).
    public static Button Circle(string glyph, Action action, string help, double size = 30)
    {
        var face = new Grid { Width = size, Height = size };
        var disc = new Ellipse();
        UI.Bind(disc, Shape.FillProperty, "ChipBrush");
        face.Children.Add(disc);
        face.Children.Add(UI.Icon(glyph, size * 0.4));
        return UI.PlainButton(face, action, help);
    }

    /// "WHAT YOU TALKED ABOUT": small, spaced capitals.
    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpper(Loc.Culture),
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        CharacterSpacing = 140,
        Foreground = UI.Brush("MutedBrush"),
    };

    public static TextBlock Serif(string text, double size, bool wrap = true, string brush = "PrimaryBrush") => new()
    {
        Text = text,
        FontFamily = size >= 30 ? UI.Serif : TextSerif,
        FontSize = size,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        Foreground = UI.Brush(brush),
    };

    public static FontFamily TextSerif => (FontFamily)Application.Current.Resources["SerifTextFont"];

    /// A time you can click to hear that moment: "2:33".
    public static Button Time(double seconds, Action<double>? play)
    {
        var text = UI.Text(Meeting.Clock(seconds), 12.5, FontWeights.Medium, "MutedBrush");
        Parts.Tabular(text);
        var button = UI.PlainButton(text, () => play?.Invoke(seconds), Loc.T($"Lyssna från {Meeting.Clock(seconds)}", $"Listen from {Meeting.Clock(seconds)}"));
        button.IsEnabled = play is not null;
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    /// Figures of equal width, so a running clock doesn't jiggle.
    public static void Tabular(TextBlock text) =>
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);

    /// A small coloured dot (the red one on the start buttons).
    public static Ellipse Dot(string brush, double size = 10)
    {
        var dot = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        UI.Bind(dot, Shape.FillProperty, brush);
        return dot;
    }

    /// A shortcut as one small keycap: "Ctrl Alt M".
    public static Border KeyChip(Shortcut? shortcut) => KeyChip(shortcut is null ? "–" : string.Join(" ", shortcut.Parts));

    public static Border KeyChip(string text, bool onInk = false)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = UI.Text(text, 11, FontWeights.SemiBold, onInk ? "OnInkBrush" : "PrimaryBrush"),
        };
        UI.Bind(chip, Border.BackgroundProperty, onInk ? "InkBrush" : "ControlBrush");
        UI.Bind(chip, Border.BorderBrushProperty, "FieldStrokeBrush");
        return chip;
    }

    /// A thin ink bar on a grey track; null: indeterminate.
    public static Grid Progress(double? value, double width = double.NaN)
    {
        var grid = new Grid { Height = 4, Width = width, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        UI.Bind(grid, Panel.BackgroundProperty, "ChipBrush");
        if (value is null)
        {
            grid.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, Foreground = UI.Brush("InkBrush"), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) });
            return grid;
        }
        var v = Math.Clamp(value.Value, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, v), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - v), GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        UI.Bind(fill, Border.BackgroundProperty, "InkBrush");
        grid.Children.Add(fill);
        return grid;
    }

    /// The big start buttons on the Meetings page: "● Start with a round of names", "● Start right away ↵".
    public static Button StartButton(string text, bool ink, Action action, bool returnKey = false)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Dot("RecordingBrush", 10));
        var label = UI.Text(text, 16, FontWeights.SemiBold, ink ? "OnInkBrush" : "PrimaryBrush");
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (returnKey)
        {
            var enter = UI.Icon("", 12, ink ? "OnInkBrush" : "MutedBrush");
            enter.Opacity = 0.7;
            Grid.SetColumn(enter, 2);
            row.Children.Add(enter);
        }
        var face = new Border
        {
            Height = 50,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(22, 0, 20, 0),
            BorderThickness = new Thickness(ink ? 0 : 1),
            Child = row,
        };
        UI.Bind(face, Border.BackgroundProperty, ink ? "InkBrush" : (UI.Dark ? "ChipBrush" : "ControlBrush"));
        UI.Bind(face, Border.BorderBrushProperty, "DividerBrush");
        var button = UI.PlainButton(face, action);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// "Listens in Swedish  Change": quiet text, the action in semibold.
    public static StackPanel TextWithAction(string? glyph, string text, string action, Action<FrameworkElement> onAction)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null) stack.Children.Add(UI.Icon(glyph, 13, "MutedBrush"));
        stack.Children.Add(UI.Text(text, 13, brush: "MutedBrush"));
        var link = UI.PlainButton(UI.Text(action, 13, FontWeights.SemiBold, "MutedBrush"), () => { });
        link.Click += (s, _) => onAction((FrameworkElement)s);
        AutomationProperties.SetName(link, $"{action}: {text}");
        stack.Children.Add(link);
        return stack;
    }

    /// The white capsule at the bottom of a page: "Ask about all meetings  Ctrl K  ↑".
    public sealed partial class AskBar : Grid
    {
        public readonly TextBox Field;
        private readonly Button _send;

        public AskBar(string placeholder, string? chip, Action<string> ask)
        {
            Height = 46;
            VerticalAlignment = VerticalAlignment.Bottom;
            var face = new Border { CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 7, 0) };
            UI.Bind(face, Border.BackgroundProperty, "FieldBrush");
            UI.Bind(face, Border.BorderBrushProperty, "FieldStrokeBrush");
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Field = new TextBox
            {
                PlaceholderText = placeholder,
                FontSize = 14.5,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 5, 6, 6),
            };
            Field.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Field.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Field.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
            Field.Resources["TextControlPlaceholderForeground"] = UI.Brush("PrimaryBrush");
            Field.Resources["TextControlPlaceholderForegroundPointerOver"] = UI.Brush("PrimaryBrush");
            Field.Resources["TextControlPlaceholderForegroundFocused"] = UI.Brush("MutedBrush");
            AutomationProperties.SetName(Field, placeholder);
            row.Children.Add(Field);
            if (chip is not null)
            {
                var keys = UI.Text(chip, 12, FontWeights.SemiBold, "MutedBrush");
                keys.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetColumn(keys, 1);
                row.Children.Add(keys);
            }
            var arrow = new Grid { Width = 30, Height = 30 };
            var disc = new Ellipse();
            UI.Bind(disc, Shape.FillProperty, "ChipBrush");
            arrow.Children.Add(disc);
            arrow.Children.Add(UI.Icon(Icons.ArrowUp, 12, "MutedBrush"));
            _send = UI.PlainButton(arrow, Send, Loc.T("Fråga", "Ask"));
            Grid.SetColumn(_send, 2);
            row.Children.Add(_send);
            face.Child = row;
            Children.Add(SoftShadow.Around(face, 23, 14, 3, UI.Dark ? 0.5 : 0.10));
            Field.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                e.Handled = true;
                Send();
            };
            void Send()
            {
                var question = Field.Text.Trim();
                if (question.Length == 0) { Field.Focus(FocusState.Programmatic); return; }
                Field.Text = "";
                ask(question);
            }
        }

        public void FocusField() => Field.Focus(FocusState.Keyboard);
    }

    /// Lets a <see cref="ScrollViewer"/>'s content end above a floating bar.
    public static Border Spacer(double height) => new() { Height = height };

    /// The red dot that pulses while recording.
    public static Ellipse RecordingDot(double size = 9)
    {
        var dot = Dot("RecordingBrush", size);
        if (!Motion.Reduced)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(dot);
            var compositor = visual.Compositor;
            var pulse = compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0, 1);
            pulse.InsertKeyFrame(0.5f, 0.35f);
            pulse.InsertKeyFrame(1, 1);
            pulse.Duration = TimeSpan.FromSeconds(1.6);
            pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            dot.Loaded += (_, _) => visual.StartAnimation("Opacity", pulse);
        }
        return dot;
    }

    /// A text block whose parts can be bold: used for "Is this **Anna**?".
    public static TextBlock Rich(double size, params (string Text, bool Bold)[] parts)
    {
        var block = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = UI.Brush("PrimaryBrush") };
        foreach (var (text, bold) in parts)
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        return block;
    }

    /// Marks: their icon and name.
    public static string MarkGlyph(MarkKind kind) => kind switch
    {
        MarkKind.Decision => Icons.Flag,
        MarkKind.Todo => Icons.Tasks,
        _ => Icons.Important,
    };

    public static string MarkTitle(MarkKind kind) => kind switch
    {
        MarkKind.Decision => Loc.T("Beslut", "Decision"),
        MarkKind.Todo => Loc.T("Att göra", "To do"),
        _ => Loc.T("Viktigt", "Important"),
    };

    public static Shortcut MarkShortcut(MarkKind kind) => kind switch
    {
        MarkKind.Decision => Shortcut.MarkDecision,
        MarkKind.Todo => Shortcut.MarkTodo,
        _ => Shortcut.MarkImportant,
    };

    /// A soft rounded button with an icon and a label (Decision / To do / Important, Pause).
    public static Button SoftPill(string? glyph, string text, Action action, string? help = null, bool ink = false, double height = 32)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null) content.Children.Add(UI.Icon(glyph, 12, ink ? "OnInkBrush" : "PrimaryBrush"));
        content.Children.Add(UI.Text(text, 13, FontWeights.SemiBold, ink ? "OnInkBrush" : "PrimaryBrush"));
        var face = new Border { CornerRadius = new CornerRadius(height / 2), Height = height, Padding = new Thickness(13, 0, 14, 0), Child = content };
        UI.Bind(face, Border.BackgroundProperty, ink ? "InkBrush" : "ChipBrush");
        var button = UI.PlainButton(face, action, help ?? text);
        return button;
    }

    /// Hover highlight for a clickable card row.
    public static void Hover(Border row, string normal = "CardBrush")
    {
        row.PointerEntered += (_, _) => UI.Bind(row, Border.BackgroundProperty, "HoverBrush");
        row.PointerExited += (_, _) => row.Background = normal == "Transparent" ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : UI.Brush(normal);
    }

    public static void OnClick(UIElement element, Action action)
    {
        element.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideButton(source, element)) return;
            action();
        };
    }

    /// A tap that landed on a button inside the row belongs to that button.
    private static bool InsideButton(DependencyObject source, UIElement stop)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, stop); node = VisualTreeHelper.GetParent(node))
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or TextBox or CheckBox) return true;
        return false;
    }
}
