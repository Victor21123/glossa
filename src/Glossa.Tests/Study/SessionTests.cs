using Glossa.Core.Library;
using Glossa.Core.Study;

namespace Glossa.Tests.Study;

public class SessionTests
{
    private static readonly StudyClock Utc = new(TimeZoneInfo.Utc);
    private static readonly DateTime Noon = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static SavedWord Word(string id, bool pinned = false, int lookups = 1, string language = "en", int seenDaysAgo = 1) => new()
    {
        Id = id, Language = language, Word = id, Pinned = pinned, Lookups = lookups, CreatedUtc = Noon.AddDays(-seenDaysAgo),
        PinnedUtc = pinned ? Noon.AddDays(-30) : null,
    };

    private static ReviewState Due(string id, int daysAgo, CardDirection direction = CardDirection.Forward) => new()
    {
        WordId = id, Direction = direction, Queue = CardQueue.Review, IntervalDays = 5, DueDay = Today.AddDays(-daysAgo), Ease = 2.5f, Reps = 3,
    };

    private static StudyPlan Plan(IReadOnlyList<SavedWord> words, IReadOnlyList<ReviewState> states, IReadOnlyList<ReviewAnswer>? today = null,
        StudyLimits? limits = null, Func<SavedWord, bool>? include = null) =>
        SessionBuilder.Build(words, states.ToDictionary(s => s.Key), today ?? [], limits ?? new StudyLimits(), new StudyConfig(), Utc, Noon, include);

    private static StudyLimits Both(bool bury = true) => new(Size: 100, Direction: StudyDirection.Both, BurySiblings: bury);

    /// <summary>"w>" for the forward card of w, "w&lt;" for its reverse one.</summary>
    private static string Name(SessionCard card) => card.Word.Id + (card.Direction == CardDirection.Reverse ? "<" : ">");

    private static ReviewState Stepping(string id, DateTime due) => new()
    {
        WordId = id, Queue = CardQueue.Learning, RemainingSteps = 1, DueAt = due, Reps = 1, AnsweredUtc = Noon.AddHours(-1),
    };

    private static ReviewAnswer StartedToday(string id) => new()
    {
        WordId = id, AnsweredUtc = Noon.AddHours(-1), Rating = Rating.Good, QueueBefore = CardQueue.New,
    };

    [Fact]
    public void A_session_takes_pinned_words_up_to_half_then_due_ones_then_new_ones()
    {
        var words = Enumerable.Range(0, 12).Select(i => Word($"p{i}", pinned: true))
            .Concat(Enumerable.Range(0, 8).Select(i => Word($"d{i}")))
            .Concat(Enumerable.Range(0, 30).Select(i => Word($"n{i}", lookups: i % 4 + 1, seenDaysAgo: i)))
            .ToList();
        var plan = Plan(words, Enumerable.Range(0, 8).Select(i => Due($"d{i}", i)).ToList());

        Assert.Equal((10, 8, 2), (plan.Pinned.Count, plan.Due.Count, plan.New.Count));
        Assert.Equal(20, plan.Cards.Count);
        Assert.All(plan.New, c => Assert.Equal(4, c.Word.Lookups));              // looked up most often first
        Assert.Equal(new[] { "n3", "n7" }, plan.New.Select(c => c.Word.Id));     // then the most recently met
        Assert.Equal(new[] { "d7", "d6", "d5" }, plan.Due.Take(3).Select(c => c.Word.Id)); // due longest ago first
    }

    [Fact]
    public void New_words_count_against_the_daily_limit_across_sessions()
    {
        var words = Enumerable.Range(0, 10).Select(i => Word($"n{i}")).ToList();
        var today = Enumerable.Range(0, 18).Select(i => new ReviewAnswer
        {
            WordId = $"old{i}", AnsweredUtc = Noon.AddHours(-1), Rating = Rating.Good, QueueBefore = CardQueue.New,
        }).ToList();

        var plan = Plan(words, [], today);

        Assert.Equal(2, plan.New.Count);
        Assert.Equal(18, plan.NewToday);
    }

