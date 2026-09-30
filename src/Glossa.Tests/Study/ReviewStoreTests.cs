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
        Assert.Equal(review, reopened.ReviewStates()[new CardKey(id, CardDirection.Forward)]);
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

    [Fact]
    public void Both_directions_of_a_word_keep_their_own_state_and_log()
    {
        string id;
        ReviewState forward, reverse;
        using (var store = new LibraryStore(_path))
        {
            id = store.Record(Word("stained"), newLookup: true);
            (forward, var f) = Answer(ReviewState.New(id), Rating.Easy, Noon);
            store.SaveAnswer(forward, f);
            var (learning, r1) = Answer(ReviewState.New(id, CardDirection.Reverse), Rating.Good, Noon.AddMinutes(1));
            store.SaveAnswer(learning, r1);
            (reverse, var r2) = Answer(learning, Rating.Good, Noon.AddMinutes(11)); // the second answer updates only its own card
            store.SaveAnswer(reverse, r2);
        }

        using var reopened = new LibraryStore(_path);
        var states = reopened.ReviewStates();
        Assert.Equal(2, states.Count);
        Assert.Equal(forward, states[new CardKey(id, CardDirection.Forward)]);
        Assert.Equal(reverse, states[new CardKey(id, CardDirection.Reverse)]);
        Assert.Equal((4, 1), (forward.IntervalDays, reverse.IntervalDays));
        Assert.Equal(new[] { CardDirection.Forward, CardDirection.Reverse, CardDirection.Reverse },
            reopened.Answers(Noon.AddHours(-1)).Select(a => a.Direction));
    }

    [Fact]
    public void Taking_back_a_new_word_removes_both_of_its_cards()
    {
        using var store = new LibraryStore(_path);
        var id = store.Record(Word("misread"), newLookup: true, out var added);
        foreach (var direction in new[] { CardDirection.Forward, CardDirection.Reverse })
        {
            var (state, answer) = Answer(ReviewState.New(id, direction), Rating.Good, Noon);
            store.SaveAnswer(state, answer);
        }
        Assert.Equal(2, store.ReviewStates().Count);

        store.Retract(added!);

        Assert.Empty(store.ReviewStates());
        Assert.Empty(store.Answers(Noon.AddDays(-1)));
    }

    [Fact]
    public void A_version_seven_library_keeps_its_study_as_forward_cards_and_a_backup()
    {
        string id;
        ReviewState state;
        using (var store = new LibraryStore(_path))
        {
            id = store.Record(Word("sus"), newLookup: true);
            (state, var answer) = Answer(ReviewState.New(id), Rating.Easy, Noon);
            store.SaveAnswer(state, answer);
        }
        // Back to v7: a state per word, a log without directions.
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE review_state_v7(
                  word_id TEXT PRIMARY KEY, queue TEXT NOT NULL, remaining_steps INTEGER NOT NULL DEFAULT 0, due_at TEXT, due_day TEXT,
                  interval_days INTEGER NOT NULL DEFAULT 0, ease INTEGER NOT NULL DEFAULT 0, reps INTEGER NOT NULL DEFAULT 0,
                  lapses INTEGER NOT NULL DEFAULT 0, leech INTEGER NOT NULL DEFAULT 0, answered_utc TEXT);
                INSERT INTO review_state_v7
                  SELECT word_id, queue, remaining_steps, due_at, due_day, interval_days, ease, reps, lapses, leech, answered_utc FROM review_state;
                DROP TABLE review_state;
                ALTER TABLE review_state_v7 RENAME TO review_state;
                ALTER TABLE review_log DROP COLUMN direction;
                PRAGMA user_version = 7;
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_path);
        Assert.True(File.Exists(_path + ".v7.bak"));
        Assert.Equal(state, migrated.ReviewStates()[new CardKey(id, CardDirection.Forward)]);
        Assert.Equal(CardDirection.Forward, migrated.Answers(Noon.AddHours(-1)).Single().Direction);

        var (reverse, first) = Answer(ReviewState.New(id, CardDirection.Reverse), Rating.Good, Noon);
        migrated.SaveAnswer(reverse, first);
        Assert.Equal(2, migrated.ReviewStates().Count); // the word now has room for its second card
    }
}
