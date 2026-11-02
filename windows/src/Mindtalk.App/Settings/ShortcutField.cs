using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Input;
using Windows.UI.Core;

namespace Mindtalk.App.Settings;

/// <summary>
/// A shortcut in a white field, as the Mac's "⌃⌥⌘M ✎ ⊗": the keys as keycaps, a pencil to
/// record new ones, and ⊗ beside it to turn the shortcut off.
///  · the dictation key and the Say a thought keys are picked through the keyboard hook
///    (modifiers held together, left and right distinct);
///  · app-wide shortcuts (Start a meeting, Paste last dictation) are recorded from a key press
///    in the field itself: Ctrl, Alt or Windows with a key.
/// </summary>
public sealed partial class ShortcutField : StackPanel
{
    private enum Kind { Dictation, Thought, App }

    private readonly App _app;
    private readonly Kind _kind;
    private readonly Func<Shortcut?>? _get;
    private readonly Action<Shortcut?>? _set;
    private readonly Func<Shortcut?>? _other;
    private readonly Button _field;
    private readonly Border _face;
    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button? _clear;
    private bool _recording;
    private string _shown = "";

    private ShortcutField(App app, Kind kind, bool clearable, Func<Shortcut?>? get = null, Action<Shortcut?>? set = null, Func<Shortcut?>? other = null)
    {
        _app = app;
        _kind = kind;
        _get = get;
        _set = set;
        _other = other;
        Orientation = Orientation.Horizontal;
        Spacing = 10;
        VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_keys);
        var pencil = UI.Icon(Icons.Pencil, 13, "MutedBrush");
        Grid.SetColumn(pencil, 1);
        row.Children.Add(pencil);
        _face = new Border
        {
            Width = 264,
            Height = 42,
            Padding = new Thickness(6, 0, 14, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Child = row,
        };
        UI.Bind(_face, Border.BackgroundProperty, "FieldBrush");
        _field = new Button { Style = UI.Style("PlainButtonStyle"), Content = _face, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(_field, Tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_field, Tip);
        _field.Click += (_, _) => Toggle();
        Children.Add(_field);

        if (clearable)
        {
            var x = new Grid { Width = 20, Height = 20 };
            var disc = new Ellipse();
            UI.Bind(disc, Shape.FillProperty, "FaintBrush");
            x.Children.Add(disc);
            x.Children.Add(UI.Icon(Icons.XMark, 8, "OnInkBrush"));
            _clear = UI.PlainButton(x, Clear, Loc.T("Stäng av genvägen", "Turn off the shortcut"));
            Children.Add(_clear);
        }
        // The same room without one, so the fields line up.
        else Children.Add(new Grid { Width = 20 });

        if (kind == Kind.App)
        {
            _field.PreviewKeyDown += OnKey;
            _field.LostFocus += (_, _) => StopRecording();
        }
        Unloaded += (_, _) => StopRecording();
        Refresh();
    }

    /// The dictation key (always set: no ⊗).
    public static ShortcutField Dictation(App app) => new(app, Kind.Dictation, clearable: false);

    /// The Say a thought keys; ⊗ turns Say a thought off.
    public static ShortcutField Thought(App app) => new(app, Kind.Thought, clearable: true);

    /// An app-wide shortcut; <paramref name="other"/> is the one it mustn't equal.
    public static ShortcutField AppWide(App app, Func<Shortcut?> get, Action<Shortcut?> set, Func<Shortcut?>? other = null) =>
        new(app, Kind.App, clearable: true, get, set, other);

    /// Why the last shortcut recorded wasn't taken, until the next try.
    public string? Problem { get; private set; }

    public bool Recording => _kind switch
    {
        Kind.Dictation => _app.PickingKey && !_app.PickingThoughtKey,
        Kind.Thought => _app.PickingThoughtKey,
        _ => _recording,
    };

    private string Tip => _kind == Kind.App
        ? Loc.T("Klicka och tryck på den nya genvägen, till exempel Ctrl + Alt + M. Esc avbryter.",
                "Click and press the new shortcut, for example Ctrl + Alt + M. Esc cancels.")
        : Loc.T("Klicka och håll in önskade modifierartangenter tillsammans. Släpp för att spara. Esc avbryter.",
                "Click and hold the modifier keys you want together. Release to save. Esc cancels.");

    private IReadOnlyList<string>? Keys => _kind switch
    {
        Kind.Dictation => Split(_app.Settings.Hotkey.Chip),
        Kind.Thought => _app.Settings.ThoughtHotkey is { } key ? Split(key.Chip) : null,
        _ => _get!() is { } shortcut ? shortcut.Parts : null,
    };

    private static string[] Split(string chip) => chip.Split(" + ");

    private void Toggle()
    {
        Problem = null;
        switch (_kind)
        {
            case Kind.Dictation:
                if (Recording) _app.StopPickingKey(); else _app.PickKey();
                break;
            case Kind.Thought:
                if (Recording) _app.StopPickingKey(); else _app.PickThoughtKey();
                break;
            default:
                if (_recording) StopRecording();
                else
                {
                    _app.StopPickingKey();
                    _recording = true;
                    _field.Focus(FocusState.Programmatic);
                    _app.Refresh();
                }
                break;
        }
    }

    private void Clear()
    {
        Problem = null;
        if (_kind == Kind.Thought) { _app.SetThoughtKey(null); return; }
        StopRecording();
        _set?.Invoke(null);
        _app.SaveSettings();
        _app.RaiseShortcutsChanged();
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _app.Refresh();
    }

    private static bool Down(Windows.System.VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = (int)e.Key;
        if (key == VirtualKey.Escape) { StopRecording(); return; }
        var shortcut = ShortcutRules.FromPress(key, Down(Windows.System.VirtualKey.Control), Down(Windows.System.VirtualKey.Menu),
                                               Down(Windows.System.VirtualKey.Shift),
                                               Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows));
        if (shortcut is null)
        {
            // Only modifiers so far — or a key without Ctrl, Alt or Windows.
            if (!ShortcutRules.IsModifier(key))
            {
                Problem = Loc.T("Använd Ctrl, Alt eller Windows tillsammans med en tangent.", "Use Ctrl, Alt or Windows together with a key.");
                _app.Refresh();
            }
            return;
        }
        _recording = false;
        Problem = Check(shortcut);
        if (Problem is null)
        {
            _set!(shortcut);
            _app.SaveSettings();
            _app.RaiseShortcutsChanged();
        }
        else _app.Refresh();
    }

