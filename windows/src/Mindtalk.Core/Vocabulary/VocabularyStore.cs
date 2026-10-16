using System.Text.Json;

namespace Mindtalk.Core.Vocabulary;

/// <summary>A name or word Mindtalk should spell your way.</summary>
/// <param name="HeardAs">What the speech model writes instead ("min dag" for Mindact).</param>
/// <param name="Fixes">How many times it has corrected something.</param>
public sealed record VocabularyEntry(Guid Id, string Word, IReadOnlyList<string> HeardAs, int Fixes = 0)
{
    public VocabularyEntry(string word, IReadOnlyList<string>? heardAs = null) : this(Guid.NewGuid(), word, heardAs ?? [], 0) { }
}

/// <summary>
/// The Ordlista: names and words fixed in the text after transcription (matching is in
/// <see cref="VocabularyMatcher"/>). Kept in %APPDATA%\Mindtalk\vocabulary.json, never
/// anywhere else. Call it on the UI thread.
/// </summary>
public sealed class VocabularyStore
{
    public static string DefaultPath { get; } = Path.Combine(AppSettings.Folder, "vocabulary.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private List<VocabularyEntry> _entries;
    /// Built once per change of the list, not per dictation.
    private VocabularyMatcher _matcher;

    public VocabularyStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _entries = Load(_path);
        _matcher = MatcherFor(_entries);
    }

    public IReadOnlyList<VocabularyEntry> Entries => _entries;

    /// An ordinary Swedish or English word — then it isn't recased or "fixed". The Mac asks
    /// its spell checker; Windows plugs one in here.
    public Func<string, bool> IsDictionaryWord { get; set; } = _ => false;

    /// Raised after every change to the list.
    public event Action? Changed;

    // ---- Storage --------------------------------------------------------------

    /// Every field but the word may be missing — so adding a field later never wipes anyone's list.
    private sealed class StoredEntry
    {
        public Guid? Id { get; set; }
        public string? Word { get; set; }
        public List<string>? HeardAs { get; set; }
        public int? Fixes { get; set; }
    }

    private static List<VocabularyEntry> Load(string path)
    {
        if (!File.Exists(path)) return [new VocabularyEntry("Mindtalk")];
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(path), Json)
                         ?? throw new JsonException("null");
            if (stored.Any(e => e is null || e.Word is null)) throw new JsonException("an entry without a word");
            return stored.Select(e => new VocabularyEntry(e.Id ?? Guid.NewGuid(), e.Word!, e.HeardAs ?? [], e.Fixes ?? 0)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable: keep a copy rather than lose it, and start afresh.
            try { File.Copy(path, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "vocabulary.unreadable.json"), overwrite: true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            return [];
        }
    }

    private void Save()
    {
        var stored = _entries.Select(e => new StoredEntry { Id = e.Id, Word = e.Word, HeardAs = [.. e.HeardAs], Fixes = e.Fixes });
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(stored, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // kept in memory; saved next change
    }

    private static VocabularyMatcher MatcherFor(IEnumerable<VocabularyEntry> entries) =>
        new(entries.Select(e => (e.Word, e.HeardAs)));

    /// The Mac's didSet: save, rebuild the matcher only if words or variants changed, tell the UI.
    private void Set(List<VocabularyEntry> updated)
    {
        var old = _entries;
        _entries = updated;
        Save();
        var sameWords = old.Count == updated.Count && old.Zip(updated).All(p =>
            p.First.Word == p.Second.Word && p.First.HeardAs.SequenceEqual(p.Second.HeardAs));
        if (!sameWords) _matcher = MatcherFor(updated);
        Changed?.Invoke();
    }

    // ---- Editing ----------------------------------------------------------------

    /// "Mind Talk", "mind-talk" and "Mindtalk" are the same entry.
    private static string Key(string word) =>
        string.Concat(TextElements.Of(word.ToLowerInvariant()).Where(TextElements.IsLetterOrNumber));

    public static bool IsValidWord(string word) =>
        TextElements.Of(word).Any(TextElements.IsLetterOrNumber) && TextElements.Count(word) <= 60;

    /// <summary>
    /// Adds a word, or more "heard as" variants to the same word. <paramref name="heardAs"/> is
    /// comma-separated. Returns the entry's id (to highlight it), or null if the word isn't valid.
    /// </summary>
    public Guid? Add(string word, string heardAs)
    {
        word = word.Trim();
        if (!IsValidWord(word)) return null;
        var variants = heardAs.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => IsValidWord(v) && Key(v) != Key(word))
            .ToList();
        var updated = _entries.ToList();
        var i = updated.FindIndex(e => Key(e.Word) == Key(word));
        if (i >= 0)
        {
            var merged = updated[i].HeardAs.ToList();
            foreach (var v in variants)
                if (!merged.Any(h => Key(h) == Key(v))) merged.Add(v);
            updated[i] = updated[i] with { Word = word, HeardAs = merged };
            Set(updated);
            return updated[i].Id;
        }
        var entry = new VocabularyEntry(word, variants);
        updated.Insert(0, entry);
        Set(updated);
        return entry.Id;
    }

