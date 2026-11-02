using Mindtalk.Core.Vocabulary;

namespace Mindtalk.Core.Tests;

public class VocabularyMatcherTests
{
    private static readonly HashSet<string> Dictionary = ["per", "test", "klass", "google"];

    private static string Fix(string text, params (string Word, string[] HeardAs)[] entries) =>
        new VocabularyMatcher(entries.Select(e => (e.Word, (IReadOnlyList<string>)e.HeardAs)))
            .Apply(text, Dictionary.Contains).Text;

    [Theory]
    [InlineData("jag använder mind talk varje dag", "jag använder Mindtalk varje dag")]
    [InlineData("jag använder mindtalk", "jag använder Mindtalk")]
    [InlineData("jag använder Mind-talk", "jag använder Mindtalk")]
    [InlineData("mindtalks bästa sida", "Mindtalks bästa sida")]
    [InlineData("i mindtalken", "i Mindtalken")]
    [InlineData("MINDTALK är skrivet med versaler", "MINDTALK är skrivet med versaler")]
    [InlineData("mind\ntalk", "mind\ntalk")]
    [InlineData("mindtalkare", "mindtalkare")]
    public void TheWordItself(string text, string expected) =>
        Assert.Equal(expected, Fix(text, ("Mindtalk", [])));

    [Theory]
    [InlineData("han sen kom", "han sen kom")]
    [InlineData("hansen kom", "Hansen kom")]
    public void ShortNamesAreNeverPiecedTogether(string text, string expected) =>
        Assert.Equal(expected, Fix(text, ("Hansen", [])));

    [Fact]
    public void SplitPartsAreAtLeastFourLetters()
    {
        // "Mindact" is under eight letters: never split.
        Assert.Equal("min dact", Fix("min dact", ("Mindact", [])));
        // Eight letters: "mind talk" yes, "min dtalk" no.
        Assert.Equal("min dtalk", Fix("min dtalk", ("Mindtalk", [])));
    }

    [Theory]
    [InlineData("två gånger per dag", "två gånger per dag")]
    [InlineData("det här är ett test", "det här är ett test")]
    // A dictionary word with an ending: "klas" + s.
    [InlineData("en hel klass", "en hel klass")]
    [InlineData("hälsa till klas", "hälsa till Klas")]
    public void OrdinaryWordsAreLeftAlone(string text, string expected) =>
        Assert.Equal(expected, Fix(text, ("Per", []), ("Test", []), ("Klas", [])));

    [Fact]
    public void LongerNamesAreRecasedEvenIfTheDictionaryKnowsThem() =>
        Assert.Equal("sök på Google", Fix("sök på google", ("Google", [])));

    [Fact]
    public void VariantsAreAlwaysFixed()
    {
        Assert.Equal("jag skrev till Mindact", Fix("jag skrev till min dag", ("Mindact", ["min dag"])));
        Assert.Equal("Mindacts kontor", Fix("min dags kontor", ("Mindact", ["min dag"])));
        Assert.Equal("Mindact", Fix("MIN DAG", ("Mindact", ["min dag"])));
        Assert.Equal("min\ndag", Fix("min\ndag", ("Mindact", ["min dag"])));
    }

    [Fact]
    public void OneEntryNeverRewritesAnothersResult() =>
        Assert.Equal("Bert och Bart", Fix("bart och bert", ("Bert", ["bart"]), ("Bart", ["bert"])));

    [Fact]
    public void LowercaseEntryKeepsTheSentenceCapital()
    {
        Assert.Equal("Mejl skickat.", Fix("Mail skickat.", ("mejl", ["mail"])));
        Assert.Equal("ett mejl skickat.", Fix("ett mail skickat.", ("mejl", ["mail"])));
        Assert.Equal("Mejl skickat.", Fix("Mejl skickat.", ("mejl", [])));
    }

    [Fact]
    public void ApplyCountsPerEntryAndReportsRangesInTheNewText()
    {
        var matcher = new VocabularyMatcher([("Mindtalk", (IReadOnlyList<string>)[]), ("Anna", ["ana"])]);
        var result = matcher.Apply("mind talk och ana och mindtalk", _ => false);
        Assert.Equal("Mindtalk och Anna och Mindtalk", result.Text);
        Assert.Equal(2, result.Counts[0]);
        Assert.Equal(1, result.Counts[1]);
        Assert.Equal([new TextSpan(0, 8), new TextSpan(13, 4), new TextSpan(22, 8)], result.Ranges);
        foreach (var r in result.Ranges) Assert.Contains(result.Text.Substring(r.Start, r.Length), new[] { "Mindtalk", "Anna" });
    }

    [Fact]
    public void NothingToFixReturnsTheTextAsIs()
    {
        var result = new VocabularyMatcher([("Mindtalk", (IReadOnlyList<string>)[])]).Apply("Mindtalk", _ => false);
        Assert.Equal("Mindtalk", result.Text);
        Assert.Empty(result.Counts);
        Assert.Empty(result.Ranges);
    }

    [Fact]
    public void TheLongestMatchWinsAtTheSameStart() =>
        Assert.Equal("Mindtalk Pro", Fix("mindtalk pro", ("Mindtalk", []), ("Mindtalk Pro", [])));
}
