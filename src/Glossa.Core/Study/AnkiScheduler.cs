namespace Glossa.Core.Study;

/// <summary>What the scheduler needs besides the word.</summary>
/// <param name="Fuzz">Anki spreads and balances day intervals; off only to compare with Anki's worked examples.</param>
/// <param name="DueOn">Reviews already due on a study day, for load balancing; null turns balancing off.</param>
/// <param name="LearnJitter">0..1: where within Anki's extra (up to 25%, at most 5 minutes) a learning step lands.</param>
public sealed record SchedulerContext(StudyConfig Config, StudyClock Clock, DateTime NowUtc, bool Fuzz = true,
    Func<DateOnly, int>? DueOn = null, float LearnJitter = 0f);

/// <summary>What an answer leads to: the word's next state and the interval its button shows.</summary>
public sealed record Outcome(ReviewState State, TimeSpan Interval);

/// <summary>The four buttons. <see cref="Early"/>: a review answered before it is due (a pinned word).</summary>
public sealed record Choices(Outcome Again, Outcome Hard, Outcome Good, Outcome Easy, bool Early)
{
    public Outcome this[Rating rating] => rating switch
    {
        Rating.Again => Again, Rating.Hard => Hard, Rating.Good => Good, _ => Easy,
    };
}

/// <summary>
/// Anki's v3 scheduler with SM-2, ported one to one (docs/ANKI_SCHEDULER.md; rslib scheduler/states). Pure: it only
/// says what each answer would do. Arithmetic is in float and rounds half away from zero, like Rust's f32.
/// </summary>
public static class AnkiScheduler
{
    private const float AgainDelta = -0.2f, HardDelta = -0.15f, EasyDelta = 0.15f, MinimumEase = 1.3f;
    private const int Day = 86_400;

    public static Choices Next(ReviewState state, SchedulerContext context) => new Answering(state, context).Choices();

    /// <summary>Anki's leech rule: at the threshold, then every half of it (8, 12, 16...).</summary>
    public static bool LeechThresholdMet(int lapses, int threshold)
    {
        if (threshold <= 0) return false;
        var half = Math.Max((int)MathF.Ceiling(threshold / 2f), 1);
        return lapses >= threshold && (lapses - threshold) % half == 0;
    }

    private sealed class Answering(ReviewState card, SchedulerContext ctx)
    {
        private readonly StudyConfig _c = ctx.Config;
        private readonly DateOnly _today = ctx.Clock.Day(ctx.NowUtc);
        private readonly ulong? _seed = ctx.Fuzz ? StudySeed.Of(card.Key, card.Reps) : null;

        public Choices Choices() => card.Queue switch
        {
            CardQueue.New => Learning(_c.LearnSteps.Count),
            CardQueue.Learning => Learning(card.RemainingSteps),
            CardQueue.Relearning => Relearning(),
            _ => Review(),
        };

        // learning.rs: a new word is a word on learning step 0.
        private Choices Learning(int remaining)
        {
            var steps = new Steps(_c.LearnSteps);
            var (min, max) = Bounds(1);
            Outcome Graduate(int days) => ToReview(card, ReviewFuzz(days, min, max), _c.StartingEase, card.Lapses, card.Leech);
            return new Choices(
                steps.Again is { } again ? Learn(card, CardQueue.Learning, steps.Count, again) : Graduate(_c.GraduatingInterval),
                steps.Hard(remaining) is { } hard ? Learn(card, CardQueue.Learning, remaining, hard) : Graduate(_c.GraduatingInterval),
                steps.Good(remaining) is { } good ? Learn(card, CardQueue.Learning, steps.RemainingForGood(remaining), good) : Graduate(_c.GraduatingInterval),
                Graduate(_c.EasyInterval),
                false);
        }

        // relearning.rs: the lapse already set the interval and ease; steps only decide when the word returns.
        private Choices Relearning()
        {
            var steps = new Steps(_c.RelearnSteps);
            var remaining = card.RemainingSteps;
            Outcome Back(int days) => ToReview(card, days, card.Ease, card.Lapses, card.Leech);
            return new Choices(
                steps.Again is { } again ? Learn(card, CardQueue.Relearning, steps.Count, again) : Back(card.IntervalDays),
                steps.Hard(remaining) is { } hard ? Learn(card, CardQueue.Relearning, remaining, hard) : Back(card.IntervalDays),
                steps.Good(remaining) is { } good ? Learn(card, CardQueue.Relearning, steps.RemainingForGood(remaining), good) : Back(card.IntervalDays),
                Back(card.IntervalDays + 1),
                false);
        }

        // review.rs
        private Choices Review()
        {
            var scheduled = Math.Max(card.IntervalDays, 1);
            var due = card.DueDay ?? _today;
            var elapsed = _today.DayNumber - due.AddDays(-card.IntervalDays).DayNumber;
            var late = elapsed - card.IntervalDays;

            var lapses = card.Lapses + 1;
            var leech = card.Leech || LeechThresholdMet(lapses, _c.LeechThreshold);
            var (lapseMin, lapseMax) = Bounds(_c.MinimumLapseInterval);
            var lapseDays = ReviewFuzz(card.IntervalDays * _c.LapseMultiplier, lapseMin, lapseMax);
            var lapseEase = MathF.Max(card.Ease + AgainDelta, MinimumEase);
            var relearn = new Steps(_c.RelearnSteps);
            var again = relearn.Again is { } secs
                ? Learn(card with { IntervalDays = lapseDays, Ease = lapseEase, Lapses = lapses, Leech = leech }, CardQueue.Relearning, relearn.Count, secs)
                : ToReview(card, lapseDays, lapseEase, lapses, leech);

            var (hard, good, easy) = late < 0 ? EarlyIntervals(scheduled, elapsed) : Intervals(scheduled, late);
            return new Choices(
                again,
                ToReview(card, hard, MathF.Max(card.Ease + HardDelta, MinimumEase), card.Lapses, card.Leech),
                ToReview(card, good, card.Ease, card.Lapses, card.Leech),
                ToReview(card, easy, card.Ease + EasyDelta, card.Lapses, card.Leech),
                late < 0);
        }