    public void AddVariant(string variant, VocabularyEntry entry)
    {
        var v = variant.Trim();
        var i = _entries.FindIndex(e => e.Id == entry.Id);
        if (!IsValidWord(v) || i < 0 || Key(v) == Key(_entries[i].Word)
            || _entries[i].HeardAs.Any(h => Key(h) == Key(v))) return;
        var updated = _entries.ToList();
        updated[i] = updated[i] with { HeardAs = [.. updated[i].HeardAs, v] };
        Set(updated);
    }

    /// <summary>Fixes a typo in a word, keeping its variants and count. Renaming onto another entry merges the two.</summary>
    public void Rename(VocabularyEntry entry, string word)
    {
        word = word.Trim();
        var i = _entries.FindIndex(e => e.Id == entry.Id);
        if (!IsValidWord(word) || i < 0) return;
        var updated = _entries.ToList();
        var j = updated.FindIndex(e => e.Id != entry.Id && Key(e.Word) == Key(word));
        if (j >= 0)
        {
            var merged = updated[j].HeardAs.ToList();
            foreach (var v in updated[i].HeardAs)
                if (!merged.Any(h => Key(h) == Key(v))) merged.Add(v);
            updated[j] = updated[j] with { HeardAs = merged, Fixes = updated[j].Fixes + updated[i].Fixes, Word = word };
            updated.RemoveAt(i);
        }
        else
        {
            updated[i] = updated[i] with { Word = word };
        }
        Set(updated);
    }

    /// <summary>
    /// Removes an entry. Returns the undo (Ctrl+Z puts it back where it was), or null if it
    /// wasn't on the list.
    /// </summary>
    public Action? Remove(VocabularyEntry entry)
    {
        var i = _entries.FindIndex(e => e.Id == entry.Id);
        if (i < 0) return null;
        var removed = _entries[i];
        var updated = _entries.ToList();
        updated.RemoveAt(i);
        Set(updated);
        return () =>
        {
            if (_entries.Any(e => e.Id == removed.Id)) return;
            var restored = _entries.ToList();
            restored.Insert(Math.Min(i, restored.Count), removed);
            Set(restored);
        };
    }

    /// <summary>Removes one "heard as" variant. Returns the undo, or null if there was no such variant.</summary>
    public Action? RemoveVariant(string variant, VocabularyEntry entry)
    {
        var i = _entries.FindIndex(e => e.Id == entry.Id);
        if (i < 0) return null;
        var v = _entries[i].HeardAs.ToList().IndexOf(variant);
        if (v < 0) return null;
        var updated = _entries.ToList();
        var heard = updated[i].HeardAs.ToList();
        heard.RemoveAt(v);
        updated[i] = updated[i] with { HeardAs = heard };
        Set(updated);
        var id = entry.Id;
        return () =>
        {
            var k = _entries.FindIndex(e => e.Id == id);
            if (k < 0) return;
            var restored = _entries.ToList();
            var list = restored[k].HeardAs.ToList();
            list.Insert(Math.Min(v, list.Count), variant);
            restored[k] = restored[k] with { HeardAs = list };
            Set(restored);
        };
    }

    // ---- Fixing text ------------------------------------------------------------

    /// <summary>Terms the speech model was made to hear: counted as fixes too.</summary>
    public void NoteHeard(IEnumerable<string> terms)
    {
        var list = terms.ToList();
        if (list.Count == 0) return;
        var updated = _entries.ToList();
        foreach (var term in list)
        {
            var i = updated.FindIndex(e => Key(e.Word) == Key(term));
            if (i >= 0) updated[i] = updated[i] with { Fixes = updated[i].Fixes + 1 };
        }
        Set(updated);
    }

    /// <summary>
    /// A word you added yourself — the preset "Mindtalk" is fixed by the rules alone and
    /// doesn't need the listener.
    /// </summary>
    public bool HasOwnWords => _entries.Any(e => Key(e.Word) != "mindtalk");

    /// <summary>Fixes the text and counts what it fixed.</summary>
    public string Apply(string text)
    {
        var result = _matcher.Apply(text, IsDictionaryWord);
        if (result.Counts.Count > 0)
        {
            var updated = _entries.ToList();
            foreach (var (i, n) in result.Counts)
                if (i >= 0 && i < updated.Count) updated[i] = updated[i] with { Fixes = updated[i].Fixes + n };
            Set(updated);
        }
        return result.Text;
    }

    /// <summary>Fixes the text again without counting — after the polish, which may have undone a spelling.</summary>
    public string Reapply(string text) => _matcher.Apply(text, IsDictionaryWord).Text;

    /// <summary>For the page's preview: the fixed text and where the fixes are.</summary>
    public (string Text, IReadOnlyList<TextSpan> Ranges) Preview(string text)
    {
        var result = _matcher.Apply(text, IsDictionaryWord);
        return (result.Text, result.Ranges);
    }
}
