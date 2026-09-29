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

    private static ReviewState Due(string id, int daysAgo) => new()
    {
        WordId = id, Queue = CardQueue.Review, IntervalDays = 5, DueDay = Today.AddDays(-daysAgo), Ease = 2.5f, Reps = 3,
    };

    private static StudyPlan Plan(IReadOnlyList<SavedWord> words, IReadOnlyList<ReviewState> states, IReadOnlyList<ReviewAnswer>? today = null,
        StudyLimits? limits = null, Func<SavedWord, bool>? include = null) =>
        SessionBuilder.Build(words, states.ToDictionary(s => s.WordId), today ?? [], limits ?? new StudyLimits(), new StudyConfig(), Utc, Noon, include);

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
        Assert.Equal(new StudyLimits(), settings.Limits());
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
        var session = new StudySession(plan, new Dictionary<string, ReviewState>(), new StudyConfig(), Utc,
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
        var session = new StudySession(plan, new Dictionary<string, ReviewState>(), new StudyConfig(), Utc, (_, _) => { }, new NoJitter());

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
}
