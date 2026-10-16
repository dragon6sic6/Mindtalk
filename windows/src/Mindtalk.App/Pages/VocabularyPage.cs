using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Vocabulary;
using Windows.System;

namespace Mindtalk.App.Pages;

/// <summary>Vocabulary: names and words Mindtalk should spell your way.</summary>
public sealed partial class VocabularyPage : PageView
{
    private readonly TextBox _word, _heardAs, _sample, _search;
    private readonly Button _add;
    private readonly Border _list = new();
    private readonly StackPanel _preview;
    private readonly RichTextBlock _previewText = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly Stack<Action> _undo = new();
    private Guid? _highlighted;
    private string? _key;

    public VocabularyPage(App app) : base(app)
    {
        Column.Spacing = 14;
        var header = UI.PageHeader(Loc.T("Ordlista", "Vocabulary"),
            Loc.T("Namn och ord som Mindtalk ska stava som du vill. Delade eller felskrivna varianter – som ”Mind Talk” – rättas automatiskt.",
                  "Names and words Mindtalk should spell your way. Split or misspelled variants – like “Mind Talk” – are fixed automatically."));
        header.Margin = new Thickness(0, 0, 0, 12);
        Add(header, 0);

        // Add: a word, and what Mindtalk often hears instead.
        Add(UI.SectionTitle(Loc.T("Lägg till", "Add")), 0);
        _word = Field(Loc.T("t.ex. Mindtalk", "e.g. Mindtalk"));
        _heardAs = Field(Loc.T("t.ex. mind talk, min tal", "e.g. mind talk, mine talk"));
        _add = UI.InkButton(Loc.T("Lägg till", "Add"), AddWord);
        _add.VerticalAlignment = VerticalAlignment.Bottom;
        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var word = Labeled(Loc.T("Ord", "Word"), _word);
        var heard = Labeled(Loc.T("Hörs ofta som (valfritt)", "Often heard as (optional)"), _heardAs);
        Grid.SetColumn(heard, 1);
        Grid.SetColumn(_add, 2);
        fields.Children.Add(word);
        fields.Children.Add(heard);
        fields.Children.Add(_add);
        Add(UI.Card(fields, new Thickness(20)), 0);
        foreach (var box in new[] { _word, _heardAs })
        {
            box.TextChanged += (_, _) => _add.IsEnabled = VocabularyStore.IsValidWord(_word.Text);
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; AddWord(); } };
        }
        _add.IsEnabled = false;

        // Your words, searchable once there are many.
        _search = Field(Loc.T("Sök", "Search"));
        _search.Width = 180;
        _search.Padding = new Thickness(10, 5, 10, 5);
        _search.TextChanged += (_, _) => { _key = null; Refresh(); };
        var yours = new Grid();
        yours.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        yours.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        yours.Children.Add(UI.SectionTitle(Loc.T("Dina ord", "Your words")));
        Grid.SetColumn(_search, 1);
        yours.Children.Add(_search);
        Column.Children.Add(yours);
        Column.Children.Add(_list);

        // Try it: the text as it would come out, fixes in bold.
        Add(UI.SectionTitle(Loc.T("Prova ordlistan", "Try your vocabulary")), 1);
        _sample = Field(Loc.T("Skriv en mening som Mindtalk kunde ha hört, t.ex. ”jag använder mind talk”",
                              "Type a sentence Mindtalk might have heard, e.g. “I use mind talk”"));
        _sample.Padding = new Thickness(12, 9, 12, 9);
        _preview = UI.HStack(8, UI.Icon("", 13, "MutedBrush"), _previewText);
        _preview.Visibility = Visibility.Collapsed;
        _sample.TextChanged += (_, _) => ShowPreview();
        Add(UI.Card(UI.VStack(12, _sample, _preview), new Thickness(20)), 1);

        var note = UI.Muted(Loc.T("Varianter rättas alltid – lägg bara till sådant du inte också säger på riktigt. Skilj flera varianter med komma. Vanliga ord (som ”per” eller ”test”) och text i versaler lämnas orörda.",
                                  "Variants are always corrected – only add what you don’t also say for real. Separate several with commas. Ordinary words (like “per” or “test”) and text in capitals are left alone."), 12);
        note.Margin = new Thickness(0, 4, 0, 0);
        Column.Children.Add(note);

        // Ctrl+Z puts back what was just removed.
        var undo = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        undo.Invoked += (_, e) =>
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox || _undo.Count == 0) return;
            e.Handled = true;
            _undo.Pop()();
        };
        KeyboardAccelerators.Add(undo);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private static TextBox Field(string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, Style = UI.Style("FieldStyle") };
        return box;
    }

    private static StackPanel Labeled(string label, TextBox box) =>
        UI.VStack(6, UI.Text(label, 12, FontWeights.SemiBold, "MutedBrush"), box);

    private void AddWord()
    {
        if (!VocabularyStore.IsValidWord(_word.Text)) return;
        var id = App.Vocabulary.Add(_word.Text, _heardAs.Text);
        _word.Text = _heardAs.Text = _search.Text = "";
        _word.Focus(FocusState.Programmatic);
        // Show where it went — also when it was merged into an existing word.
        _highlighted = id;
        _key = null;
        Refresh();
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1400);
            if (_highlighted != id) return;
            _highlighted = null;
            _key = null;
            Refresh();
        });
    }

    public void Remember(Action? undo)
    {
        if (undo is not null) _undo.Push(undo);
    }

    public override void Refresh()
    {
        var entries = App.Vocabulary.Entries;
        _search.Visibility = entries.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
        var query = _search.Visibility == Visibility.Visible ? _search.Text.Trim() : "";
        var shown = query.Length == 0 ? entries
            : entries.Where(e => e.Word.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                              || e.HeardAs.Any(v => v.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var key = string.Join("|", shown.Select(e => $"{e.Id}:{e.Word}:{string.Join(",", e.HeardAs)}:{e.Fixes}")) + $"#{query}#{_highlighted}";
        if (key == _key) return;
        _key = key;

        if (entries.Count == 0) _list.Child = Note(Loc.T("Inga ord än. Lägg till namn och begrepp du ofta säger.", "No words yet. Add names and terms you often say."));
        else if (shown.Count == 0) _list.Child = Note(Loc.T($"Inget ord matchar ”{query}”.", $"No word matches “{query}”."));
        else _list.Child = UI.Rows(shown.Select(e => (UIElement)new VocabularyRow(App, this, e, e.Id == _highlighted)).ToArray());
        ShowPreview();
    }

    private static Border Note(string text)
    {
        var note = UI.Muted(text, 13);
        note.Margin = new Thickness(22, 18, 22, 18);
        return UI.Card(note);
    }

    /// The sample with every fixed word in bold.
    private void ShowPreview()
    {
        _preview.Visibility = _sample.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_sample.Text.Length == 0) return;
        var (text, ranges) = App.Vocabulary.Preview(_sample.Text);
        var paragraph = new Paragraph();
        var at = 0;
        foreach (var range in ranges)
        {
            if (range.Start > at) paragraph.Inlines.Add(new Run { Text = text[at..range.Start] });
            var fix = new Run { Text = text.Substring(range.Start, range.Length), FontWeight = FontWeights.SemiBold };
            fix.Foreground = UI.Brush("AccentBrush");
            paragraph.Inlines.Add(fix);
            at = range.Start + range.Length;
        }
        if (at < text.Length) paragraph.Inlines.Add(new Run { Text = text[at..] });
        _previewText.Blocks.Clear();
        _previewText.Blocks.Add(paragraph);
        _previewText.Foreground = UI.Brush("PrimaryBrush");
    }

    /// <summary>One word: its spelling (double-click to edit), what it's heard as, how often it fixed something.</summary>
    private sealed partial class VocabularyRow : Grid
    {
        private readonly App _app;
        private readonly VocabularyPage _page;
        private readonly VocabularyEntry _entry;
        private readonly Grid _wordHost = new();
        private readonly FlowPanel _variants = new() { Spacing = 6 };
        private readonly Button _pencil, _trash;

        public VocabularyRow(App app, VocabularyPage page, VocabularyEntry entry, bool highlighted)
        {
            _app = app;
            _page = page;
            _entry = entry;
            Padding = new Thickness(22, 14, 22, 14);
            ColumnSpacing = 14;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Background = highlighted ? UI.Brush("ChipBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (highlighted) Motion.PageIn(this);

            ShowWord();
            BuildVariants(adding: false);
            Children.Add(UI.VStack(8, _wordHost, _variants));

            if (entry.Fixes > 0)
            {
                var fixes = UI.Text(entry.Fixes == 1 ? Loc.T("Rättat 1 gång", "Fixed once") : Loc.T($"Rättat {entry.Fixes} gånger", $"Fixed {entry.Fixes} times"), 12, brush: "MutedBrush");
                fixes.VerticalAlignment = VerticalAlignment.Top;
                fixes.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetColumn(fixes, 1);
                Children.Add(fixes);
            }
            _pencil = UI.PlainButton(UI.Icon(Icons.Pencil, 13, "MutedBrush"), StartRename, Loc.T($"Ändra {entry.Word}", $"Edit {entry.Word}"));
            _trash = UI.PlainButton(UI.Icon(Icons.Trash, 13, "MutedBrush"), Remove, Loc.T($"Ta bort {entry.Word} (Ctrl+Z ångrar)", $"Remove {entry.Word} (Ctrl+Z undoes)"));
            foreach (var (button, column) in new[] { (_pencil, 2), (_trash, 3) })
            {
                button.VerticalAlignment = VerticalAlignment.Top;
                button.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetColumn(button, column);
                Children.Add(button);
            }
            Hover(false);
            PointerEntered += (_, _) => Hover(true);
            PointerExited += (_, _) => Hover(false);
        }

        private void Hover(bool on)
        {
            _pencil.Opacity = on ? 1 : 0;
            _trash.Opacity = on ? 1 : 0.7;
        }

        private void ShowWord()
        {
            var word = UI.Text(_entry.Word, 15, FontWeights.SemiBold);
            ToolTipService.SetToolTip(word, Loc.T("Dubbelklicka för att ändra", "Double-click to edit"));
            word.DoubleTapped += (_, _) => StartRename();
            _wordHost.Children.Clear();
            _wordHost.Children.Add(word);
        }

        private void StartRename()
        {
            var box = new TextBox { Text = _entry.Word, FontSize = 14, FontWeight = FontWeights.SemiBold, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            var done = false;
            void Commit(bool keep)
            {
                if (done) return;
                done = true;
                if (keep && box.Text != _entry.Word) _app.Vocabulary.Rename(_entry, box.Text);
                else ShowWord();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { e.Handled = true; Commit(true); }
                if (e.Key == VirtualKey.Escape) { e.Handled = true; Commit(false); }
            };
            // Clicking elsewhere ends editing.
            box.LostFocus += (_, _) => Commit(true);
            _wordHost.Children.Clear();
            _wordHost.Children.Add(box);
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        }

        private void BuildVariants(bool adding)
        {
            _variants.Children.Clear();
            if (_entry.HeardAs.Count > 0) _variants.Children.Add(UI.Text(Loc.T("hörs ofta som", "often heard as"), 12, brush: "MutedBrush"));
            foreach (var variant in _entry.HeardAs) _variants.Children.Add(Chip(variant));
            if (adding)
            {
                var box = new TextBox { PlaceholderText = "variant", FontSize = 12, Width = 150, Padding = new Thickness(8, 3, 8, 3), MinHeight = 0 };
                var done = false;
                void Commit()
                {
                    if (done) return;
                    done = true;
                    var added = false;
                    foreach (var v in box.Text.Split(',')) { _app.Vocabulary.AddVariant(v, _entry); added = true; }
                    if (!added || box.Text.Trim().Length == 0) BuildVariants(false);
                }
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter) { e.Handled = true; Commit(); }
                    if (e.Key == VirtualKey.Escape) { e.Handled = true; done = true; BuildVariants(false); }
                };
                box.LostFocus += (_, _) => Commit();
                _variants.Children.Add(box);
                box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
                return;
            }
            var add = UI.PlainButton(UI.HStack(4, UI.Icon(Icons.Plus, 10, "MutedBrush"), UI.Text(Loc.T("Variant", "Variant"), 12, FontWeights.Medium, "MutedBrush")),
                () => BuildVariants(true), Loc.T("Lägg till något Mindtalk ofta hör i stället", "Add something Mindtalk often hears instead"));
            add.Opacity = _entry.HeardAs.Count == 0 ? 1 : 0.6;
            PointerEntered += (_, _) => add.Opacity = 1;
            PointerExited += (_, _) => add.Opacity = _entry.HeardAs.Count == 0 ? 1 : 0.6;
            _variants.Children.Add(add);
        }

        private Border Chip(string variant)
        {
            var x = UI.PlainButton(UI.Icon(Icons.XMark, 8, "MutedBrush"), () => _page.Remember(_app.Vocabulary.RemoveVariant(variant, _entry)),
                                   Loc.T($"Ta bort ”{variant}” (Ctrl+Z ångrar)", $"Remove “{variant}” (Ctrl+Z undoes)"));
            x.Width = x.Height = 16;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 2, 3, 2),
                Child = UI.HStack(5, UI.Text(variant, 12, FontWeights.Medium), x),
            };
            UI.Bind(chip, Border.BackgroundProperty, "ChipBrush");
            return chip;
        }

        private void Remove() => _page.Remember(_app.Vocabulary.Remove(_entry));
    }
}
