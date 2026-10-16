using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mindtalk.App.Controls;

/// <summary>
/// Mindtalk's segmented control: a warm track with the chosen option lifted out as a paper
/// pill that slides between options. Replaces the system's own.
/// </summary>
public sealed partial class PillPicker<T> : Grid where T : notnull
{
    private readonly (T Value, string Title)[] _options;
    private readonly List<TextBlock> _labels = [];
    private readonly List<Button> _buttons = [];
    private readonly Border _pill, _shade;
    private readonly Animated _x = new(0), _width = new(0);
    private T _selection;
    private long _lastFrame;
    private bool _placed;

    public PillPicker((T Value, string Title)[] options, T selection, Action<T> changed, bool compact = false)
    {
        _options = options;
        _selection = selection;
        Padding = new Thickness(3);
        CornerRadius = new CornerRadius(100);
        HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SetValue(BackgroundProperty, UI.Brush("ChipBrush"));

        // The paper pill under the chosen option.
        var canvas = new Canvas();
        _pill = new Border { CornerRadius = new CornerRadius(100), BorderThickness = new Thickness(0.5), Height = compact ? 24 : 28 };
        _pill.SetValue(Border.BackgroundProperty, UI.Brush("PillBrush"));
        _pill.SetValue(Border.BorderBrushProperty, UI.Brush("FieldStrokeBrush"));
        // Its soft shadow, 1 px down.
        _shade = new Border { CornerRadius = new CornerRadius(100), Height = _pill.Height, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 0, 0, 0)) };
        Canvas.SetTop(_shade, 1);
        canvas.Children.Add(_shade);
        canvas.Children.Add(_pill);
        Children.Add(canvas);

        var row = new Grid();
        for (var i = 0; i < options.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var option = options[i];
            var label = new TextBlock
            {
                Text = option.Title,
                FontSize = compact ? 12 : 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Button
            {
                Style = UI.Style("PlainButtonStyle"),
                Content = label,
                Height = compact ? 24 : 28,
                Padding = new Thickness(compact ? 10 : 13, 0, compact ? 10 : 13, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetName(button, option.Title);
            button.Click += (_, _) =>
            {
                if (Equals(_selection, option.Value)) return;
                _selection = option.Value;
                Paint(animated: true);
                changed(option.Value);
            };
            Grid.SetColumn(button, i);
            row.Children.Add(button);
            _labels.Add(label);
            _buttons.Add(button);
        }
        Children.Add(row);
        row.LayoutUpdated += (_, _) => { if (!_placed && _buttons[0].ActualWidth > 0) { _placed = true; Paint(animated: false); } };
        SizeChanged += (_, _) => Paint(animated: false);
        Paint(animated: false);
    }

    public T Selection
    {
        get => _selection;
        set { _selection = value; Paint(animated: true); }
    }

    private void Paint(bool animated)
    {
        var index = Array.FindIndex(_options, o => Equals(o.Value, _selection));
        for (var i = 0; i < _labels.Count; i++)
        {
            var selected = i == index;
            _labels[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
            _labels[i].SetValue(TextBlock.ForegroundProperty, UI.Brush(selected ? "PrimaryBrush" : "MutedBrush"));
        }
        if (index < 0 || _buttons[index].ActualWidth <= 0) return;
        var target = _buttons[index].TransformToVisual(_buttons[0]).TransformPoint(default).X;
        var width = _buttons[index].ActualWidth;
        if (animated)
        {
            // spring(duration 0.35, bounce 0.2), as the pill slides on the Mac.
            _x.Spring(target, 0.35, 0.2);
            _width.Spring(width, 0.35, 0.2);
            _lastFrame = Environment.TickCount64;
            CompositionTarget.Rendering -= OnFrame;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            _x.Set(target);
            _width.Set(width);
        }
        Apply();
    }

    private void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.1, (now - _lastFrame) / 1000.0);
        _lastFrame = now;
        var moving = _x.Step(dt) | _width.Step(dt);
        Apply();
        if (!moving) CompositionTarget.Rendering -= OnFrame;
    }

    private void Apply()
    {
        Canvas.SetLeft(_pill, _x.Value);
        Canvas.SetLeft(_shade, _x.Value);
        _pill.Width = _shade.Width = Math.Max(0, _width.Value);
    }
}

/// <summary>
/// A pop-up menu that looks like the rest of Mindtalk: the choice in a soft capsule with a
/// small chevron.
/// </summary>
public sealed partial class MenuPicker<T> : Button where T : notnull
{
    private readonly (T Value, string Title)[] _options;
    private readonly TextBlock _title = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<T> _changed;
    private T _selection;

    public MenuPicker((T Value, string Title)[] options, T selection, Action<T> changed)
    {
        _options = options;
        _selection = selection;
        _changed = changed;
        Style = UI.Style("PlainButtonStyle");
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        var face = new Border
        {
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Height = 30,
            Padding = new Thickness(13, 0, 13, 0),
            Child = UI.HStack(7, _title, UI.Icon("", 9, "MutedBrush")),
        };
        face.SetValue(Border.BackgroundProperty, UI.Brush("ControlBrush"));
        face.SetValue(Border.BorderBrushProperty, UI.Brush("FieldStrokeBrush"));
        Content = face;
        Click += (_, _) => Open();
        Paint();
    }

    public T Selection
    {
        get => _selection;
        set { _selection = value; Paint(); }
    }

    private void Paint()
    {
        var title = _options.FirstOrDefault(o => Equals(o.Value, _selection)).Title ?? "";
        _title.Text = title;
        _title.SetValue(TextBlock.ForegroundProperty, UI.Brush("PrimaryBrush"));
        AutomationProperties.SetName(this, title);
    }

    private void Open()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var option in _options)
        {
            var item = new ToggleMenuFlyoutItem { Text = option.Title, IsChecked = Equals(option.Value, _selection) };
            item.Click += (_, _) =>
            {
                _selection = option.Value;
                Paint();
                _changed(option.Value);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }
}
