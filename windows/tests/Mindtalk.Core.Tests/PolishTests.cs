using Mindtalk.Core.Polish;

namespace Mindtalk.Core.Tests;

public class PolishGuardTests
{
    [Theory]
    // Self-corrections (the examples in the model's instructions).
    [InlineData("Vi ses på tisdag, nej jag menar onsdag.", "Vi ses på onsdag.")]
    [InlineData("Skicka det till Per, förlåt, till Anna.", "Skicka det till Anna.")]
    [InlineData("Call me at three, I mean four.", "Call me at four.")]
    // Not a correction: kept as it is.
    [InlineData("Nej, jag menar att vi borde vänta.", "Nej, jag menar att vi borde vänta.")]
    // Hesitations, words said twice, spoken punctuation.
    [InlineData("eh jag tänkte att vi kunde ses", "Jag tänkte att vi kunde ses.")]
    [InlineData("vi ses på på fredag", "Vi ses på fredag.")]
    [InlineData("hej komma hur mår du frågetecken", "Hej, hur mår du?")]
    [InlineData("that is all full stop", "That is all.")]
    // Split or joined words.
    [InlineData("köpmjöl och socker", "Köp mjöl och socker.")]
    [InlineData("köp mjöl och socker", "Köpmjöl och socker.")]
    // Apostrophes: typographic or straight.
    [InlineData("I don't know", "I don’t know.")]
    public void Accepts(string original, string polished) =>
        Assert.True(PolishGuard.Accepts(polished, original));

    [Theory]
    // A negation is never dropped.
    [InlineData("jag godkänner inte avtalet", "Jag godkänner avtalet.")]
    [InlineData("I don't like it", "I like it.")]
    [InlineData("we can never go back", "We can go back.")]
    // Nothing added, nothing rephrased.
    [InlineData("vi ses i morgon", "Vi ses i morgon klockan tre.")]
    [InlineData("vi ses i morgon", "Vi träffas i morgon.")]
    // Both copies of a repeated word can't go.
    [InlineData("vi ses på på fredag", "Vi ses fredag.")]
    // "nej" alone isn't a correction.
    [InlineData("nej det gör vi inte", "Det gör vi inte.")]
    // Numbers are never joined: "1 2" isn't "12".
    [InlineData("ring 1 2 3", "Ring 123.")]
    // Punctuation glued between letters.
    [InlineData("mind act är bra", "Mind.act är bra.")]
    // Far too short.
    [InlineData("ett två tre fyra fem sex", "Ett.")]
    [InlineData("hej", "")]
    public void Rejects(string original, string polished) =>
        Assert.False(PolishGuard.Accepts(polished, original));

    [Fact]
    public void OnlyOrdinaryCharacters()
    {
        Assert.True(PolishGuard.Accepts("Hej – ”då” … 50 % (ok)\nVi ses.", "hej då 50 ok\nvi ses"));
        // Bidi override, zero-width space, tab, emoji.
        Assert.False(PolishGuard.Accepts("Hej" + (char)0x202E + " då.", "hej då"));
        Assert.False(PolishGuard.Accepts("Hej" + (char)0x200B + "då.", "hej då"));
        Assert.False(PolishGuard.Accepts("Hej\tdå.", "hej då"));
        Assert.False(PolishGuard.Accepts("Hej då 😀", "hej då"));
        // Accents as combining marks are letters.
        Assert.True(PolishGuard.Accepts("Cafe" + (char)0x0301 + ".", "cafe" + (char)0x0301));
    }

    [Fact]
    public void Words() =>
        Assert.Equal(["i", "don't", "know", "köp", "mjöl"], PolishGuard.Words("I don’t know – köp-mjöl!"));
}

public class LinePolisherTests
{
    private static Func<string, CancellationToken, Task<string?>> Answers(Dictionary<string, string> answers, List<string>? asked = null) =>
        (line, _) =>
        {
            lock (answers) asked?.Add(line);
            return Task.FromResult<string?>(answers.GetValueOrDefault(line));
        };

