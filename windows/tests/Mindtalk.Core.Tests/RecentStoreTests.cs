using Mindtalk.Core.Dictation;

namespace Mindtalk.Core.Tests;

[Collection("Loc")]
public sealed class RecentStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private string FilePath => _temp.File("Recent.json");
    // Friday 16 October 2026, 22:00.
    private static readonly DateTime Now = new(2026, 10, 16, 22, 0, 0);

    public void Dispose() => _temp.Dispose();

    /// .NET may put a narrow no-break space before AM/PM; compare as ordinary spaces.
    private static string Plain(string s) => s.Replace((char)0x202F, ' ').Replace((char)0xA0, ' ');

    [Fact]
    public void NewestFirstAndAtMostFifty()
    {
        var recent = new RecentStore(FilePath);
        var changes = 0;
        recent.Changed += () => changes++;
        for (var i = 0; i < 55; i++) recent.Add($"text {i}", Now.AddMinutes(i));
        Assert.Equal(RecentStore.Limit, recent.Entries.Count);
        Assert.Equal("text 54", recent.Latest!.Text);
        Assert.Equal("text 5", recent.Entries[^1].Text);
        Assert.Equal(55, changes);
    }

    [Fact]
    public void RemoveAndClear()
    {
        var recent = new RecentStore(FilePath);
        var a = recent.Add("a", Now);
        recent.Add("b", Now);
        recent.Remove(a.Id);
        Assert.Equal(["b"], recent.Entries.Select(e => e.Text));
        recent.Remove(Guid.NewGuid());
        Assert.Single(recent.Entries);
        Assert.True(File.Exists(FilePath));
        recent.Clear();
        Assert.Null(recent.Latest);
        Assert.False(File.Exists(FilePath));   // nothing kept, nothing on disk
    }

    [Fact]
    public void SavesAndLoads()
    {
        var recent = new RecentStore(FilePath);
        recent.Add("första", Now.AddHours(-1));
        recent.Add("andra – med ”citat”", Now);
        var again = new RecentStore(FilePath);
        Assert.Equal(recent.Entries, again.Entries);
        File.WriteAllText(FilePath, "[{");
        Assert.Empty(new RecentStore(FilePath).Entries);
    }

    [Theory]
    [InlineData(0, "now", "nu")]
    [InlineData(59, "now", "nu")]
    [InlineData(60, "1 min", "1 min")]
    [InlineData(4 * 60, "4 min", "4 min")]
    [InlineData(2 * 3600, "2 hr", "2 tim")]
    [InlineData(86_400, "1 day", "1 d")]
    [InlineData(3 * 86_400, "3 days", "3 d")]
    [InlineData(8 * 86_400, "1 wk", "1 v")]
    [InlineData(21 * 86_400, "3 wk", "3 v")]
    public void Ago(int secondsAgo, string english, string swedish)
    {
        using (Language.English()) Assert.Equal(english, RecentStore.Ago(Now.AddSeconds(-secondsAgo), Now));
        using (Language.Swedish()) Assert.Equal(swedish, RecentStore.Ago(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void WhenInEnglish()
    {
        using var _ = Language.English();
        Assert.Equal("4 minutes ago", RecentStore.When(Now.AddMinutes(-4), Now));
        Assert.Equal("1 minute ago", RecentStore.When(Now.AddSeconds(-90), Now));
        Assert.Equal("30 seconds ago", RecentStore.When(Now.AddSeconds(-30), Now));
        Assert.Equal("59 minutes ago", RecentStore.When(Now.AddMinutes(-59), Now));
        Assert.Equal("Today 10:00 AM", Plain(RecentStore.When(Now.AddHours(-12), Now)));
        Assert.Equal("Yesterday 9:35 PM", Plain(RecentStore.When(new DateTime(2026, 10, 15, 21, 35, 0), Now)));
        Assert.Equal("Oct 12, 2026 at 9:05 AM", Plain(RecentStore.When(new DateTime(2026, 10, 12, 9, 5, 0), Now)));
    }

    [Fact]
    public void WhenInSwedish()
    {
        using var _ = Language.Swedish();
        Assert.Equal("För 4 minuter sedan", RecentStore.When(Now.AddMinutes(-4), Now));
        Assert.Equal("För 1 minut sedan", RecentStore.When(Now.AddSeconds(-61), Now));
        Assert.Equal("För 1 sekund sedan", RecentStore.When(Now.AddSeconds(-1), Now));
        Assert.Equal("I dag 10:00", RecentStore.When(Now.AddHours(-12), Now));
        Assert.Equal("I går 21:35", RecentStore.When(new DateTime(2026, 10, 15, 21, 35, 0), Now));
        Assert.Equal("12 okt. 2026 09:05", Plain(RecentStore.When(new DateTime(2026, 10, 12, 9, 5, 0), Now)));
    }
}
