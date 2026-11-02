using Mindtalk.Core.AI;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.Tests;

public class RetrievalTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0);   // a Wednesday

    private static List<Meeting> All() =>
    [
        TestMeetings.Launch(new DateTime(2026, 11, 2, 9, 30, 0)),
        TestMeetings.Swedish("Höstbudget", new DateTime(2026, 10, 26, 10, 0, 0),
            ("S1", "Höstbudgeten håller, förutom marknadsföringen."), ("S2", "Den tar vi på veckomötet.")),
        TestMeetings.Swedish("Avstämning", new DateTime(2026, 10, 24, 15, 0, 0),
            ("S1", "Hur går det med rekryteringen till supporten?"), ("S2", "Vi har två bra kandidater.")),
    ];

    [Fact]
    public void TheBestLineComesFirst()
    {
        var hits = Retrieval.Search("Who is sending the numbers to Sara?", All(), Now);
        Assert.NotEmpty(hits);
        Assert.Equal(1100, hits[0].Passage.Time);
    }

    [Fact]
    public void AccentsAndCaseDontMatter()
    {
        var hits = Retrieval.Search("HOSTBUDGETEN", All(), Now);
        Assert.Equal("Höstbudget", hits[0].Passage.Meeting.Title);
    }

    [Fact]
    public void CompoundsAndEndingsAreLookedInto()
    {
        // "budget" is inside "höstbudgeten"; "kandidat" is "kandidater" without its ending.
        Assert.Contains(Retrieval.Search("budget", All(), Now), h => h.Passage.Meeting.Title == "Höstbudget");
        Assert.Contains(Retrieval.Search("kandidat", All(), Now), h => h.Passage.Text.Contains("kandidater"));
    }

    [Fact]
    public void WhatWasDecidedFindsDecisionsWithoutTheWord()
    {
        var hits = Retrieval.Search("What was decided?", [TestMeetings.Launch()], Now);
        Assert.Equal(700, hits[0].Passage.Time);
    }

    [Fact]
    public void ThisWeekNarrowsToThisWeeksMeetings()
    {
        var hits = Retrieval.Search("budget this week", All(), Now);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("Weekly meeting about the launch", h.Passage.Meeting.Title));
        Assert.Empty(Retrieval.Search("rekryteringen i veckan", All(), Now));
    }

    [Fact]
    public void NothingRelevantIsNothing()
    {
        Assert.Empty(Retrieval.Search("What did we say about the Eiffel tower?", All(), Now));
        Assert.Empty(Retrieval.Search("budget", [], Now));
    }

    [Fact]
    public void BackgroundSoundIsNeverFound()
    {
        var m = TestMeetings.Launch();
        m.Speakers.Add(new Speaker { Id = "S9", IsBackground = true });
        m.Segments.Add(new Segment(500, 505, "S9", "Volcano erupts in Iceland"));
        Assert.Empty(Retrieval.Search("volcano", [m], Now));
    }
}

public class MeetingAskTests
{
    [Fact]
    public async Task NothingMatchesSaysSoWithoutAskingTheModel()
    {
        var model = new ScriptedModel("should not be asked");
        var ask = new MeetingAsk(() => model);
        var en = await ask.AskAsync("What about the Eiffel tower?", [TestMeetings.Launch()]);
        var sv = await ask.AskAsync("Vad sa vi om Eiffeltornet?", [TestMeetings.Launch(), TestMeetings.Launch()]);
        Assert.Empty(model.Asked);
        Assert.Equal("Nothing in this meeting mentions that.", en.Answer);
        Assert.Equal("Inget i dina möten nämner det.", sv.Answer);
        Assert.Empty(en.Citations);
    }

    [Fact]
    public async Task CitationsBecomeMeetingsAndMoments()
    {
        var launch = TestMeetings.Launch();
        var model = new ScriptedModel { Answer = msgs => "Erik sends the numbers to Sara on Thursday [1]." };
        var partials = new List<string>();
        var exchange = await new MeetingAsk(() => model).AskAsync("Who sends the numbers to Sara?", [launch], new SyncProgress<string>(partials.Add));
        Assert.Equal("Erik sends the numbers to Sara on Thursday.", exchange.Answer);
        var c = Assert.Single(exchange.Citations);
        Assert.Equal(launch.Id, c.MeetingId);
        Assert.Equal(1100, c.Time);
        Assert.Contains("Thursday", c.Quote);
        Assert.All(partials, p => Assert.DoesNotContain("[", p));
        Assert.Contains("[1] Weekly meeting about the launch", model.Asked[0][^1].Content);
        Assert.Contains("Answer in English", model.Asked[0][^1].Content);
    }

    [Fact]
    public async Task TheModelSayingNothingMatchesIsTheNothingAnswer()
    {
        var model = new ScriptedModel(MeetingAsk.NotFound);
        var exchange = await new MeetingAsk(() => model).AskAsync("Vad bestämdes om budgeten?", [TestMeetings.Launch()]);
        Assert.Equal("Inget i det här mötet nämner det.", exchange.Answer);
        Assert.Empty(exchange.Citations);
    }

    [Fact]
    public void MarkersAreMappedInOrderAndStripped()
    {
        var hits = Retrieval.Search("downloads meetings feature budget", [TestMeetings.Launch()], DateTime.Now);
        var citations = MeetingAsk.Citations("A [2]. B [1, 3]. C [2-3]. D [99].", hits);
        Assert.Equal([hits[1].Passage.Time, hits[0].Passage.Time, hits[2].Passage.Time], citations.Select(c => c.Time));
        Assert.Equal("A. B. C. D.", MeetingAsk.StripMarkers("A [2]. B [1, 3]. C [2-3]. D [99].", streaming: false));
        Assert.Equal("Erik will send it", MeetingAsk.StripMarkers("Erik will send it [1", streaming: true));
    }

    [Fact]
    public async Task WithoutAWriterThePassagesAreTheAnswer()
    {
        var exchange = await new MeetingAsk(() => null).AskAsync("autumn budget", [TestMeetings.Launch()]);
        Assert.StartsWith("Here's what your meetings say:", exchange.Answer);
        Assert.NotEmpty(exchange.Citations);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
