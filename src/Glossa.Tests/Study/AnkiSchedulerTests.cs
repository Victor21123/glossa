using Glossa.Core.Study;

namespace Glossa.Tests.Study;

/// <summary>Every number here comes from docs/ANKI_SCHEDULER.md (Anki 26.09.3, v3 scheduler, SM-2 defaults).</summary>
public class AnkiSchedulerTests
{
    private static readonly StudyClock Utc = new(TimeZoneInfo.Utc);
    private static readonly DateTime Noon = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static SchedulerContext At(DateTime now, bool fuzz = false) => new(new StudyConfig(), Utc, now, fuzz);

    private static ReviewState Review(int interval, DateOnly due, float ease = 2.5f, int lapses = 0) => new()
    {
        WordId = "w", Queue = CardQueue.Review, IntervalDays = interval, DueDay = due, Ease = ease, Lapses = lapses, Reps = 5,
    };

    [Fact]
    public void A_new_word_shows_the_anki_default_buttons()
    {
        var next = AnkiScheduler.Next(ReviewState.New("w"), At(Noon));

        Assert.Equal("<1 мин", StudyLabels.Button(next.Again.Interval));
        Assert.Equal("<6 мин", StudyLabels.Button(next.Hard.Interval));
        Assert.Equal("<10 мин", StudyLabels.Button(next.Good.Interval));
        Assert.Equal("4 дня", StudyLabels.Button(next.Easy.Interval));
    }