    /// Already ours for something else, or another app's.
    private string? Check(Shortcut shortcut)
    {
        if (shortcut == _get!()) return null;
        if (_other?.Invoke() == shortcut)
            return Loc.T($"{shortcut} används redan i Mindtalk. Välj en annan.", $"{shortcut} is already used in Mindtalk. Choose another.");
        var id = _app.Shortcuts.Register(shortcut, () => { });
        if (id == 0)
            return Loc.T($"En annan app använder redan {shortcut}. Välj en annan.", $"Another app already uses {shortcut}. Choose another.");
        _app.Shortcuts.Unregister(id);
        return null;
    }

    public void Refresh()
    {
        var recording = Recording;
        UI.Bind(_face, Border.BorderBrushProperty, recording ? "InkBrush" : "FieldStrokeBrush");
        _face.BorderThickness = new Thickness(recording ? 1.5 : 1);
        var keys = Keys;
        var shown = recording ? "…" : keys is null ? "-" : string.Join("|", keys);
        if (_clear is not null)
        {
            var shows = keys is not null && !recording;
            _clear.Opacity = shows ? 1 : 0;
            _clear.IsHitTestVisible = _clear.IsTabStop = shows;
        }
        if (shown == _shown) return;
        _shown = shown;
        _keys.Children.Clear();
        if (recording)
            _keys.Children.Add(Padded(UI.Text(_kind == Kind.App ? Loc.T("Tryck på genvägen…", "Press the shortcut…") : Loc.T("Håll in tangenterna…", "Hold the keys…"), 13, brush: "MutedBrush")));
        else if (keys is null)
            _keys.Children.Add(Padded(UI.Text(Loc.T("Av", "Off"), 13, brush: "MutedBrush")));
        else
            foreach (var key in keys) _keys.Children.Add(Chip(key));
    }

    private static TextBlock Padded(TextBlock text)
    {
        text.Margin = new Thickness(8, 0, 0, 0);
        return text;
    }

    /// A key as the Mac's chip inside a field: soft grey, a hairline edge.
    public static Border Chip(string key)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = UI.Text(key, 13, FontWeights.SemiBold),
        };
        UI.Bind(chip, Border.BackgroundProperty, "CardBrush");
        UI.Bind(chip, Border.BorderBrushProperty, "FieldStrokeBrush");
        return chip;
    }
}
