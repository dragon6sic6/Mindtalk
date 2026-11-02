using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;
using Mindtalk.Core.Thoughts;
using Windows.Storage.Pickers;

namespace Mindtalk.App.Pages;

/// <summary>
/// Thoughts (2.0): what you said with the thought key, kept here instead of typed. The key
/// (changed or turned off right here) and Say a Thought; a field to type one; search; pinned
/// thoughts, then a group per day. Each thought can be pinned, ticked off, edited, copied or
/// deleted; select several to merge, export or delete them.
/// </summary>
public sealed partial class ThoughtsPage : PageView
{
    private readonly MainWindow _window;
    private readonly TextBlock _caption = Kit.Caption("");
    private readonly Grid _keyCard = new();
    private readonly Grid _selectBar = new();
    private readonly StackPanel _groups = new() { Spacing = 22 };
    private readonly TextBox _search;
    private readonly HashSet<DateTime> _collapsed = [];
    private readonly HashSet<Guid> _selected = [];
    private bool _selecting;
    private Guid? _editing;
    private string? _keyKey, _listKey;

    public ThoughtsPage(App app, MainWindow window) : base(app)
    {
        _window = window;
        Column.Spacing = 24;
        var menu = new MenuFlyout();
        menu.Items.Add(Kit.Item(Loc.T("Exportera till Markdown…", "Export to Markdown…"), () => Export(App.Thoughts.All), Icons.Share));
        menu.Items.Add(Kit.Item(Loc.T("Markera…", "Select…"), () => Select(true), Icons.CheckCircle));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Kit.Item(Loc.T("Rensa alla…", "Clear All…"), ConfirmClear, Icons.Delete));
        Add(Kit.Header(_caption, Kit.Title(Loc.T("Tankar", "Thoughts")), menu), 0);
        Add(_keyCard, 1);