    [Fact]
    public void New_words_can_come_by_recency_or_in_a_random_order_that_holds_all_day()
    {
        var words = new[] { Word("old", lookups: 5, seenDaysAgo: 9), Word("fresh", seenDaysAgo: 0), Word("mid", seenDaysAgo: 4) };

        Assert.Equal("old", Plan(words, []).New[0].Word.Id);
        Assert.Equal("fresh", Plan(words, [], limits: new StudyLimits(NewOrder: NewWordOrder.Recent)).New[0].Word.Id);
        var random = new StudyLimits(NewOrder: NewWordOrder.Random);
        Assert.Equal(Plan(words, [], limits: random).New.Select(c => c.Word.Id), Plan(words, [], limits: random).New.Select(c => c.Word.Id));
    }

    [Fact]
    public void Default_study_settings_are_ankis()
    {
        var settings = new Glossa.Core.Config.StudySettings();
        var config = settings.Config();

        Assert.Equal(new[] { 1f, 10f }, config.LearnSteps);
        Assert.Equal(new[] { 10f }, config.RelearnSteps);
        Assert.Equal((1, 4, 2.5f), (config.GraduatingInterval, config.EasyInterval, config.StartingEase));
        Assert.Equal((1.3f, 1.2f, 1f, 36500), (config.EasyBonus, config.HardMultiplier, config.IntervalModifier, config.MaximumInterval));
        Assert.Equal(new StudyLimits(), settings.Limits());
        Assert.Equal((0.5, StudyDirection.Forward, true), (settings.Limits().PinnedShare, settings.Limits().Direction, settings.Limits().BurySiblings));
    }

    [Fact]
    public void Study_settings_are_held_to_ankis_ranges()
    {
        var high = new Glossa.Core.Config.StudySettings
        {
            StartingEase = 9f, EasyBonus = 9f, HardMultiplier = 2f, IntervalModifier = 3f, MaximumInterval = 99_999, PinnedShare = 1.7,
            Direction = "both", BurySiblings = false, LearnSteps = [5, -1, 30, float.PositiveInfinity],
        };
        var low = new Glossa.Core.Config.StudySettings
        {
            StartingEase = 1f, EasyBonus = 0.5f, HardMultiplier = 0.1f, IntervalModifier = 0.1f, MaximumInterval = 0, PinnedShare = -1,
            Direction = "sideways", LearnSteps = [-1, float.NaN, 0], RelearnSteps = [], SessionSize = 0, NewPerDay = -5,
        };

        var (h, l) = (high.Config(), low.Config());
        Assert.Equal((5f, 5f, 1.3f, 2f, 36500), (h.StartingEase, h.EasyBonus, h.HardMultiplier, h.IntervalModifier, h.MaximumInterval));
        Assert.Equal((1.31f, 1f, 0.5f, 0.5f, 1), (l.StartingEase, l.EasyBonus, l.HardMultiplier, l.IntervalModifier, l.MaximumInterval));
        Assert.Equal(new[] { 5f, 30f }, h.LearnSteps);   // the usable steps stay
        Assert.Equal(new[] { 1f, 10f }, l.LearnSteps);   // nothing usable: Anki's steps
        Assert.Empty(l.RelearnSteps);                    // no steps on purpose: Anki allows it

        Assert.Equal((1.0, StudyDirection.Both, false), (high.Limits().PinnedShare, high.Limits().Direction, high.Limits().BurySiblings));
        Assert.Equal((0.0, StudyDirection.Forward), (low.Limits().PinnedShare, low.Limits().Direction));
        Assert.Equal((1, 0), (low.Limits().Size, low.Limits().NewPerDay));
        Assert.Equal(StudyDirection.Reverse, new Glossa.Core.Config.StudySettings { Direction = "reverse" }.Limits().Direction);
    }

