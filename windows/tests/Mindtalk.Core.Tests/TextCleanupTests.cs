using Mindtalk.Core;
using Xunit;

namespace Mindtalk.Core.Tests;

public class TextCleanupTests
{
    [Theory]
    [InlineData("Eh, vi ses på fredag", "Vi ses på fredag")]
    [InlineData("vi ses, öh, på fredag", "Vi ses på fredag")]
    [InlineData("I think um we should go", "I think we should go")]
    [InlineData("Hmm", "")]
    [InlineData("ehm", "")]
    // Words that merely look like hesitations are left alone.
    [InlineData("The er is a word", "The er is a word")]
    [InlineData("Ah, nice", "Ah, nice")]
    public void Fillers(string input, string expected) =>
        Assert.Equal(expected, TextCleanup.Apply(input, fillers: true, commands: false));

    [Theory]
    [InlineData("tack ny rad hälsningar Anna", "Tack\nHälsningar Anna")]
    [InlineData("hello new line world", "Hello\nWorld")]
    [InlineData("hej nytt stycke på återseende", "Hej\n\nPå återseende")]
    [InlineData("hello new paragraph world", "Hello\n\nWorld")]
    // "a new line" used as words, not a command.
    [InlineData("add a new line to the table", "Add a new line to the table")]
    [InlineData("lägg till en ny rad i tabellen", "Lägg till en ny rad i tabellen")]
    public void VoiceCommands(string input, string expected) =>
        Assert.Equal(expected, TextCleanup.Apply(input, fillers: false, commands: true));

    [Fact]
    public void FullExampleFromTheMacApp() =>
        Assert.Equal(
            "Vi ses på på fredag, nej jag menar lördag\nTack",
            TextCleanup.Apply("Eh, vi ses på på fredag, nej jag menar lördag ny rad tack"));

    [Theory]
    [InlineData("hello  world", "Hello world")]
    [InlineData("hello , world", "Hello, world")]
    [InlineData("hello, . world", "Hello. world")]
    [InlineData("  \t hello", "Hello")]
    public void Tidy(string input, string expected) =>
        Assert.Equal(expected, TextCleanup.Tidy(input));

    [Fact]
    public void SpokenLineBreakAtTheEndStays() =>
        Assert.Equal("Hej\n", TextCleanup.Apply("hej ny rad"));
}