        // Type a thought, or search them.
        var type = Kit.Field(Loc.T("Eller skriv en tanke och tryck på retur", "Or type a thought and press Return"), Icons.Edit, out var typeHost);
        Kit.OnEnter(type, () =>
        {
            if (App.Thoughts.Add(type.Text, DateTime.Now) is not null) type.Text = "";
        });
        _search = Kit.Field(Loc.T("Sök i tankarna", "Search thoughts"), Icons.Search, out var searchHost);
        _search.TextChanged += (_, _) => { _listKey = null; RefreshList(); };
        Kit.OnEscape(_search, () => _search.Text = "");
        var fields = new Grid { ColumnSpacing = 16 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.Children.Add(typeHost);
        Grid.SetColumn(searchHost, 1);
        fields.Children.Add(searchHost);
        Add(fields, 2);
        Add(_selectBar, 2);
        Add(_groups, 3, 4);
        _selectBar.Visibility = Visibility.Collapsed;

        // Ctrl+F searches, as the Mac's ⌘F.
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) => { e.Handled = true; _search.Focus(FocusState.Keyboard); };
        KeyboardAccelerators.Add(find);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }

    public override void Refresh()
    {
        var count = App.Thoughts.Count;
        _caption.Text = (count == 1 ? Loc.T("1 tanke", "1 thought") : Loc.T($"{count} tankar", $"{count} thoughts")).ToUpper(Loc.Culture);
        RefreshKey();
        RefreshList();
    }

    // ---- The key card --------------------------------------------------------------

    private void RefreshKey()
    {
        var state = App.Dictation.State;
        var thinking = state.Thought && state.Phase == Phase.Recording;
        var key = $"{App.Settings.Hotkey}|{App.Settings.ThoughtHotkey}|{App.PickingThoughtKey}|{App.ThoughtKeyProblem}|{thinking}";
        if (key == _keyKey) return;
        _keyKey = key;
        _keyCard.Children.Clear();

        var dictation = App.Settings.Hotkey;
        var chosen = App.Settings.ThoughtHotkey;
        var (hold, fallback) = ThoughtKeys.Resolve(dictation, chosen);
        var picking = App.PickingThoughtKey;

        string capText, title, detail;
        if (picking)
        {
            capText = "…";
            title = Loc.T("Håll in tangenterna du vill ha och släpp", "Hold the keys you want, then let go");
            detail = Loc.T($"En eller flera modifierartangenter – Alt, Ctrl, Skift eller Windows, vänster eller höger – men inte {dictation.InlineName}, som du dikterar med. Esc behåller de du har.",
                           $"One or more modifier keys – Alt, Ctrl, Shift or Windows, left or right – but not {dictation.InlineName}, which you dictate with. Esc keeps the ones you have.");
        }
        else if (hold is not null)
        {
            capText = hold.Chip;
            title = Loc.T($"Håll in {hold.InlineName} och tänk högt", $"Hold {hold.InlineName} and think out loud");
            detail = Loc.T("Släpp när du är klar, så sparas tanken här. Tryck kort för att låsa och prata fritt. Esc avbryter.",
                           "Let go when you’re done, and the thought is saved here. Tap once to lock and talk hands-free. Esc cancels.");
        }
        else if (fallback)
        {
            capText = Shortcut.Thought.ToString();
            title = Loc.T($"Tryck {Shortcut.Thought} och tänk högt", $"Press {Shortcut.Thought} and think out loud");
            detail = Loc.T($"Du dikterar med {dictation.InlineName}, så tankar använder {Shortcut.Thought}. Tryck igen när du är klar, så sparas tanken här. Esc avbryter.",
                           $"You dictate with {dictation.InlineName}, so thoughts use {Shortcut.Thought}. Press it again when you’re done, and the thought is saved here. Esc cancels.");
        }
        else
        {
            capText = Loc.T("Av", "Off");
            title = Loc.T("Tangenten för tankar är avstängd", "The thought key is off");
            detail = Loc.T("Säg en tanke med knappen, eller skriv en nedan. Slå på för att hålla in en tangent och tänka högt.",
                           "Say a thought with the button, or type one below. Turn it on to hold a key and think out loud.");
        }

        var cap = Kit.KeyCap(capText, 150);
        if (!picking && hold is null && !fallback) cap.Opacity = 0.5;
        cap.Margin = new Thickness(0, 0, 8, 0);

        var buttons = UI.HStack(10);
        buttons.HorizontalAlignment = HorizontalAlignment.Left;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        if (picking) buttons.Children.Add(Kit.Chip(Loc.T("Avbryt", "Cancel"), App.StopPickingKey));
        else
        {
            buttons.Children.Add(Kit.Chip(Loc.T("Ändra", "Change"), App.PickThoughtKey));
            buttons.Children.Add(chosen is null
                ? Kit.Chip(Loc.T("Slå på", "Turn On"), () => App.SetThoughtKey(Hotkey.DefaultThought))
                : Kit.Chip(Loc.T("Stäng av", "Turn Off"), () => App.SetThoughtKey(null)));
        }
        var text = UI.VStack(6, UI.Text(title, 19.5, FontWeights.SemiBold, wrap: true), UI.Muted(detail, 15));
        if (App.ThoughtKeyProblem is { } problem && !picking)
        {
            var warning = UI.HStack(6, UI.Icon(Icons.WarningFill, 13, "OrangeBrush"), UI.Text(problem, 14, wrap: true));
            text.Children.Add(warning);
        }
        text.Children.Add(buttons);
        text.VerticalAlignment = VerticalAlignment.Center;

        var mic = UI.Icon(thinking ? Icons.Stop : Icons.MicFill, 14, "OnInkBrush");
        var say = Kit.Ink(thinking ? Loc.T("Spara tanken", "Save Thought") : Loc.T("Säg en tanke", "Say a Thought"), App.SayThought, mic);
        say.Visibility = Kit.Show(!picking);

        var row = new Grid { ColumnSpacing = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(cap);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(say, 2);
        row.Children.Add(say);
        _keyCard.Children.Add(Kit.BigCard(row, new Thickness(34, 36, 34, 34)));
    }

    // ---- The list ----------------------------------------------------------------------

    private void RefreshList()
    {
        var thoughts = App.Thoughts.Search(_search.Text);
        var now = DateTime.Now;
        var key = string.Join(",", thoughts.Select(t => $"{t.Id}:{t.Text.GetHashCode()}:{t.Pinned}:{t.Done}"))
                  + $"|{_selecting}|{string.Join(",", _selected)}|{_editing}|{string.Join(",", _collapsed)}|{now.Date.Ticks}|{App.Thoughts.Count}";
        if (key == _listKey) return;
        _listKey = key;
        _groups.Children.Clear();
        RefreshSelectBar();

        if (App.Thoughts.Count == 0)
        {
            var hint = ThoughtKeys.Resolve(App.Settings.Hotkey, App.Settings.ThoughtHotkey) is { Hold: { } hold }
                ? Loc.T($"Håll in {hold.InlineName} och säg det du tänker på. Det sparas här, inte där du skriver.",
                        $"Hold {hold.InlineName} and say what’s on your mind. It’s kept here, not typed where you are.")
                : Loc.T("Tryck på Säg en tanke och säg det du tänker på. Det sparas här, inte där du skriver.",
                        "Click Say a Thought and say what’s on your mind. It’s kept here, not typed where you are.");
            var empty = UI.VStack(10, Centered(UI.Icon(Icons.Lightbulb, 26, "FaintBrush")),
                Centered(UI.Text(Loc.T("Inga tankar än", "No thoughts yet"), 15, FontWeights.SemiBold)),
                Centered(UI.Muted(hint, 14)));
            _groups.Children.Add(Kit.Card(empty, new Thickness(40), 20));
            return;
        }
        if (thoughts.Count == 0)
        {
            _groups.Children.Add(UI.Muted(Loc.T($"Ingen tanke innehåller ”{_search.Text.Trim()}”.", $"No thought contains “{_search.Text.Trim()}”."), 15));
            return;
        }

        var pinned = thoughts.Where(t => t.Pinned).ToList();
        if (pinned.Count > 0) _groups.Children.Add(Group(Loc.T("Fäst", "Pinned"), pinned, null, now));
        foreach (var day in thoughts.Where(t => !t.Pinned).GroupBy(t => t.Created.Date))
            _groups.Children.Add(Group(Kit.Day(day.Key, now), day.ToList(), day.Key, now));
    }

    private static T Centered<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        if (element is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return element;
    }

    /// "TODAY  2 thoughts", collapsible (the pinned ones always show), and its card of thoughts.
    private StackPanel Group(string title, List<Thought> thoughts, DateTime? day, DateTime now)
    {
        var collapsed = day is { } d && _collapsed.Contains(d);
        var count = UI.Text(thoughts.Count == 1 ? Loc.T("1 tanke", "1 thought") : Loc.T($"{thoughts.Count} tankar", $"{thoughts.Count} thoughts"), 15, brush: "MutedBrush");
        var heading = UI.HStack(14, Kit.Caption(title, 13.5), count);
        FrameworkElement header = heading;
        if (day is { } date)
        {
            var chevron = UI.Icon(collapsed ? Icons.ChevronRight : Icons.ChevronDown, 11, "MutedBrush");
            chevron.Width = 14;
            var toggle = UI.PlainButton(UI.HStack(12, chevron, heading), () =>
            {
                if (!_collapsed.Remove(date)) _collapsed.Add(date);
                RefreshList();
            }, collapsed ? Loc.T("Visa", "Show") : Loc.T("Dölj", "Hide"));
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            // The chevron hangs in the margin, so the captions line up.
            toggle.Margin = new Thickness(-26, 0, 0, 0);
            header = toggle;
        }
        var group = UI.VStack(12, header);
        if (collapsed) return group;
        var list = new StackPanel();
        for (var i = 0; i < thoughts.Count; i++)
        {
            if (i > 0)
            {
                var line = new Rectangle { Height = 1, Margin = new Thickness(42, 0, 22, 0) };
                UI.Bind(line, Shape.FillProperty, "DividerBrush");
                list.Children.Add(line);
            }
            list.Children.Add(Row(thoughts[i], now));
        }
        group.Children.Add(Kit.Card(list, new Thickness(0, 4, 0, 4), 18));
        return group;
    }

    /// One thought in serif, with its time (and a pin); on hover, what you can do with it.
    private Grid Row(Thought thought, DateTime now)
    {
        var row = new Grid { Padding = new Thickness(18, 12, 22, 12), ColumnSpacing = 10, MinHeight = 52, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Selecting: a round check at the start of the row.
        if (_selecting)
        {
            var on = _selected.Contains(thought.Id);
            var check = UI.Icon(on ? Icons.CheckCircleFill : Icons.Circle, 17, on ? "InkBrush" : "FaintBrush");
            check.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(check);
            row.Tapped += (_, _) =>
            {
                if (!_selected.Remove(thought.Id)) _selected.Add(thought.Id);
                RefreshList();
            };
        }

        UIElement body;
        if (_editing == thought.Id) body = Editor(thought);
        else
        {
            var text = new TextBlock
            {
                Text = thought.Text, FontFamily = UI.Serif, FontSize = 19, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = !_selecting,
            };
            UI.Bind(text, TextBlock.ForegroundProperty, "PrimaryBrush");
            if (thought.Done)
            {
                text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                text.Opacity = 0.45;
            }
            if (!_selecting) text.DoubleTapped += (_, _) => Edit(thought.Id);
            body = text;
        }
        Grid.SetColumn((FrameworkElement)body, 1);
        row.Children.Add(body);

        // What you can do, on hover.
        var actions = UI.HStack(0,
            Kit.IconButton(thought.Done ? Icons.CheckCircleFill : Icons.CheckCircle, thought.Done ? Loc.T("Inte klar", "Not Done") : Loc.T("Bocka av", "Tick Off"),
                           () => App.Thoughts.SetDone(thought.Id, !thought.Done)),
            Kit.IconButton(thought.Pinned ? Icons.Unpin : Icons.Pin, thought.Pinned ? Loc.T("Lossa", "Unpin") : Loc.T("Fäst", "Pin"),
                           () => App.Thoughts.SetPinned(thought.Id, !thought.Pinned)),
            Kit.IconButton(Icons.Edit, Loc.T("Redigera", "Edit"), () => Edit(thought.Id)),
            CopyButton(thought.Text),
            Kit.IconButton(Icons.Delete, Loc.T("Ta bort", "Delete"), () => App.Thoughts.Delete(thought.Id)));
        actions.Opacity = 0;
        actions.Margin = new Thickness(8, -4, 4, -4);
        var hide = _selecting || _editing == thought.Id;
        actions.Visibility = Kit.Show(!hide);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        row.PointerEntered += (_, _) => actions.Opacity = 1;
        row.PointerExited += (_, _) => actions.Opacity = 0;
        actions.GotFocus += (_, _) => actions.Opacity = 1;

        // A day's heading already says which day; a pinned thought from another day says it itself.
        var when = !thought.Pinned || thought.Created.Date == now.Date ? Kit.Time(thought.Created) : HomePage.MeetingWhen(thought.Created, now);
        var meta = UI.HStack(7, thought.Pinned ? UI.Icon(Icons.Pin, 12, "MutedBrush") : null, UI.Text(when, 14.5, brush: "MutedBrush"));
        meta.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(meta, 3);
        row.Children.Add(meta);

        if (!_selecting)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Kit.Item(thought.Pinned ? Loc.T("Lossa", "Unpin") : Loc.T("Fäst", "Pin"), () => App.Thoughts.SetPinned(thought.Id, !thought.Pinned), Icons.Pin));
            menu.Items.Add(Kit.Item(thought.Done ? Loc.T("Inte klar", "Mark as Not Done") : Loc.T("Bocka av", "Tick Off"), () => App.Thoughts.SetDone(thought.Id, !thought.Done), Icons.CheckCircle));
            menu.Items.Add(Kit.Item(Loc.T("Redigera", "Edit"), () => Edit(thought.Id), Icons.Edit));
            menu.Items.Add(Kit.Item(Loc.T("Kopiera", "Copy"), () => App.Copy(thought.Text), Icons.Copy));
            menu.Items.Add(Kit.Item(Loc.T("Dela", "Share"), () => App.Copy(thought.Text), Icons.Share));
            menu.Items.Add(Kit.Item(Loc.T("Markera…", "Select…"), () => { Select(true); _selected.Add(thought.Id); RefreshList(); }, Icons.CheckCircle));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Kit.Item(Loc.T("Ta bort", "Delete"), () => App.Thoughts.Delete(thought.Id), Icons.Delete));
            row.ContextFlyout = menu;
        }
        return row;
    }

    private Button CopyButton(string text)
    {
        var button = Kit.IconButton(Icons.Copy, Loc.T("Kopiera", "Copy"), () => { });
        var icon = (FontIcon)((Grid)button.Content).Children[0];
        button.Click += async (_, _) =>
        {
            App.Copy(text);
            icon.Glyph = Icons.Check;
            await Task.Delay(1500);
            icon.Glyph = Icons.Copy;
        };
        return button;
    }

    // ---- Editing -------------------------------------------------------------------------

    private void Edit(Guid id)
    {
        _editing = id;
        RefreshList();
    }

    /// The thought in a field: Return saves, Shift+Return is a new line, Esc leaves it as it was.
    private TextBox Editor(Thought thought)
    {
        var box = new TextBox
        {
            Text = thought.Text, FontFamily = UI.Serif, FontSize = 19, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8),
        };
        UI.Bind(box, Control.BackgroundProperty, "FieldBrush");
        AutomationProperties.SetName(box, Loc.T("Redigera tanken", "Edit the thought"));
        var done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            _editing = null;
            if (save && box.Text.Trim() != thought.Text) App.Thoughts.Edit(thought.Id, box.Text);
            else RefreshList();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                Finish(true);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Finish(false);
            }
        };
        box.LostFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        return box;
    }

    // ---- Selecting several ------------------------------------------------------------

    private void Select(bool on)
    {
        _selecting = on;
        _selected.Clear();
        _editing = null;
        RefreshList();
    }

    private void RefreshSelectBar()
    {
        _selectBar.Visibility = Kit.Show(_selecting);
        _selectBar.Children.Clear();
        if (!_selecting) return;
        var picked = App.Thoughts.All.Where(t => _selected.Contains(t.Id)).ToList();
        var count = UI.Text(picked.Count == 1 ? Loc.T("1 markerad", "1 selected") : Loc.T($"{picked.Count} markerade", $"{picked.Count} selected"), 15, FontWeights.SemiBold);
        var merge = Kit.Chip(Loc.T("Slå ihop", "Merge"), () =>
        {
            App.Thoughts.Merge(_selected);
            Select(false);
        }, Icons.Merge);
        merge.IsEnabled = picked.Count >= 2;
        var export = Kit.Chip(Loc.T("Exportera…", "Export…"), () => Export(picked), Icons.Share);
        export.IsEnabled = picked.Count > 0;
        var delete = Kit.Chip(Loc.T("Ta bort", "Delete"), () =>
        {
            App.Thoughts.Delete(_selected.ToList());
            Select(false);
        }, Icons.Delete);
        delete.IsEnabled = picked.Count > 0;
        var all = Kit.Chip(Loc.T("Markera alla", "Select All"), () =>
        {
            foreach (var t in App.Thoughts.Search(_search.Text)) _selected.Add(t.Id);
            RefreshList();
        });
        var done = Kit.Ink(Loc.T("Klar", "Done"), () => Select(false));
        foreach (var b in new[] { merge, export, delete }) b.Opacity = b.IsEnabled ? 1 : 0.45;

        var bar = new Grid { ColumnSpacing = 10 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(count);
        var buttons = UI.HStack(10, all, merge, export, delete, done);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        _selectBar.Children.Add(Kit.Card(bar, new Thickness(22, 12, 14, 12), 16));
    }

    // ---- Export and clear ---------------------------------------------------------------

    /// Saves thoughts as Markdown — a heading per day, a bullet per thought.
    private async void Export(IReadOnlyList<Thought> thoughts)
    {
        if (thoughts.Count == 0) return;
        var picker = new FileSavePicker { SuggestedFileName = Loc.T("Tankar", "Thoughts"), SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path, ThoughtStore.Markdown(thoughts));
        if (_selecting) Select(false);
    }

    private async void ConfirmClear()
    {
        if (App.Thoughts.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = UI.Theme,
            Title = Loc.T("Ta bort alla tankar?", "Delete all thoughts?"),
            Content = Loc.T("De tas bort från din dator och går inte att få tillbaka.", "They’re removed from your PC and can’t be brought back."),
            PrimaryButtonText = Loc.T("Ta bort alla", "Delete All"),
            CloseButtonText = Loc.T("Avbryt", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Thoughts.Clear();
    }
}
