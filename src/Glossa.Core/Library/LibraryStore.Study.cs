using System.Globalization;
using Glossa.Core.Study;
using Microsoft.Data.Sqlite;

namespace Glossa.Core.Library;

/// <summary>Study: each word's Anki card state and the log of answers (schema v6).</summary>
public sealed partial class LibraryStore
{
    /// <summary>The state of every word ever answered; a word missing here is new.</summary>
    public IReadOnlyDictionary<string, ReviewState> ReviewStates()
    {
        lock (_gate)
        {
            var states = new Dictionary<string, ReviewState>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM review_state";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var state = ReadReview(r);
                states[state.WordId] = state;
            }
            return states;
        }
    }

    /// <summary>Stores an answer: the word's new state and its log line, together or not at all.</summary>
    public void SaveAnswer(ReviewState state, ReviewAnswer answer)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Run("""
                INSERT INTO review_state(word_id, queue, remaining_steps, due_at, due_day, interval_days, ease, reps, lapses, leech, answered_utc)
                VALUES($w, $queue, $left, $dueAt, $dueDay, $ivl, $ease, $reps, $lapses, $leech, $at)
                ON CONFLICT(word_id) DO UPDATE SET queue = $queue, remaining_steps = $left, due_at = $dueAt, due_day = $dueDay,
                  interval_days = $ivl, ease = $ease, reps = $reps, lapses = $lapses, leech = $leech, answered_utc = $at
                """,
                ("$w", state.WordId), ("$queue", Name(state.Queue)), ("$left", state.RemainingSteps),
                ("$dueAt", state.DueAt is { } dueAt ? Iso(dueAt) : null), ("$dueDay", state.DueDay is { } day ? DayText(day) : null),
                ("$ivl", state.IntervalDays), ("$ease", Permille(state.Ease)), ("$reps", state.Reps), ("$lapses", state.Lapses),
                ("$leech", state.Leech ? 1 : 0), ("$at", state.AnsweredUtc is { } at ? Iso(at) : null));
            Run("""
                INSERT INTO review_log(id, word_id, answered_utc, rating, queue_before, early, interval_before, interval_after, ease, taken_ms)
                VALUES($id, $w, $at, $rating, $queue, $early, $before, $after, $ease, $taken)
                """,
                ("$id", answer.Id), ("$w", answer.WordId), ("$at", Iso(answer.AnsweredUtc)), ("$rating", (int)answer.Rating),
                ("$queue", Name(answer.QueueBefore)), ("$early", answer.Early ? 1 : 0), ("$before", answer.IntervalBefore),
                ("$after", answer.IntervalAfter), ("$ease", Permille(answer.Ease)), ("$taken", answer.TakenMs));
            tx.Commit();
        }
    }

    /// <summary>Answers since a moment, oldest first: today's counts, study streaks, the suggestion to unpin.</summary>
    public IReadOnlyList<ReviewAnswer> Answers(DateTime sinceUtc)
    {
        lock (_gate)
        {
            var answers = new List<ReviewAnswer>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM review_log WHERE answered_utc >= $since ORDER BY answered_utc";
            cmd.Parameters.AddWithValue("$since", Iso(sinceUtc));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                answers.Add(new ReviewAnswer
                {
                    Id = (string)r["id"],
                    WordId = (string)r["word_id"],
                    AnsweredUtc = Date(r["answered_utc"]),
                    Rating = (Rating)Int(r, "rating"),
                    QueueBefore = Enum.Parse<CardQueue>((string)r["queue_before"], ignoreCase: true),
                    Early = Int(r, "early") != 0,
                    IntervalBefore = Int(r, "interval_before"),
                    IntervalAfter = Int(r, "interval_after"),
                    Ease = Int(r, "ease") / 1000f,
                    TakenMs = Int(r, "taken_ms"),
                });
            }
            return answers;
        }
    }

    private static ReviewState ReadReview(SqliteDataReader r)
    {
        string? S(string col) => r[col] is DBNull ? null : (string)r[col];
        return new ReviewState
        {
            WordId = (string)r["word_id"],
            Queue = Enum.Parse<CardQueue>((string)r["queue"], ignoreCase: true),
            RemainingSteps = Int(r, "remaining_steps"),
            DueAt = S("due_at") is { } at ? Date(at) : null,
            DueDay = S("due_day") is { } day ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            IntervalDays = Int(r, "interval_days"),
            Ease = Int(r, "ease") / 1000f,
            Reps = Int(r, "reps"),
            Lapses = Int(r, "lapses"),
            Leech = Int(r, "leech") != 0,
            AnsweredUtc = S("answered_utc") is { } answered ? Date(answered) : null,
        };
    }

    private static int Int(SqliteDataReader r, string col) => Convert.ToInt32(r[col], CultureInfo.InvariantCulture);

    private static string Name(CardQueue queue) => queue.ToString().ToLowerInvariant();

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Anki keeps the ease factor in thousandths (2500 = 2.5).
    private static int Permille(float ease) => (int)MathF.Round(ease * 1000f, MidpointRounding.AwayFromZero);
}