    [Fact]
    public void Reminder_times_that_are_not_a_time_of_day_are_skipped()
    {
        var settings = new Glossa.Core.Config.StudySettings { ReminderTimes = ["20:00", "7:30", "25:00", "evening", " 18:00 ", "18:00", ""] };

        Assert.Equal(new[] { new TimeOnly(7, 30), new TimeOnly(18, 0), new TimeOnly(20, 0) }, settings.ReminderSchedule());
        Assert.Empty(new Glossa.Core.Config.StudySettings { Reminders = false }.ReminderSchedule());
        Assert.Equal(new[] { new TimeOnly(18, 0), new TimeOnly(20, 0) }, new Glossa.Core.Config.StudySettings().ReminderSchedule());
    }

    [Fact]
    public void Both_directions_give_a_word_two_cards_and_one_direction_one()
    {
        var words = new[] { Word("a"), Word("b", lookups: 2) };

        Assert.Equal(new[] { "b>", "b<", "a>", "a<" }, Plan(words, [], limits: Both(bury: false)).New.Select(Name));
        Assert.Equal(new[] { "b>", "a>" }, Plan(words, []).New.Select(Name));
        Assert.Equal(new[] { "b<", "a<" }, Plan(words, [], limits: new StudyLimits(Direction: StudyDirection.Reverse)).New.Select(Name));
    }

    [Fact]
    public void Siblings_are_buried_one_card_a_word_pinned_then_due_then_new_forward_on_a_tie()
    {
        var words = new[] { Word("p", pinned: true), Word("a"), Word("b"), Word("c"), Word("d"), Word("l") };
        var states = new[]
        {
            Due("a", 1),                                        // a: due forward, new reverse
            Due("b", 0, CardDirection.Reverse),                 // b: new forward, due reverse
            Due("d", 2), Due("d", 2, CardDirection.Reverse),    // d: both due on the same day
            Stepping("l", Noon.AddMinutes(-1)), Due("l", 3, CardDirection.Reverse), // l: a step due beats an older review
        };

        var plan = Plan(words, states, limits: Both());

        Assert.Equal(new[] { "p>" }, plan.Pinned.Select(Name));
        Assert.Equal(new[] { "l>", "d>", "a>", "b<" }, plan.Due.Select(Name));
        Assert.Equal(new[] { "c>" }, plan.New.Select(Name));
        Assert.Equal(words.Length, plan.Cards.Count);
        Assert.Equal(12, Plan(words, states, limits: Both(bury: false)).Cards.Count);
    }

    [Fact]
    public void Burying_is_per_session_so_a_word_waiting_on_a_step_may_give_its_other_card()
    {
        var words = new[] { Word("x"), Word("y"), Word("p", pinned: true) };
        var states = new[] { Stepping("x", Noon.AddMinutes(5)), Stepping("y", Noon.AddMinutes(-1)), Stepping("p", Noon.AddMinutes(5)) };

        var plan = Plan(words, states, [StartedToday("x"), StartedToday("y"), StartedToday("p")], Both());

        Assert.Equal(new[] { "y>" }, plan.Due.Select(Name));     // y's step is due: its reverse card waits
        Assert.Equal(new[] { "x<" }, plan.New.Select(Name));     // x's step is not: its reverse card comes as a new one
        Assert.Equal(new[] { "p<" }, plan.Pinned.Select(Name));  // a pinned word: the card studied longest ago
    }

    [Fact]
    public void The_new_card_limit_counts_cards_in_both_directions()
    {
        var words = Enumerable.Range(0, 20).Select(i => Word($"n{i:00}")).ToList();
        var fresh = Plan(words, [], limits: Both(bury: false)).New;
        Assert.Equal((20, 10), (fresh.Count, fresh.Select(c => c.Word.Id).Distinct().Count())); // 20 new cards: 10 words
        Assert.Equal(20, Plan(words, [], limits: Both()).New.Select(c => c.Word.Id).Distinct().Count()); // buried: 20 words

        // Ten cards started today: ten more new cards, whichever words they come from.
        var started = Enumerable.Range(0, 10).Select(i => $"n{i:00}").ToList();
        var plan = Plan(words, started.Select(id => Stepping(id, Noon.AddMinutes(30))).ToList(), started.Select(StartedToday).ToList(), Both());

        Assert.Equal((10, 10), (plan.NewToday, plan.New.Count));
        Assert.Equal(10, plan.New.Select(c => c.Word.Id).Distinct().Count());
    }

