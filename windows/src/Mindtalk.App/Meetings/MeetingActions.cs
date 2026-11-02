using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>What you can do to a meeting from the list and from its page: rename, move, delete.</summary>
internal static class MeetingActions
{
    /// A one-line question: "Rename the meeting" with the current name selected. Null: cancelled.
    public static async Task<string?> Prompt(XamlRoot root, string title, string initial, string primary, string? placeholder = null)
    {
        var box = new TextBox { Text = initial, Style = UI.Style("FieldStyle"), PlaceholderText = placeholder ?? "", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = UI.Theme,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = Loc.T("Avbryt", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var accepted = false;
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            accepted = true;
            dialog.Hide();
        };
        var result = await dialog.ShowAsync();
        var text = box.Text.Trim();
        return (result == ContentDialogResult.Primary || accepted) && text.Length > 0 ? text : null;
    }

    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = UI.Theme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
            PrimaryButtonText = primary,
            CloseButtonText = Loc.T("Avbryt", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Rename(App app, XamlRoot root, Meeting meeting)
    {
        var name = await Prompt(root, Loc.T("Byt namn på mötet", "Rename the Meeting"), Format.Title(meeting), Loc.T("Byt namn", "Rename"));
        if (name is null || name == meeting.Title) return;
        meeting.Title = name;
        meeting.TitleIsAuto = false;
        app.Meetings.Save(meeting);
    }

    public static async Task Delete(App app, XamlRoot root, Meeting meeting, Action? deleted = null)
    {
        var sure = await Confirm(root, Loc.T($"Ta bort ”{Format.Title(meeting)}”?", $"Delete “{Format.Title(meeting)}”?"),
            Loc.T("Mötet flyttas till papperskorgen, med ljud, text och sammanfattning.",
                  "The meeting goes to the Recycle Bin, with its audio, text and summary."),
            Loc.T("Ta bort", "Delete"));
        if (!sure) return;
        app.Meetings.Delete(meeting.Id);
        deleted?.Invoke();
    }

    public static async Task NewFolder(App app, XamlRoot root, Meeting? moveHere = null)
    {
        var name = await Prompt(root, Loc.T("Ny mapp", "New Folder"), "", Loc.T("Skapa", "Create"), Loc.T("Kunder, projekt, team…", "Customers, projects, teams…"));
        if (name is null) return;
        app.Meetings.AddFolder(name);
        if (moveHere is not null) { moveHere.Folder = name; app.Meetings.Save(moveHere); }
    }

    /// "Move to Folder ▸": the folders, no folder, a new one.
    public static MenuFlyoutSubItem MoveMenu(App app, XamlRoot root, Meeting meeting)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.T("Flytta till mapp", "Move to Folder"), Icon = new FontIcon { Glyph = Icons.Folder } };
        foreach (var item in FolderItems(app, root, meeting)) sub.Items.Add(item);
        return sub;
    }

    public static IEnumerable<MenuFlyoutItemBase> FolderItems(App app, XamlRoot root, Meeting meeting)
    {
        foreach (var folder in app.Meetings.Folders)
        {
            var item = new ToggleMenuFlyoutItem { Text = folder, IsChecked = string.Equals(meeting.Folder, folder, StringComparison.CurrentCultureIgnoreCase) };
            item.Click += (_, _) => { meeting.Folder = folder; app.Meetings.Save(meeting); };
            yield return item;
        }
        if (meeting.Folder is not null)
        {
            var none = new MenuFlyoutItem { Text = Loc.T("Ingen mapp", "No Folder") };
            none.Click += (_, _) => { meeting.Folder = null; app.Meetings.Save(meeting); };
            yield return none;
        }
        if (app.Meetings.Folders.Count > 0) yield return new MenuFlyoutSeparator();
        var create = new MenuFlyoutItem { Text = Loc.T("Ny mapp…", "New Folder…"), Icon = new FontIcon { Glyph = Icons.NewFolder } };
        create.Click += async (_, _) => await NewFolder(app, root, meeting);
        yield return create;
    }

    /// The menu on a row in the list (right-click or ⋯).
    public static MenuFlyout RowMenu(App app, MeetingHub hub, XamlRoot root, Meeting meeting)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = Loc.T("Öppna", "Open") };
        open.Click += (_, _) => hub.OpenMeeting(meeting.Id);
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        var rename = new MenuFlyoutItem { Text = Loc.T("Byt namn…", "Rename…"), Icon = new FontIcon { Glyph = Icons.Edit } };
        rename.Click += async (_, _) => await Rename(app, root, meeting);
        menu.Items.Add(rename);
        menu.Items.Add(MoveMenu(app, root, meeting));
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = Loc.T("Ta bort…", "Delete…"), Icon = new FontIcon { Glyph = Icons.Delete } };
        delete.Click += async (_, _) => await Delete(app, root, meeting);
        menu.Items.Add(delete);
        return menu;
    }

    public static void ShowInExplorer(App app, Meeting meeting)
    {
        var target = app.Meetings.AudioPath(meeting) ?? app.Meetings.FileOf(meeting.Id);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
