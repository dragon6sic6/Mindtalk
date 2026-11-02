using Mindtalk.Core.AI;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.Tests;

public class LenientJsonTests
{
    [Fact]
    public void ReadsJsonInFencesWithTextAround()
    {
        var o = LenientJson.ParseObject("Here you go:\n```json\n{\"overview\": \"Hi\", \"n\": 12}\n```\nHope it helps!");
        Assert.Equal("Hi", (string?)o!["overview"]);
        Assert.Equal(12, (int)o["n"]!);
    }

    [Fact]
    public void MendsTrailingCommasCurlyQuotesAndMissingCommas()
    {
        var o = LenientJson.ParseObject("{“a”: “x”, \"b\": [1, 2, 30,], \"c\": \"y\" \"d\": true,}");
        Assert.NotNull(o);
        Assert.Equal("x", (string?)o!["a"]);
        Assert.Equal(30, (int)o["b"]![2]!);
        Assert.True((bool)o["d"]!);
    }

    [Fact]
    public void ClosesAnAnswerCutOffMidway()
    {
        var o = LenientJson.ParseObject("{\"overview\": \"Done.\", \"decisions\": [{\"text\": \"Ship it\", \"line\": 3}, {\"text\": \"Half a sen");
        Assert.NotNull(o);
        var decisions = o!["decisions"]!.AsArray();
        Assert.Single(decisions);
        Assert.Equal("Ship it", (string?)decisions[0]!["text"]);
    }

    [Fact]
    public void NothingUsableIsNull()
    {
        Assert.Null(LenientJson.ParseObject("I can't help with that."));
        Assert.Null(LenientJson.ParseObject(""));
    }

    [Fact]
    public void DraftReadsOtherNamesAndLinesAsText()
    {
        var d = DraftSummary.Parse("""{"summary": "S", "topics": [{"heading": "T", "items": [{"point": "P", "line": "L7", "quote": "q"}]}], "tasks": [{"task": "Do", "owner": "none", "line": 2}]}""");
        Assert.Equal("S", d!.Overview);
        Assert.Equal(7, d.Topics[0].Points[0].Line);
        Assert.Null(d.Todos[0].Owner);
    }
}

public class SummaryGroundingTests
{
    private static readonly Meeting Launch = TestMeetings.Launch();
    private static readonly List<TranscriptLine> Lines = SummaryGrounding.Lines(Launch);

    private static MeetingSummary Ground(string json) => SummaryGrounding.Ground(DraftSummary.Parse(json)!, Launch, Lines);

    [Fact]
    public void KeepsItemsWhoseQuoteIsInTheTranscriptWithTheirTimeAndLine()
    {
        // Wrong line number (off by one) and a word dropped: still found.
        var s = Ground("""{"overview":"o","topics":[{"title":"Launch","points":[{"text":"Record first day of downloads","line":3,"quote":"more downloads on first day than in all last month"}]}]}""");
        var item = Assert.Single(Assert.Single(s.Topics).Items);
        Assert.Equal(153, item.Time);
        Assert.Equal("S2", item.SpeakerId);
        Assert.Equal("We had more downloads on the first day than in all of last month.", item.Quote);
    }

    [Fact]
    public void DropsItemsWhoseQuoteIsntThere()
    {
        var s = Ground("""{"overview":"o","topics":[{"title":"Launch","points":[{"text":"Revenue doubled","line":2,"quote":"revenue doubled in October"}]}],"remember":[{"text":"Paris office opens","line":4,"quote":"the Paris office opens in May"}]}""");
        Assert.Empty(s.Topics);
        Assert.Empty(s.Remember);
    }

    [Fact]
    public void DropsARealQuoteUnderAnUnrelatedSentence()
    {
        var s = Ground("""{"overview":"o","remember":[{"text":"Hiring is frozen","line":7,"quote":"Yes, absolutely."}]}""");
        Assert.Empty(s.Remember);
    }

    [Fact]
    public void DecisionsMustComeFromADecidingSentence()
    {
        var s = Ground("""
            {"overview":"o","decisions":[
            {"text":"Build the meetings feature next","line":6,"quote":"Then we build the meetings feature next"},
            {"text":"Support answers in English","line":4,"quote":"Support is already getting questions in English"}]}
            """);
        var d = Assert.Single(s.Decisions);
        Assert.Equal(700, d.Time);
    }

    [Fact]
    public void ANegatedDecisionIsntOne()
    {
        var m = TestMeetings.Swedish("x", DateTime.Now.AddDays(-1), ("S1", "We don't need to decide everything about the party today."));
        var lines = SummaryGrounding.Lines(m);
        var s = SummaryGrounding.Ground(DraftSummary.Parse("""{"decisions":[{"text":"Not everything about the party is decided today","line":1,"quote":"We don't need to decide everything about the party"}]}""")!, m, lines);
        Assert.Empty(s.Decisions);
    }

    [Fact]
    public void ADecisionMarkMakesALineADecision()
    {
        var m = TestMeetings.Swedish("x", DateTime.Now.AddDays(-1), ("S1", "Höstens konferens hålls i Göteborg."));
        m.Marks.Add(new MeetingMark(2, MarkKind.Decision));
        var s = SummaryGrounding.Ground(DraftSummary.Parse("""{"decisions":[{"text":"Konferensen hålls i Göteborg","line":1,"quote":"konferens hålls i Göteborg"}]}""")!, m, SummaryGrounding.Lines(m));
        Assert.Single(s.Decisions);
    }

