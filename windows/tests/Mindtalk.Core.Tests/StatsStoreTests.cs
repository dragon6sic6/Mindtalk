using Mindtalk.Core.Stats;

namespace Mindtalk.Core.Tests;

[Collection("Loc")]
public sealed class StatsStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    // Thursday 15 October 2026, ISO week 42.
    private DateTime _now = new(2026, 10, 15, 10, 0, 0);

    public void Dispose() => _temp.Dispose();

    private StatsStore Store() => new(_temp.File("stats.json"), () => _now);

    [Fact]
    public void RecordsWordsSecondsAndDictationsPerDay()
    {
        var stats = Store();
        var changes = 0;
        stats.Changed += () => changes++;
        stats.Record("Hej där,\nhur mår  du?", 3);
        stats.Record("   \n", 5);   // no words: not a dictation
        stats.Record("ett två tre", 1.5);
        Assert.Equal(2, changes);
        Assert.Equal(new DayStat { Words = 8, Seconds = 4.5, Dictations = 2 }, stats.Today);
        Assert.Equal(["2026-10-15"], stats.Days.Keys);
        Assert.Equal(8, stats.TotalWords);
    }

    [Fact]
    public void SavedTimeComparesWithTypingAtFortyWordsAMinute()
    {
        Assert.Equal(90, new DayStat { Words = 80, Seconds = 30 }.SavedSeconds);
        Assert.Equal(0, new DayStat { Words = 10, Seconds = 60 }.SavedSeconds);
        var stats = Store();
        stats.Record(string.Join(" ", Enumerable.Repeat("ord", 80)), 30);
        _now = _now.AddDays(-1);
        stats.Record(string.Join(" ", Enumerable.Repeat("ord", 40)), 20);
        Assert.Equal(130, stats.TotalSaved);
    }

    [Fact]
    public void TheWeekRunsMondayToSunday()
    {
        var stats = Store();
        stats.Record("tre ord här", 1);
        _now = new DateTime(2026, 10, 12, 8, 0, 0);   // Monday
        stats.Record("två ord", 1);
        _now = new DateTime(2026, 10, 11, 8, 0, 0);   // Sunday before: last week
        stats.Record("fyra ord som räknas", 1);

        _now = new DateTime(2026, 10, 15, 10, 0, 0);
        var week = stats.Week;
        Assert.Equal(7, week.Count);
        Assert.Equal(new DateTime(2026, 10, 12), week[0].Date);
        Assert.Equal(new DateTime(2026, 10, 18), week[6].Date);
        Assert.Equal([false, false, false, true, false, false, false], week.Select(d => d.IsToday));
        Assert.Equal([false, false, false, false, true, true, true], week.Select(d => d.IsFuture));
        Assert.Equal([2, 0, 0, 3, 0, 0, 0], week.Select(d => d.Stat.Words));
        Assert.Equal(5, stats.WeekWords);
        Assert.Equal(9, stats.TotalWords);

        _now = new DateTime(2026, 10, 18, 23, 59, 0);   // Sunday: still the same week
        Assert.Equal(new DateTime(2026, 10, 12), stats.Week[0].Date);
        Assert.True(stats.Week[6].IsToday);
        _now = new DateTime(2026, 10, 19, 0, 1, 0);     // Monday: a new week
        Assert.Equal(new DateTime(2026, 10, 19), stats.Week[0].Date);
        Assert.Equal(0, stats.WeekWords);
    }

    [Theory]
    [InlineData(2026, 10, 15, 42)]
    [InlineData(2026, 1, 1, 1)]
    [InlineData(2025, 12, 29, 1)]    // Monday of 2026's first week
    [InlineData(2027, 1, 1, 53)]     // 2026 has 53 weeks
    [InlineData(2027, 1, 4, 1)]
    public void WeekNumbersAreIso(int year, int month, int day, int week)
    {
        _now = new DateTime(year, month, day, 12, 0, 0);
        Assert.Equal(week, Store().WeekNumber);
    }

    [Fact]
    public void StreakCountsTodayOrUpToYesterday()
    {
        var stats = Store();
        foreach (var day in new[] { 9, 10, 12, 13, 14 })
        {
            _now = new DateTime(2026, 10, day, 9, 0, 0);
            stats.Record("hej", 1);
        }
        _now = new DateTime(2026, 10, 14, 20, 0, 0);
        Assert.Equal(3, stats.Streak);
        _now = new DateTime(2026, 10, 15, 20, 0, 0);   // nothing yet today
        Assert.Equal(3, stats.Streak);
        stats.Record("hej", 1);
        Assert.Equal(4, stats.Streak);
        _now = new DateTime(2026, 10, 17, 8, 0, 0);    // missed a day
        Assert.Equal(0, stats.Streak);
    }

    [Fact]
    public void KeepsTheNewest400Days()
    {
        var stats = Store();
        var first = new DateTime(2025, 1, 1, 12, 0, 0);
        for (var i = 0; i < 402; i++)
        {
            _now = first.AddDays(i);
            stats.Record("ord", 1);
        }
        Assert.Equal(400, stats.Days.Count);
        Assert.DoesNotContain("2025-01-01", stats.Days.Keys);
        Assert.DoesNotContain("2025-01-02", stats.Days.Keys);
        Assert.Contains("2025-01-03", stats.Days.Keys);
    }

    [Fact]
    public void SavesAndLoads()
    {
        var stats = Store();
        stats.Record("ett två tre", 2.5);
        var again = Store();
        Assert.Equal(stats.Today, again.Today);
        File.WriteAllText(_temp.File("stats.json"), "{ broken");
        Assert.Empty(Store().Days);
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(44.6, "45 s")]
    [InlineData(59.4, "59 s")]
    [InlineData(59.5, "1 min")]
    [InlineData(2400, "40 min")]
    [InlineData(3599, "59 min")]
    [InlineData(7200, "2 h")]
    [InlineData(7500, "2 h 5 min")]
    public void Duration(double seconds, string expected) => Assert.Equal(expected, StatsStore.Duration(seconds));

    [Fact]
    public void NumbersFollowTheLanguage()
    {
        using (Language.Swedish())
        {
            var swedish = StatsStore.Number(1234567);
            // Swedish groups with a (non-breaking) space.
            Assert.Equal("1 234 567", swedish.Replace((char)0xA0, ' ').Replace((char)0x202F, ' '));
            Assert.Equal("999", StatsStore.Number(999));
        }
        using (Language.English())
        {
            Assert.Equal("1,234,567", StatsStore.Number(1234567));
        }
    }
}
