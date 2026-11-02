using Mindtalk.Core.Vocabulary;

namespace Mindtalk.Core.Tests;

public sealed class VocabularyStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private string FilePath => _temp.File("vocabulary.json");

    public void Dispose() => _temp.Dispose();

    private VocabularyEntry Entry(VocabularyStore store, string word) => store.Entries.Single(e => e.Word == word);

    [Fact]
    public void FirstRunHasMindtalkOnly()
    {
        var store = new VocabularyStore(FilePath);
        Assert.Equal(["Mindtalk"], store.Entries.Select(e => e.Word));
        Assert.False(store.HasOwnWords);
        Assert.False(File.Exists(FilePath));   // nothing written until something changes
    }

    [Fact]
    public void AddPutsNewWordsFirstAndMergesTheSameWord()
    {
        var store = new VocabularyStore(FilePath);
        var changes = 0;
        store.Changed += () => changes++;

        var id = store.Add("  Anna ", "ana, hanna, , Anna");
        Assert.NotNull(id);
        Assert.Equal(["Anna", "Mindtalk"], store.Entries.Select(e => e.Word));
        Assert.Equal(["ana", "hanna"], store.Entries[0].HeardAs);   // the word itself isn't a variant
        Assert.True(store.HasOwnWords);

        // "anna" is the same entry: recased, variants merged without duplicates.
        Assert.Equal(id, store.Add("anna", "Ana, anah"));
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("anna", store.Entries[0].Word);
        Assert.Equal(["ana", "hanna", "anah"], store.Entries[0].HeardAs);
        Assert.Equal(2, changes);

        Assert.Null(store.Add(" - ", ""));
        Assert.Null(store.Add(new string('a', 61), ""));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void IsValidWord()
    {
        Assert.True(VocabularyStore.IsValidWord("Å"));
        Assert.True(VocabularyStore.IsValidWord("C3"));
        Assert.False(VocabularyStore.IsValidWord("–"));
        Assert.True(VocabularyStore.IsValidWord(new string('a', 60)));
        Assert.False(VocabularyStore.IsValidWord(new string('a', 61)));
    }

    [Fact]
    public void AddVariantSkipsDuplicatesAndTheWordItself()
    {
        var store = new VocabularyStore(FilePath);
        store.Add("Mindact", "min dag");
        var entry = Entry(store, "Mindact");
        store.AddVariant("mint act", entry);
        store.AddVariant("Min Dag", entry);
        store.AddVariant("mind-act", entry);   // same key as the word
        store.AddVariant("   ", entry);
        Assert.Equal(["min dag", "mint act"], Entry(store, "Mindact").HeardAs);
        Assert.Equal("ring Mindact", store.Apply("ring min dag"));
    }

    [Fact]
    public void RenameFixesATypoAndMergesOntoAnotherEntry()
    {
        var store = new VocabularyStore(FilePath);
        store.Add("Mindact", "min dag");
        store.Add("Mindakt", "mind act, min dag");
        store.Apply("mindakt mindakt");   // two fixes for Mindakt
        store.Apply("min dag");           // the variant: Mindakt is first in the list now
        var fixes = Entry(store, "Mindakt").Fixes + Entry(store, "Mindact").Fixes;

        store.Rename(Entry(store, "Mindakt"), "mindact");
        Assert.Equal(["mindact", "Mindtalk"], store.Entries.Select(e => e.Word));
        Assert.Equal(["min dag", "mind act"], store.Entries[0].HeardAs);
        Assert.Equal(fixes, store.Entries[0].Fixes);

        store.Rename(store.Entries[0], "Mindact");
        Assert.Equal("Mindact", store.Entries[0].Word);
        Assert.Equal("Mindact", store.Reapply("mind act"));
    }

    [Fact]
    public void RemoveCanBeUndoneInPlace()
    {
        var store = new VocabularyStore(FilePath);
        store.Add("Bert", "");
        store.Add("Anna", "");   // [Anna, Bert, Mindtalk]
        var undo = store.Remove(Entry(store, "Bert"));
        Assert.NotNull(undo);
        Assert.Equal(["Anna", "Mindtalk"], store.Entries.Select(e => e.Word));
        Assert.Equal("bert", store.Reapply("bert"));
        undo();
        Assert.Equal(["Anna", "Bert", "Mindtalk"], store.Entries.Select(e => e.Word));
        Assert.Equal("Bert", store.Reapply("bert"));
        undo();   // twice is harmless
        Assert.Equal(3, store.Entries.Count);
        Assert.Null(store.Remove(new VocabularyEntry("Nobody")));
    }

    [Fact]
    public void RemoveVariantCanBeUndoneInPlace()
    {
        var store = new VocabularyStore(FilePath);
        store.Add("Mindact", "min dag, mint act, mind akt");
        var undo = store.RemoveVariant("mint act", Entry(store, "Mindact"));
        Assert.NotNull(undo);
        Assert.Equal(["min dag", "mind akt"], Entry(store, "Mindact").HeardAs);
        undo();
        Assert.Equal(["min dag", "mint act", "mind akt"], Entry(store, "Mindact").HeardAs);
        Assert.Null(store.RemoveVariant("nope", Entry(store, "Mindact")));
    }

    [Fact]
    public void ApplyCountsReapplyAndPreviewDont()
    {
        var store = new VocabularyStore(FilePath);
        Assert.Equal("Mindtalk och Mindtalk", store.Apply("mind talk och mindtalk"));
        Assert.Equal(2, Entry(store, "Mindtalk").Fixes);
        Assert.Equal("Mindtalk", store.Reapply("mindtalk"));
        var (text, ranges) = store.Preview("i mindtalk");
        Assert.Equal("i Mindtalk", text);
        Assert.Equal([new TextSpan(2, 8)], ranges);
        Assert.Equal(2, Entry(store, "Mindtalk").Fixes);

        store.NoteHeard(["mind-talk", "unknown"]);
        Assert.Equal(3, Entry(store, "Mindtalk").Fixes);
    }

    [Fact]
    public void TheDictionaryCheckIsInjected()
    {
        var store = new VocabularyStore(FilePath);
        store.Add("Per", "");
        Assert.Equal("två gånger Per dag", store.Reapply("två gånger per dag"));
        store.IsDictionaryWord = w => w == "per";
        Assert.Equal("två gånger per dag", store.Reapply("två gånger per dag"));
    }

    [Fact]
    public void SavesAndLoadsTheList()
    {
        var store = new VocabularyStore(FilePath);
        var id = store.Add("Mindact", "min dag");
        store.Apply("min dag");
        var again = new VocabularyStore(FilePath);
        Assert.Equal(store.Entries.Select(e => (e.Id, e.Word, string.Join("|", e.HeardAs), e.Fixes)),
                     again.Entries.Select(e => (e.Id, e.Word, string.Join("|", e.HeardAs), e.Fixes)));
        Assert.Equal(id, again.Entries[0].Id);
        Assert.Equal(1, again.Entries[0].Fixes);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void MissingFieldsAreFilledIn()
    {
        File.WriteAllText(FilePath, """[{"word":"Anna"},{"word":"Bert","heardAs":["bart"],"fixes":3}]""");
        var store = new VocabularyStore(FilePath);
        Assert.Equal(["Anna", "Bert"], store.Entries.Select(e => e.Word));
        Assert.NotEqual(Guid.Empty, store.Entries[0].Id);
        Assert.Empty(store.Entries[0].HeardAs);
        Assert.Equal(3, store.Entries[1].Fixes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""[{"heardAs":["x"]}]""")]
    public void AnUnreadableFileIsKeptAndTheListStartsEmpty(string contents)
    {
        File.WriteAllText(FilePath, contents);
        var store = new VocabularyStore(FilePath);
        Assert.Empty(store.Entries);
        Assert.Equal(contents, File.ReadAllText(_temp.File("vocabulary.unreadable.json")));
    }
}
