using Glossa.Core.Library;
using Glossa.Core.Study;
using Microsoft.Data.Sqlite;

namespace Glossa.Tests.Study;

public sealed class ReviewStoreTests : IDisposable
{
    private static readonly StudyClock Utc = new(TimeZoneInfo.Utc);
    private static readonly DateTime Noon = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glossa-study-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*")) File.Delete(f);
    }

    private static SavedWord Word(string word) => new() { Language = "en", Word = word, Translation = "перевод", Context = $"say {word}" };

    private static (ReviewState State, ReviewAnswer Answer) Answer(ReviewState before, Rating rating, DateTime now)
    {
        var choices = AnkiScheduler.Next(before, new SchedulerContext(new StudyConfig(), Utc, now, Fuzz: false));
        return (choices[rating].State, ReviewAnswer.Of(before, choices, rating, takenMs: 4200));
    }

    [Fact]
    public void An_answer_reads_back_as_the_same_state_after_reopening()
    {
        string id;
        ReviewState learning, review;
        using (var store = new LibraryStore(_path))
        {
            id = store.Record(Word("reconsider"), newLookup: true);
            (learning, var first) = Answer(ReviewState.New(id), Rating.Good, Noon);
            store.SaveAnswer(learning, first);
            var (graduated, second) = Answer(learning, Rating.Good, Noon.AddMinutes(10));
            store.SaveAnswer(graduated, second);
            (review, var third) = Answer(graduated, Rating.Hard, Noon.AddDays(1));
            store.SaveAnswer(review, third);
        }

        using var reopened = new LibraryStore(_path);
        Assert.Equal(review, reopened.ReviewStates()[id]);
        Assert.Equal(2.35f, review.Ease);

        var log = reopened.Answers(Noon.AddHours(-1));
        Assert.Equal(new[] { Rating.Good, Rating.Good, Rating.Hard }, log.Select(a => a.Rating));
        Assert.Equal(new[] { CardQueue.New, CardQueue.Learning, CardQueue.Review }, log.Select(a => a.QueueBefore));
        Assert.Equal(new[] { 0, -600, 1 }, log.Select(a => a.IntervalBefore));
        Assert.Equal(new[] { -600, 1, 2 }, log.Select(a => a.IntervalAfter));
        Assert.All(log, a => Assert.Equal(4200, a.TakenMs));
        Assert.Single(reopened.Answers(Noon.AddDays(1)));
    }

    [Fact]
    public void Pinning_remembers_when_and_unpinning_forgets()
    {
        using var store = new LibraryStore(_path);
        var id = store.Record(Word("cred"), newLookup: true);

        store.SetPinned(id, true);
        var first = store.List().Single().PinnedUtc;
        Assert.NotNull(first);
        store.SetPinned(id, true);
        Assert.Equal(first, store.List().Single().PinnedUtc);
        store.SetPinned(id, false);
        Assert.Null(store.List().Single().PinnedUtc);
    }

    [Fact]
    public void A_version_five_library_gets_study_tables_and_a_backup()
    {
        string id;
        using (var store = new LibraryStore(_path))
        {
            id = store.Record(Word("tsundere"), newLookup: true);
            store.SetPinned(id, true);
        }
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DROP TABLE review_state; DROP TABLE review_log; ALTER TABLE words DROP COLUMN pinned_utc;
                PRAGMA user_version = 5;
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_path);
        Assert.True(File.Exists(_path + ".v5.bak"));
        Assert.Empty(migrated.ReviewStates());
        Assert.NotNull(migrated.List().Single().PinnedUtc); // a word pinned before v6 counts from its last change
    }
}