    [Fact]
    public void ToDosNeedAPromiseAndOwnersMustBeInTheMeeting()
    {
        var s = Ground("""
            {"overview":"o","todos":[
            {"text":"Send the numbers to Sara on Thursday","owner":"Sara","line":9,"quote":"I'll pull the numbers together and send them to Sara"},
            {"text":"Answer support in English","owner":"Bob","line":4,"quote":"Support is already getting questions in English"}]}
            """);
        var t = Assert.Single(s.Todos);
        // "I'll …" said by Erik: Erik owns it, whatever the model guessed.
        Assert.Equal("Erik", t.Owner);
        Assert.Equal(1100, t.Time);
    }

    [Fact]
    public void AnOwnerWhoIsntInTheMeetingIsNobody()
    {
        var m = TestMeetings.Swedish("x", DateTime.Now.AddDays(-1), ("S1", "Kan du skicka offerten till kunden?"), ("S2", "Ja."));
        var s = SummaryGrounding.Ground(DraftSummary.Parse("""{"todos":[{"text":"Skicka offerten","owner":"Bosse","line":1,"quote":"skicka offerten till kunden"}]}""")!, m, SummaryGrounding.Lines(m));
        Assert.Null(Assert.Single(s.Todos).Owner);
    }

    [Fact]
    public void BackgroundSoundIsLeftOut()
    {
        var m = TestMeetings.Launch();
        m.Speakers.Add(new Speaker { Id = "S9", IsBackground = true });
        m.Segments.Add(new Segment(500, 505, "S9", "Breaking news: the stock market fell sharply today."));
        var lines = SummaryGrounding.Lines(m);
        Assert.DoesNotContain(lines, l => l.Segment.SpeakerId == "S9");
        var s = SummaryGrounding.Ground(DraftSummary.Parse("""{"remember":[{"text":"The stock market fell","quote":"the stock market fell sharply today"}]}""")!, m, lines);
        Assert.Empty(s.Remember);
    }

    [Fact]
    public void VocabularyIsAppliedToTheText()
    {
        var s = SummaryGrounding.Ground(DraftSummary.Parse("""{"overview":"Mind Talk launched."}""")!, Launch, Lines, t => t.Replace("Mind Talk", "Mindtalk"));
        Assert.Equal("Mindtalk launched.", s.Overview);
    }
}

public class MeetingSummarizerTests
{
    private const string Good = """
        {"overview":"The launch went well.","topics":[{"title":"The launch","points":[{"text":"Record downloads","line":2,"quote":"more downloads on the first day"}]}],
         "decisions":[{"text":"Meetings feature next","line":6,"quote":"Then we build the meetings feature next"}],
         "todos":[{"text":"Send the numbers to Sara","owner":"Erik","line":9,"quote":"send them to Sara on Thursday"}],"remember":[]}
        """;

    [Fact]
    public async Task AsksAgainOnceWhenTheAnswerIsntJson()
    {
        var model = new ScriptedModel("Sure! The meeting was about the launch.", Good);
        var s = await new MeetingSummarizer(() => model, () => model.Name).SummarizeAsync(TestMeetings.Launch());
        Assert.Equal(2, model.Asked.Count);
        Assert.Contains("valid JSON", model.Asked[1][^1].Content);
        Assert.Equal("The launch went well.", s.Overview);
        Assert.Single(s.Decisions);
        Assert.Equal("Erik", s.Todos[0].Owner);
        Assert.Equal("Scripted", s.Writer);
    }

    [Fact]
    public async Task ThePromptHasNumberedLinesNotesAndMarksInTheMeetingsLanguage()
    {
        var model = new ScriptedModel(Good);
        await new MeetingSummarizer(() => model, () => model.Name).SummarizeAsync(TestMeetings.Launch());
        var prompt = model.Asked[0][^1].Content;
        Assert.Contains("L6 [11:40] Anna: Then we build", prompt);
        Assert.Contains("marked by the user: Decision", prompt);
        Assert.Contains("[5:20] Check the numbers for Norway", prompt);
        Assert.Contains("Write everything in English", prompt);
    }

    [Fact]
    public async Task ALongMeetingIsSummarizedInPartsThenPutTogether()
    {
        var m = TestMeetings.Launch();
        var t = 2000.0;
        for (var i = 0; i < 400; i++, t += 6) m.Segments.Add(new Segment(t, t + 5, "S1", $"Line {i} about the warehouse move and the many boxes we still have to pack."));
        var model = new ScriptedModel { ContextTokens = 4096, Answer = msgs => Good };
        var progress = new List<double>();
        var s = await new MeetingSummarizer(() => model, () => model.Name).SummarizeAsync(m, new SyncProgress(progress.Add));
        var parts = MeetingSummarizer.Chunk(SummaryGrounding.Lines(m), MeetingSummarizer.ChunkChars(model)).Count;
        Assert.True(parts > 1);
        Assert.Equal(parts + 1, model.Asked.Count);                     // each part, then the merge
        Assert.Contains("summarized in", model.Asked[^1][^1].Content);
        Assert.Equal(1, progress[^1]);
        Assert.Single(s.Decisions);                                      // the same decision from every part, once
    }

    [Fact]
    public void TitlesAreCleanedUp()
    {
        Assert.Equal("Weekly meeting about the launch", MeetingSummarizer.CleanTitle("Title: \"weekly meeting about the launch.\"\nThis meeting…"));
        Assert.Equal("Veckomöte om lanseringen", MeetingSummarizer.CleanTitle("**Veckomöte om lanseringen**"));
    }

    [Fact]
    public void TheMeetingsLanguageIsFoundFromWhatsSaid()
    {
        var sv = TestMeetings.Swedish("x", DateTime.Now, ("S1", "Vi måste bestämma det här innan fredag, och det är viktigt."));
        Assert.Equal("sv", MeetingSummarizer.MeetingLanguage(SummaryGrounding.Lines(sv)));
        Assert.Equal("en", MeetingSummarizer.MeetingLanguage(SummaryGrounding.Lines(TestMeetings.Launch())));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