    [Fact]
    public void Switching_direction_picks_up_that_directions_own_schedule()
    {
        var words = new[] { Word("w") };
        var states = new[] { Due("w", 1), Due("w", 3, CardDirection.Reverse) with { IntervalDays = 9 } };

        var forward = Plan(words, states).Cards.Single();
        var reverse = Plan(words, states, limits: new StudyLimits(Direction: StudyDirection.Reverse)).Cards.Single();

        Assert.Equal((CardDirection.Forward, 5), (forward.Direction, forward.State.IntervalDays));
        Assert.Equal((CardDirection.Reverse, 9), (reverse.Direction, reverse.State.IntervalDays));
        Assert.Equal(new CardKey("w", CardDirection.Reverse), reverse.Key);
    }

    [Fact]
    public void A_reverse_card_is_answered_and_saved_as_reverse()
    {
        var saved = new List<(ReviewState State, ReviewAnswer Answer)>();
        var plan = Plan([Word("r")], [], limits: new StudyLimits(Direction: StudyDirection.Reverse));
        var session = new StudySession(plan, new Dictionary<CardKey, ReviewState>(), new StudyConfig(), Utc, (s, a) => saved.Add((s, a)), new Random(1));

        Assert.Equal(CardDirection.Reverse, session.Next(Noon)!.Direction);
        session.Answer(Rating.Easy, Noon, 1000);

        var (state, answer) = saved.Single();
        Assert.Equal((new CardKey("r", CardDirection.Reverse), CardQueue.Review), (state.Key, state.Queue));
        Assert.Equal(new CardKey("r", CardDirection.Reverse), answer.Key);
    }

    [Fact]
    public void Reviews_get_new_words_mixed_in_and_pinned_words_spread_through()
    {
        var words = new[] { Word("d0"), Word("d1"), Word("n0"), Word("n1"), Word("p0", pinned: true) };
        var plan = Plan(words, [Due("d0", 2), Due("d1", 1)]);

        Assert.Equal(new[] { "d0", "n0", "p0", "d1", "n1" }, plan.Cards.Select(c => c.Word.Id));
    }

    [Fact]
    public void Learning_steps_that_are_due_come_first_and_the_filter_limits_the_words()
    {
        var learning = new ReviewState { WordId = "l0", Queue = CardQueue.Learning, RemainingSteps = 1, DueAt = Noon.AddMinutes(-1), Reps = 1 };
        var words = new[] { Word("d0"), Word("l0"), Word("ja0", language: "ja") };
        var plan = Plan(words, [Due("d0", 0), learning], include: w => w.Language == "en");

        Assert.Equal(new[] { "l0", "d0" }, plan.Cards.Select(c => c.Word.Id));
    }

    [Fact]
    public void A_word_answered_good_comes_back_after_its_step_and_then_leaves()
    {
        var saved = new List<(ReviewState State, ReviewAnswer Answer)>();
        var plan = Plan([Word("n0"), Word("n1")], []);
        var session = new StudySession(plan, new Dictionary<CardKey, ReviewState>(), new StudyConfig(), Utc,
            (s, a) => saved.Add((s, a)), new Random(1));

        Assert.Equal("n0", session.Next(Noon)!.Word.Id);
        session.Answer(Rating.Good, Noon, 3000);
        Assert.Equal("n1", session.Next(Noon)!.Word.Id);
        session.Answer(Rating.Easy, Noon, 3000);                      // graduates: leaves the session
        Assert.Equal(1, session.Done);
        var back = session.Next(Noon.AddMinutes(1));                    // nothing else left: learn ahead 20 minutes
        Assert.Equal("n0", back!.Word.Id);
        Assert.Equal("<10 мин", StudyLabels.Button(session.Choices(Noon.AddMinutes(1)).Hard.Interval));
        session.Answer(Rating.Good, Noon.AddMinutes(1), 2000);
        Assert.Null(session.Next(Noon.AddMinutes(2)));
        Assert.Equal((2, 2), (session.Done, session.Total));
        Assert.Equal(3, saved.Count);
        Assert.Equal(CardQueue.Review, saved[^1].State.Queue);
    }