        // passing_nonearly_review_intervals: each button at least a day more than the one before.
        private (int Hard, int Good, int Easy) Intervals(int scheduled, int late)
        {
            float current = scheduled;
            var hard = Passing(current * _c.HardMultiplier, _c.HardMultiplier <= 1 ? 0 : scheduled + 1, fuzz: true);
            var good = Passing((current + late / 2f) * card.Ease, _c.HardMultiplier <= 1 ? scheduled + 1 : hard + 1, fuzz: true);
            var easy = Passing((current + late) * card.Ease * _c.EasyBonus, good + 1, fuzz: true);
            return (hard, good, easy);
        }

        // passing_early_review_intervals (filtered decks): no fuzz and no ordering minimums. Hard uses the deck's hard
        // multiplier and half of it (1.2 and 0.6 by default), as Anki does.
        private (int Hard, int Good, int Easy) EarlyIntervals(int scheduled, int elapsed)
        {
            float current = scheduled, passed = elapsed;
            var hard = Passing(MathF.Max(passed * _c.HardMultiplier, current * (_c.HardMultiplier / 2f)), 0, fuzz: false);
            var good = Passing(MathF.Max(passed * card.Ease, current), 0, fuzz: false);
            var reducedBonus = _c.EasyBonus - (_c.EasyBonus - 1f) / 2f;
            var easy = Passing(MathF.Max(passed * card.Ease, current) * reducedBonus, 0, fuzz: false);
            return (hard, good, easy);
        }

        private int Passing(float interval, int minimum, bool fuzz)
        {
            interval *= _c.IntervalModifier;
            var (min, max) = Bounds(minimum);
            return fuzz ? ReviewFuzz(interval, min, max) : Math.Clamp(Fuzz.Round(interval), min, max);
        }

        private (int Min, int Max) Bounds(int minimum)
        {
            var max = Math.Max(_c.MaximumInterval, 1);
            return (Math.Clamp(minimum, 1, max), max);
        }

        // with_review_fuzz: balanced over the fuzz range when possible, else fuzzed, else just rounded.
        private int ReviewFuzz(float interval, int min, int max)
        {
            if (_seed is not { } seed) return Math.Clamp(Fuzz.Round(interval), min, max);
            if (ctx.DueOn is { } dueOn && LoadBalancer.Pick(interval, min, max, days => dueOn(_today.AddDays(days)), seed) is { } day)
                return day;
            return Fuzz.Apply(interval, min, max, StudyRandom.Float(seed));
        }

        private Outcome Learn(ReviewState state, CardQueue queue, int remaining, int secs)
        {
            var next = state with { Queue = queue, RemainingSteps = remaining, Reps = card.Reps + 1, AnsweredUtc = ctx.NowUtc };
            // A step that reaches past 4:00 is counted in days and waits for that study day (Anki's "day learn").
            var untilRollover = (int)(ctx.Clock.Rollover(ctx.NowUtc) - ctx.NowUtc).TotalSeconds;
            next = secs >= untilRollover
                ? next with { DueAt = null, DueDay = _today.AddDays((secs - untilRollover) / Day + 1) }
                : next with { DueAt = ctx.NowUtc.AddSeconds(LearnFuzz(secs)), DueDay = null };
            return new Outcome(next, TimeSpan.FromSeconds(secs));
        }

        // Up to 25% more, at most 5 minutes (answering/learning.rs).
        private int LearnFuzz(int secs)
        {
            var upper = secs + (int)MathF.Floor(MathF.Min(secs * 0.25f, 300f));
            return secs >= upper ? secs : secs + (int)(ctx.LearnJitter * (upper - secs));
        }

        private Outcome ToReview(ReviewState state, int days, float ease, int lapses, bool leech) => new(
            state with
            {
                Queue = CardQueue.Review, RemainingSteps = 0, DueAt = null, DueDay = _today.AddDays(days), IntervalDays = days,
                Ease = ease, Lapses = lapses, Leech = leech, Reps = card.Reps + 1, AnsweredUtc = ctx.NowUtc,
            },
            TimeSpan.FromDays(days));
    }

    /// <summary>Learning steps in minutes (steps.rs); "remaining" counts the current step, as Anki's "left" does.</summary>
    private readonly struct Steps(IReadOnlyList<float> minutes)
    {
        public int Count => minutes.Count;

        public int? Again => Secs(0);

        /// <summary>The same step; on the first one the average of the first two, or half as long again (at most a day more).</summary>
        public int? Hard(int remaining)
        {
            var index = Index(remaining);
            if ((Secs(index) ?? Secs(0)) is not { } secs) return null;
            if (index == 0) secs = Secs(1) is { } next ? (secs + next) / 2 : Math.Min((int)(secs * 1.5f), secs + Day);
            return secs > Day ? (int)MathF.Round((float)secs / Day, MidpointRounding.AwayFromZero) * Day : secs;
        }

        public int? Good(int remaining) => Secs(Index(remaining) + 1);

        public int RemainingForGood(int remaining) => Math.Max(minutes.Count - (Index(remaining) + 1), 0);

        private int? Secs(int index) => index < minutes.Count ? (int)(minutes[index] * 60f) : null;

        private int Index(int remaining) => Math.Min(Math.Max(minutes.Count - remaining % 1000, 0), Math.Max(minutes.Count - 1, 0));
    }
}