    [Fact]
    public async Task PolishesLineByLineKeepingLineBreaks()
    {
        var asked = new List<string>();
        var result = await LinePolisher.PolishAsync(
            "hej komma hur mår du\nok tack\n\nvi ses på på fredag",
            Answers(new() { ["hej komma hur mår du"] = "Hej, hur mår du?", ["vi ses på på fredag"] = "\"Vi ses på fredag.\"\n" }, asked));
        Assert.Equal("Hej, hur mår du?\nok tack\n\nVi ses på fredag.", result);
        // Lines under three words aren't sent.
        Assert.Equal(["hej komma hur mår du", "vi ses på på fredag"], asked.Order());
    }

    [Fact]
    public async Task AnAnswerTheGuardRejectsKeepsTheLine()
    {
        var result = await LinePolisher.PolishAsync(
            "jag godkänner inte avtalet\nvi ses på på fredag",
            Answers(new() { ["jag godkänner inte avtalet"] = "Jag godkänner avtalet.", ["vi ses på på fredag"] = "Vi ses på fredag." }));
        Assert.Equal("jag godkänner inte avtalet\nVi ses på fredag.", result);
    }

    [Fact]
    public async Task NothingChangedIsNull()
    {
        Assert.Null(await LinePolisher.PolishAsync("vi ses i morgon",
            Answers(new() { ["vi ses i morgon"] = "Vi ses i morgon och sen åker vi." })));
        Assert.Null(await LinePolisher.PolishAsync("vi ses i morgon", Answers(new() { ["vi ses i morgon"] = "vi ses i morgon" })));
        Assert.Null(await LinePolisher.PolishAsync("vi ses i morgon", (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task ShortOrLongTextIsNotSent()
    {
        var calls = 0;
        Func<string, CancellationToken, Task<string?>> respond = (line, _) => { calls++; return Task.FromResult<string?>(line.ToUpperInvariant()); };
        Assert.Null(await LinePolisher.PolishAsync("ok tack\nja", respond));
        Assert.Null(await LinePolisher.PolishAsync(string.Join(" ", Enumerable.Repeat("ord", 601)), respond));
        Assert.Equal(0, calls);
        Assert.NotNull(await LinePolisher.PolishAsync(string.Join(" ", Enumerable.Repeat("ord", 600)), respond));
    }

    [Fact]
    public async Task SlowLinesKeepTheirTextAfterTheTimeout()
    {
        var cancelled = new TaskCompletionSource();
        Func<string, CancellationToken, Task<string?>> respond = async (line, ct) =>
        {
            if (line.StartsWith("slow"))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return line == "vi ses på på fredag" ? "Vi ses på fredag." : "Slow, line here.";
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await LinePolisher.PolishAsync("slow line here\nvi ses på på fredag", respond, TimeSpan.FromMilliseconds(300));
        Assert.Equal("slow line here\nVi ses på fredag.", result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the slow answer was called off
    }

    [Fact]
    public void AnswersAreCleaned()
    {
        Assert.Equal("Hej, hur mår du?", LinePolisher.Accept(" «Hej, hur mår du?» ", "hej komma hur mår du"));
        Assert.Equal("Hej, hur mår du?", LinePolisher.Accept("“Hej,\nhur mår du?”", "hej komma hur mår du"));
        Assert.Null(LinePolisher.Accept("Visst! Hej, hur mår du?", "hej komma hur mår du"));
    }

    [Fact]
    public void InstructionsAreTheMacsWordForWord()
    {
        Assert.StartsWith("You clean up one line of dictated text. Someone spoke it aloud and a speech recognizer wrote it down. Return the same text",
                          LinePolisher.Instructions);
        Assert.Contains("\n  \"Vi ses på tisdag, nej jag menar onsdag.\" → \"Vi ses på onsdag.\"\n", LinePolisher.Instructions);
        Assert.Contains("(\"punkt\", \"komma\", \"frågetecken\", \"utropstecken\", \"kolon\",", LinePolisher.Instructions);
        Assert.EndsWith("only clean it. Reply with the cleaned line only.", LinePolisher.Instructions);
        Assert.DoesNotContain("\r", LinePolisher.Instructions);
        Assert.Equal(10, LinePolisher.Instructions.Split('\n').Length);
    }

    [Fact]
    public void WordsAreRunsOfLettersAndDigits() =>
        Assert.Equal(["don", "t", "köp", "12"], LinePolisher.Words("Don't köp-12!"));
}