    private sealed class NoJitter : Random
    {
        public override float NextSingle() => 0f;
    }

    [Fact]
    public void With_nothing_else_left_the_word_just_answered_waits_behind_the_next_step()
    {
        var plan = Plan([Word("b", lookups: 2), Word("a")], []);
        var session = new StudySession(plan, new Dictionary<CardKey, ReviewState>(), new StudyConfig(), Utc, (_, _) => { }, new NoJitter());

        Assert.Equal("b", session.Next(Noon)!.Word.Id);
        session.Answer(Rating.Hard, Noon, 1000);             // b: 5.5 minutes
        Assert.Equal("a", session.Next(Noon)!.Word.Id);
        session.Answer(Rating.Again, Noon, 1000);            // a: 1 minute, but it goes after b (Anki's requeue)

        Assert.Equal("b", session.Next(Noon.AddSeconds(1))!.Word.Id);
        session.Answer(Rating.Good, Noon.AddSeconds(1), 1000);
        Assert.Equal("a", session.Next(Noon.AddSeconds(2))!.Word.Id);
    }

    [Fact]
    public void Unpinning_is_suggested_after_three_right_answers_on_different_days()
    {
        var word = Word("p0", pinned: true) with { PinnedUtc = Noon.AddDays(-10) };
        ReviewAnswer At(int daysAgo, Rating rating) => new()
        {
            WordId = "p0", AnsweredUtc = Noon.AddDays(-daysAgo), Rating = rating, QueueBefore = CardQueue.Review,
        };

        Assert.False(StudyPins.SuggestUnpin(word, [At(3, Rating.Good), At(3, Rating.Good), At(2, Rating.Good)], Utc));
        Assert.False(StudyPins.SuggestUnpin(word, [At(20, Rating.Good), At(3, Rating.Good), At(2, Rating.Again)], Utc));
        Assert.True(StudyPins.SuggestUnpin(word, [At(3, Rating.Hard), At(2, Rating.Good), At(1, Rating.Easy)], Utc));
        Assert.Equal(2, StudyPins.RightDays(word, [At(3, Rating.Good), At(3, Rating.Easy), At(1, Rating.Good)], Utc));
    }

    [Fact]
    public void Stats_count_days_in_a_row_what_was_remembered_and_time_per_card()
    {
        ReviewAnswer At(int daysAgo, Rating rating, CardQueue queue = CardQueue.Review) => new()
        {
            WordId = "w", AnsweredUtc = Noon.AddDays(-daysAgo), Rating = rating, QueueBefore = queue, TakenMs = 12_000,
        };
        var answers = new[]
        {
            At(9, Rating.Good), At(8, Rating.Good),                                  // an older series of two
            At(3, Rating.Good), At(2, Rating.Again), At(1, Rating.Good), At(1, Rating.Good, CardQueue.New),
        };

        var stats = StudyStats.Of(answers, Utc, Noon);

        Assert.Equal((3, 3), (stats.Streak, stats.BestStreak));   // yesterday back to three days ago; today is not over
        Assert.Equal(4, stats.WeekAnswers);
        Assert.Equal(67, stats.WeekRetention);                     // reviews only: 2 of 3 remembered
        Assert.Equal(12, stats.SecondsPerCard);
        Assert.Equal(18, StudyStats.Empty.SecondsPerCard);         // no history yet: 18 s per card
    }

    // ---- "Ещё N слов": a portion past the daily new limit ----

    private static IReadOnlyList<ReviewAnswer> StartedMany(int n) => Enumerable.Range(0, n).Select(i => StartedToday($"old{i}")).ToList();

    private static StudyPlan More(IReadOnlyList<SavedWord> words, IReadOnlyList<ReviewState> states, IReadOnlyList<ReviewAnswer>? today = null) =>
        Plan(words, states, today, new StudyLimits().More());

