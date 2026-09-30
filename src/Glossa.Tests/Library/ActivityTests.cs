using Glossa.Core.Library;
using Glossa.Core.Study;
using Microsoft.Data.Sqlite;

namespace Glossa.Tests.Library;

public sealed class ActivityTests : IDisposable
{
    private readonly TestFolders _folders = new();
    private readonly string _path;

    public ActivityTests() => _path = Path.Combine(_folders.New(), "library.db");

    public void Dispose() => _folders.Dispose();

    private static readonly DateOnly Today = new(2026, 9, 30);

    private static DayActivity Did(int daysAgo, int points = 1, string kind = DayAction.Line) =>
        new(Today.AddDays(-daysAgo), new Dictionary<string, int> { [kind] = points });

    [Fact]
    public void Any_action_keeps_a_day_and_today_breaks_nothing_before_it_is_over()
    {
        var series = ActivityStreak.Of([Did(3), Did(2, kind: DayAction.Lookup), Did(1, kind: DayAction.Study)], Today, goal: 10);
        Assert.Equal((3, 3, 0), (series.Days, series.Best, series.TodayPoints));
        Assert.False(series.TodayFull);

        // A missed yesterday with no freeze ends it; today alone starts again.
        var broken = ActivityStreak.Of([Did(5), Did(4), Did(3), Did(0, points: 12)], Today, goal: 10);
        Assert.Equal((1, 3), (broken.Days, broken.Best));
        Assert.True(broken.TodayFull);
    }

    [Fact]
    public void Seven_days_in_a_row_earn_a_freeze_that_covers_a_missed_day()
    {
        // Days 10..4 ago in a row (seven), day 3 missed, days 2..1 again.
        var days = Enumerable.Range(4, 7).Select(n => Did(n)).Concat([Did(2), Did(1)]).ToList();
        var series = ActivityStreak.Of(days, Today, goal: 10);
        Assert.Equal(9, series.Days);
        Assert.Equal(0, series.Freezes);
        Assert.Equal(new[] { Today.AddDays(-3) }, series.Frozen);

        // Without the freeze (six days first) the miss breaks it.
        var shorter = ActivityStreak.Of(Enumerable.Range(4, 6).Select(n => Did(n)).Concat([Did(2), Did(1)]).ToList(), Today, goal: 10);
        Assert.Equal((2, 6), (shorter.Days, shorter.Best));
        Assert.Empty(shorter.Frozen);
    }

    [Fact]
    public void Freezes_are_held_two_at_most()
    {
        var series = ActivityStreak.Of(Enumerable.Range(1, 28).Select(n => Did(n)).ToList(), Today, goal: 10);
        Assert.Equal((28, ActivityStreak.MaxFreezes), (series.Days, series.Freezes));
    }

    [Fact]
    public void Actions_are_counted_by_day_and_kind_and_study_answers_count_where_they_are_saved()
    {
        using var store = new LibraryStore(_path);
        var at = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        store.AddActivity(DayAction.Line, at);
        store.AddActivity(DayAction.Line, at.AddMinutes(5));
        store.AddActivity(DayAction.Lookup, at);
        var id = store.Record(new SavedWord { Language = "en", Word = "bat" }, newLookup: true);
        store.SaveAnswer(new ReviewState { WordId = id, Queue = CardQueue.Learning, AnsweredUtc = at },
            new ReviewAnswer { WordId = id, AnsweredUtc = at, Rating = Rating.Good, QueueBefore = CardQueue.New });

        var day = Assert.Single(store.ActivityDays(DateOnly.MinValue));
        Assert.Equal(LibraryStats.Day(at), day.Day);
        Assert.Equal((2, 1, 1, 4), (day.Translations, day.Lookups, day.Answers, day.Points));
    }

    [Fact]
    public void A_library_from_before_gets_its_days_from_lookups_study_and_quotes()
    {
        var looked = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        var studied = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        var quoted = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        using (var store = new LibraryStore(_path))
        {
            var id = store.Record(new SavedWord { Language = "en", Word = "bat", Context = "A bat.", CreatedUtc = looked }, newLookup: true);
            store.SaveAnswer(new ReviewState { WordId = id, Queue = CardQueue.Learning, AnsweredUtc = studied },
                new ReviewAnswer { WordId = id, AnsweredUtc = studied, Rating = Rating.Good, QueueBefore = CardQueue.New });
            store.RecordQuote(new Quote { Language = "en", Text = "Hello there.", Translation = "Привет.", CreatedUtc = quoted, SeenUtc = quoted });
        }
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE activity; PRAGMA user_version = 10;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_path);
        var days = migrated.ActivityDays(DateOnly.MinValue).ToDictionary(d => d.Day);
        Assert.Equal(1, days[LibraryStats.Day(looked)].Lookups);
        Assert.Equal(1, days[LibraryStats.Day(studied)].Answers);
        Assert.Equal(1, days[LibraryStats.Day(quoted)].Translations);
        Assert.Equal(3, days.Count);
    }
}