    [Fact]
    public void Learning_steps_move_a_new_word_one_step_at_a_time()
    {
        var next = AnkiScheduler.Next(ReviewState.New("w"), At(Noon));

        Assert.Equal((CardQueue.Learning, 2, Noon.AddSeconds(60)), (next.Again.State.Queue, next.Again.State.RemainingSteps, next.Again.State.DueAt));
        Assert.Equal((CardQueue.Learning, 2, Noon.AddSeconds(330)), (next.Hard.State.Queue, next.Hard.State.RemainingSteps, next.Hard.State.DueAt));
        Assert.Equal((CardQueue.Learning, 1, Noon.AddSeconds(600)), (next.Good.State.Queue, next.Good.State.RemainingSteps, next.Good.State.DueAt));
        Assert.Equal(1, next.Good.State.Reps);

        // On the last step Hard repeats it and Good graduates with the graduating interval and the starting ease.
        var last = AnkiScheduler.Next(next.Good.State, At(Noon.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(10), last.Hard.Interval);
        Assert.Equal((CardQueue.Review, 1, Today.AddDays(1), 2.5f), (last.Good.State.Queue, last.Good.State.IntervalDays, last.Good.State.DueDay, last.Good.State.Ease));
        Assert.Null(last.Good.State.DueAt);
    }

    [Fact]
    public void Easy_graduates_a_new_word_at_once_for_four_days()
    {
        var easy = AnkiScheduler.Next(ReviewState.New("w"), At(Noon)).Easy.State;

        Assert.Equal((CardQueue.Review, 4, Today.AddDays(4)), (easy.Queue, easy.IntervalDays, easy.DueDay));
    }

    [Fact]
    public void Good_four_times_goes_ten_minutes_one_three_eight_twenty_days()
    {
        var s = ReviewState.New("w");
        var now = Noon;
        s = AnkiScheduler.Next(s, At(now)).Good.State;               // 10 min
        now = now.AddMinutes(10);
        s = AnkiScheduler.Next(s, At(now)).Good.State;               // graduates: 1 day
        var intervals = new List<int> { s.IntervalDays };
        for (var i = 0; i < 3; i++)
        {
            now = now.AddDays(s.IntervalDays);                       // answered on the day it is due
            s = AnkiScheduler.Next(s, At(now)).Good.State;
            intervals.Add(s.IntervalDays);
        }

        Assert.Equal(new[] { 1, 3, 8, 20 }, intervals);
        Assert.Equal(2.5f, s.Ease);
    }

    [Fact]
    public void Hard_lowers_the_ease_for_good_and_the_chain_grows_slower()
    {
        var s = ReviewState.New("w");
        var now = Noon;
        s = AnkiScheduler.Next(s, At(now)).Good.State;
        now = now.AddMinutes(10);
        s = AnkiScheduler.Next(s, At(now)).Good.State;               // 1 day
        now = now.AddDays(1);
        s = AnkiScheduler.Next(s, At(now)).Hard.State;               // 2 days, ease 2.35
        Assert.Equal((2, 2.35f), (s.IntervalDays, s.Ease));
        now = now.AddDays(2);
        s = AnkiScheduler.Next(s, At(now)).Good.State;               // 5 days
        Assert.Equal(5, s.IntervalDays);
        now = now.AddDays(5);
        Assert.Equal(12, AnkiScheduler.Next(s, At(now)).Good.State.IntervalDays);
    }

    [Fact]
    public void A_late_answer_counts_half_the_delay_for_good_and_all_of_it_for_easy()
    {
        var next = AnkiScheduler.Next(Review(10, Today.AddDays(-4)), At(Noon));

        Assert.Equal((12, 30, 46), (next.Hard.State.IntervalDays, next.Good.State.IntervalDays, next.Easy.State.IntervalDays));
        Assert.Equal((2.35f, 2.5f, 2.65f), (next.Hard.State.Ease, next.Good.State.Ease, next.Easy.State.Ease));
        Assert.False(next.Early);
    }

    [Fact]
    public void An_early_review_uses_the_filtered_deck_formulas_without_fuzz()
    {
        // Reviewed four days ago for ten days: due in six.
        var next = AnkiScheduler.Next(Review(10, Today.AddDays(6)), At(Noon, fuzz: true));

        Assert.True(next.Early);
        Assert.Equal((6, 10, 12), (next.Hard.State.IntervalDays, next.Good.State.IntervalDays, next.Easy.State.IntervalDays));
        Assert.Equal(Today.AddDays(10), next.Good.State.DueDay);
    }

    [Fact]
    public void Forgetting_a_learned_word_sends_it_to_relearning_for_ten_minutes()
    {
        var again = AnkiScheduler.Next(Review(10, Today, lapses: 2), At(Noon)).Again.State;

        Assert.Equal(CardQueue.Relearning, again.Queue);
        Assert.Equal(Noon.AddMinutes(10), again.DueAt);
        Assert.Equal((1, 3, 2.3f), (again.IntervalDays, again.Lapses, again.Ease));

        var floor = AnkiScheduler.Next(Review(10, Today, ease: 1.4f), At(Noon)).Again.State;
        Assert.Equal(1.3f, floor.Ease);
    }

    [Fact]
    public void Relearning_hard_waits_half_again_and_good_or_easy_return_to_reviews()
    {
        var relearning = AnkiScheduler.Next(Review(10, Today), At(Noon)).Again.State;
        var later = Noon.AddMinutes(10);
        var next = AnkiScheduler.Next(relearning, At(later, fuzz: true));

        Assert.Equal(TimeSpan.FromMinutes(15), next.Hard.Interval);
        Assert.Equal(CardQueue.Relearning, next.Hard.State.Queue);
        Assert.Equal((CardQueue.Review, 1, Today.AddDays(1)), (next.Good.State.Queue, next.Good.State.IntervalDays, next.Good.State.DueDay));
        Assert.Equal((CardQueue.Review, 2), (next.Easy.State.Queue, next.Easy.State.IntervalDays));
        Assert.Equal(1, next.Again.State.Lapses); // again while relearning is not another lapse
    }

    [Fact]
    public void A_leech_is_marked_at_eight_lapses_and_then_every_four()
    {
        Assert.Equal(new[] { 8, 12, 16 }, Enumerable.Range(1, 17).Where(n => AnkiScheduler.LeechThresholdMet(n, 8)));
        Assert.False(AnkiScheduler.LeechThresholdMet(8, 0));
        Assert.True(AnkiScheduler.Next(Review(10, Today, lapses: 7), At(Noon)).Again.State.Leech);
    }

    [Fact]
    public void Learning_past_four_in_the_morning_becomes_due_on_the_next_study_day()
    {
        var early = new DateTime(2026, 10, 2, 3, 55, 0, DateTimeKind.Utc); // still study day October 1
        var good = AnkiScheduler.Next(ReviewState.New("w"), At(early)).Good.State;

        Assert.Equal(CardQueue.Learning, good.Queue);
        Assert.Null(good.DueAt);
        Assert.Equal(new DateOnly(2026, 10, 2), good.DueDay);
    }

    [Fact]
    public void A_hard_step_longer_than_a_day_is_rounded_to_whole_days()
    {
        var config = new StudyConfig { LearnSteps = [1500] }; // 25 hours: hard is 37.5 hours
        var hard = AnkiScheduler.Next(ReviewState.New("w"), new SchedulerContext(config, Utc, Noon)).Hard;

        Assert.Equal(TimeSpan.FromDays(2), hard.Interval);
    }

    [Theory]
    [InlineData(7f, 5, 9)]
    [InlineData(17f, 14, 20)]
    [InlineData(37f, 33, 41)]
    [InlineData(4f, 3, 5)]
    [InlineData(2f, 2, 2)]
    public void Fuzz_ranges_match_anki(float interval, int lo, int hi)
    {
        Assert.Equal((lo, hi), Fuzz.Bounds(interval, 1, 36500));
    }

    [Fact]
    public void Fuzz_respects_the_minimum_and_widens_a_single_day_range()
    {
        Assert.Equal((3, 4), Fuzz.Bounds(2.5f, 3, 36500));
        Assert.Equal(3, Fuzz.Apply(2.5f, 3, 36500, 0f));
        Assert.Equal(4, Fuzz.Apply(2.5f, 3, 36500, 0.99f));
    }

    [Fact]
    public void Fuzzed_intervals_stay_in_range_and_repeat_for_the_same_word_and_answer_count()
    {
        var state = Review(7, Today) with { WordId = "abc" };
        var first = AnkiScheduler.Next(state, At(Noon, fuzz: true));
        var again = AnkiScheduler.Next(state, At(Noon, fuzz: true));

        Assert.Equal(first.Good.State.IntervalDays, again.Good.State.IntervalDays);
        foreach (var n in Enumerable.Range(0, 40))
        {
            var good = AnkiScheduler.Next(state with { WordId = $"w{n}" }, At(Noon, fuzz: true)).Good;
            Assert.InRange(good.State.IntervalDays, 15, 20); // 7 x 2.5 = 17.5, fuzzed 15..20
            Assert.Equal(TimeSpan.FromDays(good.State.IntervalDays), good.Interval);
        }
    }

    [Fact]
    public void A_reverse_card_is_fuzzed_apart_from_its_forward_card_which_keeps_the_old_seed()
    {
        Assert.Equal(StudySeed.Of("abc", 5), StudySeed.Of(new CardKey("abc", CardDirection.Forward), 5));
        Assert.NotEqual(StudySeed.Of("abc", 5), StudySeed.Of(new CardKey("abc", CardDirection.Reverse), 5));

        var apart = 0;
        foreach (var n in Enumerable.Range(0, 40))
        {
            var forward = Review(7, Today) with { WordId = $"w{n}" };
            var f = AnkiScheduler.Next(forward, At(Noon, fuzz: true)).Good.State;
            var r = AnkiScheduler.Next(forward with { Direction = CardDirection.Reverse }, At(Noon, fuzz: true)).Good.State;
            Assert.Equal(CardDirection.Reverse, r.Direction);
            Assert.InRange(r.IntervalDays, 15, 20);
            if (r.IntervalDays != f.IntervalDays) apart++;
        }
        Assert.True(apart >= 25, $"only {apart} of 40 words got different days"); // one in six would match by chance
    }

    [Fact]
    public void Load_balancing_prefers_an_empty_day_and_skips_far_intervals()
    {
        static int Due(int offset) => offset == 3 ? 0 : 5;
        var picks = Enumerable.Range(1, 50).Select(seed => LoadBalancer.Pick(3f, 1, 36500, Due, (ulong)seed)).ToList();

        Assert.True(picks.Count(p => p == 3) >= 45);
        Assert.All(picks, p => Assert.InRange(p!.Value, 2, 4));
        Assert.Null(LoadBalancer.Pick(100f, 1, 36500, Due, 1));
    }

    [Theory]
    [InlineData(30, "<30 с")]
    [InlineData(1500, "25 мин")]
    [InlineData(7200, "2 ч")]
    [InlineData(5400, "1,5 ч")]
    [InlineData(86400, "1 день")]
    [InlineData(5 * 86400, "5 дней")]
    [InlineData(21 * 86400, "21 день")]
    [InlineData(45 * 86400, "1,5 мес")]
    [InlineData(400 * 86400, "1,1 года")]
    [InlineData(730 * 86400, "2 года")]
    public void Button_labels_use_anki_units(int seconds, string label)
    {
        Assert.Equal(label, StudyLabels.Button(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void New_words_are_mixed_into_reviews_evenly()
    {
        Assert.Equal(new[] { 1, 11, 2, 22, 3, 33 }, Interleave.Mix(new[] { 1, 2, 3 }, new[] { 11, 22, 33 }));
        Assert.Equal(new[] { 1, 2, 11, 3, 4 }, Interleave.Mix(new[] { 1, 2, 3, 4 }, new[] { 11 }));
        Assert.Equal(new[] { 11, 22 }, Interleave.Mix(Array.Empty<int>(), new[] { 11, 22 }));
    }

    [Fact]
    public void A_study_day_starts_at_four_in_the_morning()
    {
        var night = new DateTime(2026, 10, 2, 3, 55, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 10, 1), Utc.Day(night));
        Assert.Equal(new DateTime(2026, 10, 2, 4, 0, 0, DateTimeKind.Utc), Utc.Rollover(night));
        Assert.Equal(new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc), Utc.Rollover(night.AddMinutes(5)));
    }
}