    [Fact]
    public void An_extra_portion_takes_new_words_past_the_daily_limit()
    {
        var words = Enumerable.Range(0, 50).Select(i => Word($"n{i}")).ToList();
        var today = StartedMany(20);

        Assert.Empty(Plan(words, [], today).New);                  // the regular plan is spent
        var more = More(words, [], today);

        Assert.Equal(20, more.New.Count);
        Assert.Equal(20, more.Cards.Count);
        Assert.Equal(20, more.NewToday);                           // the counter itself is not touched
    }

    [Fact]
    public void An_extra_portion_shows_due_reviews_before_new_words()
    {
        var words = Enumerable.Range(0, 5).Select(i => Word($"d{i}")).Concat(Enumerable.Range(0, 30).Select(i => Word($"n{i}"))).ToList();
        var states = Enumerable.Range(0, 5).Select(i => Due($"d{i}", i + 1)).ToList();

        var more = More(words, states, StartedMany(20));

        Assert.Equal(5, more.Due.Count);
        Assert.Equal(15, more.New.Count);                          // the portion is 20 cards in all
        Assert.Equal(20, more.Cards.Count);
        Assert.Equal("d4", more.Due[0].Word.Id);                   // due longest ago first
    }

    [Fact]
    public void Extra_portions_work_again_and_again_in_one_day()
    {
        var words = Enumerable.Range(0, 100).Select(i => Word($"n{i}")).ToList();

        var more = More(words, [], StartedMany(40));               // two portions already taken, the limit is 20

        Assert.Equal(20, more.New.Count);
        Assert.Equal(20, More(words, [], StartedMany(60)).New.Count);
    }

    [Fact]
    public void An_extra_portion_leaves_pinned_words_to_the_regular_session()
    {
        var words = Enumerable.Range(0, 5).Select(i => Word($"p{i}", pinned: true)).Concat(Enumerable.Range(0, 30).Select(i => Word($"n{i}"))).ToList();

        var more = More(words, [], StartedMany(20));

        Assert.Empty(more.Pinned);
        Assert.DoesNotContain(more.Cards, c => c.Word.Pinned);
        Assert.Equal(20, more.New.Count);
    }

    [Fact]
    public void An_extra_portion_with_nothing_new_has_only_what_is_due()
    {
        var words = new[] { Word("a"), Word("b") };
        var states = new[] { Due("a", 1), Due("b", 0) };

        var more = More(words, states, StartedMany(20));

        Assert.Empty(more.New);
        Assert.Equal(2, more.Due.Count);
    }

    [Fact]
    public void An_extra_portion_caps_due_reviews_but_not_the_extra_new_cards()
    {
        var words = Enumerable.Range(0, 10).Select(i => Word($"d{i}")).Concat(Enumerable.Range(0, 30).Select(i => Word($"n{i}"))).ToList();
        var states = Enumerable.Range(0, 10).Select(i => Due($"d{i}", 1)).ToList();
        var limits = new StudyLimits(ReviewsPerDay: 4).More();

        var plan = Plan(words, states, StartedMany(20), limits);

        Assert.Equal(4, plan.Due.Count);
        Assert.Equal(16, plan.New.Count);
    }

    [Fact]
    public void More_keeps_the_filters_of_the_regular_limits_and_only_changes_the_portion()
    {
        var limits = new StudyLimits(Size: 30, NewPerDay: 5, PinnedShare: 0.5, NewOrder: NewWordOrder.Recent, Direction: StudyDirection.Both);

        var more = limits.More();

        Assert.Equal((30, 30, 0.0), (more.Size, more.ExtraNew, more.PinnedShare));
        Assert.Equal((5, NewWordOrder.Recent, StudyDirection.Both), (more.NewPerDay, more.NewOrder, more.Direction));
        Assert.Equal(0, limits.ExtraNew);
    }

