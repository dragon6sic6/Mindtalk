using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;
using Mindtalk.Core.Voices;

namespace Mindtalk.Core.Tests;

[Collection("Loc")]
public sealed class FoundationStoreTests
{
    [Fact]
    public void Thoughts_are_kept_newest_first_and_merge_oldest_first()
    {
        using var temp = new TempFolder();
        var store = new ThoughtStore(temp.File("thoughts.json"));
        var a = store.Add("Book a room", new DateTime(2026, 10, 1, 8, 40, 0))!;
        var b = store.Add("Call back about the quote", new DateTime(2026, 10, 1, 10, 12, 0))!;
        Assert.Equal(b.Id, store.All[0].Id);

        var merged = store.Merge([a.Id, b.Id])!;
        Assert.Equal("Book a room\nCall back about the quote", merged.Text);
        Assert.Single(new ThoughtStore(temp.File("thoughts.json")).All);
    }

    [Fact]
    public void Meetings_round_trip_and_search_ignores_accents()
    {
        using var temp = new TempFolder();
        var store = new MeetingStore(temp.Path);
        var meeting = new Meeting { Title = "Höstbudget", State = MeetingState.Done };
        meeting.Speakers.Add(new Speaker { Id = "S1", Name = "Anna" });
        meeting.Segments.Add(new Segment(2.5, 6, "S1", "Vi skär i marknadsföringen."));
        store.Save(meeting);

        var again = new MeetingStore(temp.Path);
        Assert.Equal("Höstbudget", again.Get(meeting.Id)!.Title);
        Assert.Contains(again.Search("hostbudget"), h => h.Kind == HitKind.Title);
        var hit = Assert.Single(again.Search("marknadsforingen"));
        Assert.Equal(2.5, hit.Time);
    }

    [Fact]
    public void An_unfinished_recording_is_closed_on_load()
    {
        using var temp = new TempFolder();
        var store = new MeetingStore(temp.Path);
        var meeting = new Meeting { State = MeetingState.Recording };
        meeting.Segments.Add(new Segment(0, 1, "S1", "Hej"));
        store.Save(meeting);
        Assert.Equal(MeetingState.Done, new MeetingStore(temp.Path).Get(meeting.Id)!.State);
    }

    [Fact]
    public void People_line_names_everyone_or_counts()
    {
        using var _ = Language.English();
        var m = new Meeting();
        m.Speakers.AddRange([new Speaker { Id = "S1", Name = "Anna" }, new Speaker { Id = "S2", Name = "Erik" }, new Speaker { Id = "S3", Name = "Sara" }]);
        m.Segments.AddRange([new Segment(0, 1, "S1", "a"), new Segment(1, 2, "S2", "b"), new Segment(2, 3, "S3", "c")]);
        Assert.Equal("Anna, Erik and Sara", m.PeopleLine);
        m.Speakers[2].Name = null;
        Assert.Equal("3 speakers", m.PeopleLine);
    }

    [Fact]
    public void Voices_match_by_cosine_and_blend()
    {
        using var temp = new TempFolder();
        var store = new VoiceStore(temp.File("voices.json"));
        var anna = store.Remember("Anna", [1, 0, 0]);
        store.Remember("Erik", [0, 1, 0]);
        Assert.Equal(anna.Id, store.Match([0.9f, 0.1f, 0])!.Value.Voice.Id);
        Assert.Null(store.Match([0, 0, 1]));
        store.Remember("anna", [0.8f, 0.2f, 0]);
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public void Name_suggestions_complete_ignoring_case_and_accents()
    {
        var known = NameSuggestions.Known(["Anna", "Örjan"], ["Erik", "anna", null], "Johan");
        Assert.Equal(4, known.Count);
        Assert.Equal("Anna", NameSuggestions.Complete("an", known));
        Assert.Equal("Örjan", NameSuggestions.Complete("or", known));
        Assert.Null(NameSuggestions.Complete("Anna", known));
    }

    [Fact]
    public void Shortcut_parts_read_like_keys()
    {
        using var _ = Language.English();
        Assert.Equal("Ctrl+Alt+M", Input.Shortcut.Meeting.ToString());
    }
}
