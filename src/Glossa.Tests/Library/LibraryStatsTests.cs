using Glossa.Core.Library;

namespace Glossa.Tests.Library;

public class LibraryStatsTests
{
    private static readonly DateTime Now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();

    private static SavedWord Met(string lang, string word, string game, params int[] daysAgo) => new()
    {
        Language = lang, Word = word, CreatedUtc = Now.AddDays(-daysAgo.Max()),
        Contexts = daysAgo.Select(d => new WordContext { CreatedUtc = Now.AddDays(-d), AppExe = game + ".exe" }).ToList(),
    };

    [Fact]
    public void Counts_days_in_a_row_with_lookups_even_when_today_has_none_yet()
    {
        var stats = LibraryStats.Of([Met("en", "ass", "disco", 1, 2), Met("en", "cred", "disco", 3), Met("ja", "俺様", "p5r", 6, 7, 8, 9)],
            Now, (exe, _) => exe);

        Assert.Equal(3, stats.Streak);      // yesterday, the day before, three days ago
        Assert.Equal(4, stats.BestStreak);  // six to nine days ago
        Assert.Equal(3, stats.Words);
        Assert.Equal(2, stats.AddedThisWeek);
        Assert.Equal(("en", 2), stats.Languages[0]);
        Assert.Equal(("disco.exe", 2), stats.Games[0]);
    }

    [Fact]
    public void A_lookup_before_four_in_the_morning_belongs_to_the_evening_before()
    {
        var night = new DateTime(2026, 9, 29, 2, 30, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(new DateOnly(2026, 9, 28), LibraryStats.Day(night));
        Assert.Equal(new DateOnly(2026, 9, 29), LibraryStats.Day(night.AddHours(2)));
    }
}