    [Fact]
    public void More_is_offered_once_the_new_limit_is_used_and_new_words_remain()
    {
        var words = Enumerable.Range(0, 50).Select(i => Word($"n{i}")).ToList();
        var fresh = StudyOffer.Of(Plan(words, []), More(words, []));
        var spent = StudyOffer.Of(Plan(words, [], StartedMany(20)), More(words, [], StartedMany(20)));
        var few = words.Take(3).ToList();
        var allStarted = StudyOffer.Of(Plan(few, [], StartedMany(20)), More(few, [], StartedMany(20)));
        var rest = Enumerable.Range(0, 3).Select(i => new ReviewState { WordId = $"n{i}", Queue = CardQueue.Review, IntervalDays = 3, DueDay = Today.AddDays(2), Reps = 2 }).ToList();
        var nothingLeft = StudyOffer.Of(Plan(few, rest, StartedMany(20)), More(few, rest, StartedMany(20)));

        Assert.False(fresh.ShowMore);                              // new words are still in the regular plan
        Assert.True(spent.ShowMore);
        Assert.True(allStarted.ShowMore);                          // 3 unstudied words remain
        Assert.False(nothingLeft.ShowMore);                        // all in study: nothing to offer
    }

    [Fact]
    public void Space_starts_more_only_when_nothing_is_scheduled()
    {
        var words = Enumerable.Range(0, 50).Select(i => Word($"n{i}")).Concat([Word("d")]).ToList();
        var today = StartedMany(20);

        var empty = StudyOffer.Of(Plan(words.Take(50).ToList(), [], today), More(words.Take(50).ToList(), [], today));
        var due = StudyOffer.Of(Plan(words, [Due("d", 1)], today), More(words, [Due("d", 1)], today));
        var none = StudyOffer.Of(Plan([], []), More([], []));

        Assert.Equal(StudyStart.More, empty.Space);
        Assert.True(due.ShowMore);                                 // offered beside "Начать"...
        Assert.Equal(StudyStart.Regular, due.Space);               // ...but Space keeps starting the regular session
        Assert.Equal(StudyStart.None, none.Space);
        Assert.False(none.ShowMore);
    }

    [Fact]
    public void Pinned_and_due_cards_filling_the_session_do_not_offer_more_while_new_words_are_still_free()
    {
        var words = Enumerable.Range(0, 10).Select(i => Word($"p{i}", pinned: true)).Concat(Enumerable.Range(0, 10).Select(i => Word($"d{i}")))
            .Concat(Enumerable.Range(0, 40).Select(i => Word($"n{i}"))).ToList();
        var states = Enumerable.Range(0, 10).Select(i => Due($"d{i}", 1)).ToList();

        var regular = Plan(words, states);
        var offer = StudyOffer.Of(regular, More(words, states));

        Assert.Equal((20, 0, 20), (regular.Cards.Count, regular.New.Count, regular.NewLeft)); // the limit is untouched
        Assert.False(offer.ShowMore);
        Assert.Equal(StudyStart.Regular, offer.Space);
    }

    [Fact]
    public void More_is_offered_when_the_review_limit_closed_the_regular_new_words()
    {
        var words = Enumerable.Range(0, 30).Select(i => Word($"n{i}")).ToList();
        var regular = Plan(words, [], limits: new StudyLimits(ReviewsPerDay: 0));

        Assert.Equal(0, regular.NewLeft);
        Assert.True(StudyOffer.Of(regular, SessionBuilder.Build(words, new Dictionary<CardKey, ReviewState>(), [], new StudyLimits(ReviewsPerDay: 0).More(),
            new StudyConfig(), Utc, Noon)).ShowMore);
    }

    [Fact]
    public void An_extra_portion_gives_a_full_size_when_the_day_is_already_over_the_limit()
    {
        var words = Enumerable.Range(0, 50).Select(i => Word($"n{i}")).ToList();

        Assert.Equal(20, More(words, [], StartedMany(25)).New.Count);
    }

    [Fact]
    public void An_extra_portion_keeps_the_new_word_order()
    {
        var words = new[] { Word("old", lookups: 5, seenDaysAgo: 9), Word("fresh", seenDaysAgo: 0), Word("mid", seenDaysAgo: 4) };

        var recent = Plan(words, [], StartedMany(20), new StudyLimits(NewOrder: NewWordOrder.Recent).More());
        var random = Plan(words, [], StartedMany(20), new StudyLimits(NewOrder: NewWordOrder.Random).More());
        var regularRandom = Plan(words, [], limits: new StudyLimits(NewOrder: NewWordOrder.Random));

        Assert.Equal(new[] { "fresh", "mid", "old" }, recent.New.Select(c => c.Word.Id));
        Assert.Equal(regularRandom.New.Select(c => c.Word.Id), random.New.Select(c => c.Word.Id));
    }

