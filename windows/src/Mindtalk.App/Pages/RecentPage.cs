using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App.Pages;

/// <summary>Recent: the last dictations, to copy again — kept only on this PC.</summary>
public sealed partial class RecentPage : PageView
{
    private readonly MainWindow _window;
    private readonly Button _clear;
    private readonly Border _list = new();
    private readonly TextBlock _note;
    private string? _key;

    public RecentPage(App app, MainWindow window) : base(app)
    {
        _window = window;
        Column.Spacing = 20;
        _clear = UI.SoftButton(Loc.T("Rensa…", "Clear…"), ConfirmClear);
        _clear.VerticalAlignment = VerticalAlignment.Bottom;
        _clear.Margin = new Thickness(0, 0, 0, 6);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.PageHeader(Loc.T("Senaste", "Recent")));
        Grid.SetColumn(_clear, 1);
        header.Children.Add(_clear);
        Add(header, 0);
        Add(_list, 1);
        _note = UI.Muted(Loc.T("Sparas bara på din dator, de senaste 50. Ctrl+Alt+V klistrar in den senaste igen.",
                               "Kept only on your PC, the last 50. Ctrl+Alt+V pastes the latest again."), 13);
        Column.Children.Add(_note);
    }

    public override void Refresh()
    {
        var entries = App.Recent.Entries;
        var key = string.Join(",", entries.Select(e => e.Id));
        if (key == _key) return;
        _key = key;
        _clear.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.Visibility = _clear.Visibility;
        if (entries.Count == 0)
        {
            var empty = UI.VStack(10, new WaveIcon(28, "FaintBrush") { HorizontalAlignment = HorizontalAlignment.Center },
                Center(UI.Text(Loc.T("Inget dikterat än", "Nothing dictated yet"), 15, FontWeights.SemiBold)),
                Center(UI.Muted(Loc.T("Det du dikterar dyker upp här, så att du kan kopiera det igen om det hamnade fel.",
                                      "What you dictate shows up here, so you can copy it again if it landed in the wrong place."), 13)));
            _list.Child = UI.Card(empty, new Thickness(40));
            return;
        }
        _list.Child = UI.Rows(entries.Select(e => (UIElement)new RecentRow(App, e)).ToArray());
    }

    private static TextBlock Center(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    private async void ConfirmClear()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = UI.Theme,
            Title = Loc.T("Rensa alla senaste dikteringar?", "Clear all recent dictations?"),
            Content = Loc.T("De tas bort från din dator och går inte att få tillbaka. Ctrl+Alt+V har sedan inget att klistra in.",
                            "They’re removed from your PC and can’t be brought back. Ctrl+Alt+V will have nothing to paste."),
            PrimaryButtonText = Loc.T("Rensa", "Clear"),
            CloseButtonText = Loc.T("Avbryt", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Recent.Clear();
    }

    /// One dictation: the text (four lines, more on request), when, and a copy button.
    private sealed partial class RecentRow : Grid
    {
        public RecentRow(App app, RecentEntry entry)
        {
            Padding = new Thickness(22, 16, 22, 16);
            ColumnSpacing = 14;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = UI.Text(entry.Text, 14, wrap: true);
            text.MaxLines = 4;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.IsTextSelectionEnabled = true;
            var when = UI.Text(RecentStore.When(entry.Date, DateTime.Now), 12, brush: "MutedBrush");
            var meta = UI.HStack(10, when);
            var lines = entry.Text.Count(c => c == '\n');
            if (entry.Text.Length > 280 || lines > 3)
            {
                var expanded = false;
                var label = UI.Text(Loc.T("Visa hela", "Show More"), 12, FontWeights.Medium, "AccentBrush");
                meta.Children.Add(UI.PlainButton(label, () =>
                {
                    expanded = !expanded;
                    text.MaxLines = expanded ? 0 : 4;
                    label.Text = expanded ? Loc.T("Visa mindre", "Show Less") : Loc.T("Visa hela", "Show More");
                }));
            }
            Children.Add(UI.VStack(5, text, meta));

            var icon = UI.Icon(Icons.Copy, 14, "PrimaryBrush");
            var copy = UI.PlainButton(icon, () => { }, Loc.T("Kopiera", "Copy"));
            AutomationProperties.SetName(copy, Loc.T($"Kopiera: {entry.Text}", $"Copy: {entry.Text}"));
            copy.Width = 18;
            copy.VerticalAlignment = VerticalAlignment.Top;
            copy.Margin = new Thickness(0, 2, 0, 0);
            copy.Click += async (_, _) =>
            {
                app.Copy(entry.Text);
                icon.Glyph = Icons.Check;
                await Task.Delay(1500);
                icon.Glyph = Icons.Copy;
            };
            Grid.SetColumn(copy, 1);
            Children.Add(copy);

            var menu = new MenuFlyout();
            var copyItem = new MenuFlyoutItem { Text = Loc.T("Kopiera", "Copy") };
            copyItem.Click += (_, _) => app.Copy(entry.Text);
            var remove = new MenuFlyoutItem { Text = Loc.T("Ta bort", "Remove") };
            remove.Click += (_, _) => app.Recent.Remove(entry.Id);
            menu.Items.Add(copyItem);
            menu.Items.Add(remove);
            ContextFlyout = menu;
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
}