    [Fact]
    public void An_extra_portion_with_both_directions_gives_one_card_per_word_unless_burying_is_off()
    {
        var words = Enumerable.Range(0, 6).Select(i => Word($"n{i}")).ToList();

        var buried = Plan(words, [], StartedMany(20), Both().More());
        var open = Plan(words, [], StartedMany(20), Both(bury: false).More());

        Assert.Equal(6, buried.New.Select(c => c.Word.Id).Distinct().Count());
        Assert.Equal(6, buried.Cards.Count);
        Assert.Equal(12, open.Cards.Count);
    }

    [Fact]
    public void A_learning_card_takes_room_in_an_extra_portion()
    {
        var words = new[] { Word("l") }.Concat(Enumerable.Range(0, 10).Select(i => Word($"n{i}"))).ToList();

        var more = Plan(words, [Stepping("l", Noon.AddMinutes(-1))], StartedMany(20), new StudyLimits(Size: 3).More());

        Assert.Equal(3, more.Cards.Count);
        Assert.Equal(("l", 2), (more.Cards[0].Word.Id, more.New.Count));   // the step first, then 2 new cards
    }

    [Fact]
    public void A_portion_card_answered_Again_returns_in_the_session_and_in_the_next_plan_while_the_new_counter_stays_spent()
    {
        var words = Enumerable.Range(0, 5).Select(i => Word($"n{i}")).ToList();
        var limits = new StudyLimits(Size: 3);
        var plan = Plan(words, [], StartedMany(20), limits.More());
        var saved = new List<(ReviewState State, ReviewAnswer Answer)>();
        var session = new StudySession(plan, new Dictionary<CardKey, ReviewState>(), new StudyConfig(), Utc, (s, a) => saved.Add((s, a)), new NoJitter());

        var first = session.Next(Noon)!.Word.Id;
        session.Answer(Rating.Again, Noon, 1000);
        session.Next(Noon);
        session.Answer(Rating.Good, Noon, 1000);
        session.Next(Noon);
        session.Answer(Rating.Good, Noon, 1000);

        Assert.Equal(first, session.Next(Noon.AddSeconds(5))!.Word.Id);      // main queue empty: the step comes back (learn-ahead)

        var later = Noon.AddMinutes(2);
        var states = saved.Select(x => x.State).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Last());
        var spent = StartedMany(20).Concat(saved.Select(x => x.Answer)).ToList();
        var next = SessionBuilder.Build(words, states, spent, limits, new StudyConfig(), Utc, later);

        Assert.Equal(new[] { first }, next.Due.Select(c => c.Word.Id));      // listed as learning
        Assert.Empty(next.New);                                              // the counter stays spent
        Assert.Equal(0, next.NewLeft);
    }

    [Fact]
    public void More_is_not_offered_to_someone_who_chose_no_new_words_a_day()
    {
        var words = Enumerable.Range(0, 30).Select(i => Word($"n{i}")).ToList();
        var limits = new StudyLimits(NewPerDay: 0);

        var offer = StudyOffer.Of(Plan(words, [], limits: limits), Plan(words, [], limits: limits.More()));

        Assert.False(offer.ShowMore);
        Assert.Equal(StudyStart.None, offer.Space);
    }

    [Fact]
    public void Unstudied_cards_are_counted_with_pinned_ones_so_the_all_in_study_note_is_true()
    {
        var pinnedOnly = Plan([Word("p", pinned: true)], []);
        var allStarted = Plan([Word("a"), Word("b")], [Due("a", 0), Due("b", 3)]);

        Assert.Equal(1, pinnedOnly.Unstudied);
        Assert.Empty(More([Word("p", pinned: true)], []).New);               // the portion alone would say "nothing new"
        Assert.Equal(0, allStarted.Unstudied);
    }
}
