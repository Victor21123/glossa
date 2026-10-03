using Glossa.Core.Companions;
using Glossa.Core.Library;

namespace Glossa.Tests.Companions;

public sealed class MoodTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly MoodRules Rules = new();

    private static DayActivity Did(int daysAgo, int points = 1, string kind = DayAction.Line) =>
        new(Today.AddDays(-daysAgo), new Dictionary<string, int> { [kind] = points });

    private static Mood Of(IReadOnlyList<DayActivity> days, int adoptedDaysAgo = 30, bool studyDone = false) =>
        Why(days, adoptedDaysAgo, studyDone).Mood;

    private static MoodNow Why(IReadOnlyList<DayActivity> days, int adoptedDaysAgo = 30, bool studyDone = false) =>
        CompanionMoods.Of(days, Today, goal: 10, Today.AddDays(-adoptedDaysAgo), Rules, studyDone);

    [Fact]
    public void Asleep_when_the_series_broke_and_nothing_is_done_today_and_any_action_wakes_it()
    {
        var broken = new[] { Did(5), Did(4), Did(3) };
        Assert.Equal(Mood.Asleep, Of(broken));
        Assert.Equal(Mood.Calm, Of([.. broken, Did(0)]));
    }

    [Fact]
    public void The_day_they_met_counts_so_a_new_companion_is_not_found_asleep()
    {
        var old = new[] { Did(20), Did(19) };
        Assert.Equal(Mood.Calm, Of(old, adoptedDaysAgo: 0));
        Assert.Equal(Mood.Calm, Of(old, adoptedDaysAgo: 1));
        // Met two days ago and nothing since: yesterday broke the series.
        Assert.Equal(Mood.Asleep, Of(old, adoptedDaysAgo: 2));
    }

    [Fact]
    public void Tired_from_fifty_answers_or_over_thirty_words_or_over_sixty_lines_each_on_its_own()
    {
        Assert.Equal(Mood.Happy, Of([Did(1), Did(0, 49, DayAction.Study)]));
        Assert.Equal((Mood.Tired, MoodReason.Answers), Pair(Why([Did(1), Did(0, 50, DayAction.Study)])));
        Assert.Equal(Mood.Happy, Of([Did(1), Did(0, 30, DayAction.Lookup)]));
        Assert.Equal((Mood.Tired, MoodReason.Words), Pair(Why([Did(1), Did(0, 31, DayAction.Lookup)])));
        Assert.Equal(Mood.Happy, Of([Did(1), Did(0, 60, DayAction.Zone)]));
        Assert.Equal((Mood.Tired, MoodReason.Lines), Pair(Why([Did(1), Did(0, 61, DayAction.Screen)])));
        // Live subtitles come by the hundred: they do not tire.
        Assert.Equal(Mood.Happy, Of([Did(1), Did(0, 300, DayAction.Live)]));
    }

    private static (Mood, MoodReason) Pair(MoodNow m) => (m.Mood, m.Reason);

    [Fact]
    public void Happy_when_the_days_study_is_done_even_before_a_full_day()
    {
        Assert.Equal((Mood.Happy, MoodReason.StudyDone), Pair(Why([Did(1), Did(0, 6, DayAction.Study)], studyDone: true)));
        Assert.Equal((Mood.Happy, MoodReason.FullDay), Pair(Why([Did(1), Did(0, 10, DayAction.Line)])));
    }

    [Fact]
    public void Sad_after_a_day_covered_by_a_freeze_until_something_is_done_today()
    {
        // Days 9..2 ago in a row earn a freeze; yesterday is missed and frozen.
        var days = Enumerable.Range(2, 8).Select(n => Did(n)).ToList();
        Assert.Equal(Mood.Sad, Of(days));
        Assert.Equal(Mood.Calm, Of([.. days, Did(0)]));
    }

    [Fact]
    public void Happy_on_a_full_day()
    {
        Assert.Equal(Mood.Calm, Of([Did(1), Did(0, 9)]));
        Assert.Equal(Mood.Happy, Of([Did(1), Did(0, 10)]));
    }

    [Fact]
    public void A_calm_breath_lasts_1_4_s_and_a_blink_sits_in_its_rest()
    {
        var plain = IdleTimeline.Breath(Mood.Calm, blink: false);
        Assert.Equal(new[] { (CompanionFrames.Base, 700), (CompanionFrames.Breath, 700) }, plain.Select(s => (s.Frame, s.Ms)));
        var blinking = IdleTimeline.Breath(Mood.Calm, blink: true);
        Assert.Equal(new[] { (CompanionFrames.Base, 350), (CompanionFrames.Blink, 150), (CompanionFrames.Base, 200),
            (CompanionFrames.Breath, 700) }, blinking.Select(s => (s.Frame, s.Ms)));
    }

    [Fact]
    public void Every_mood_breathes_with_its_own_face_and_the_sleep_holds_still()
    {
        Assert.Equal(new[] { (CompanionFrames.Sad, 700), ("sad_breath", 700) },
            IdleTimeline.Breath(Mood.Sad, blink: true).Select(s => (s.Frame, s.Ms)));
        Assert.Equal(new[] { CompanionFrames.Happy, "happy_breath" }, IdleTimeline.Breath(Mood.Happy, false).Select(s => s.Frame));
        var sleep = Assert.Single(IdleTimeline.Breath(Mood.Asleep, blink: true));
        Assert.Equal((CompanionFrames.Sleep, 0), (sleep.Frame, sleep.Ms));
    }

    [Fact]
    public void The_words_agree_with_the_characters_gender()
    {
        Assert.Equal("Легендарная", CompanionLabels.Rarity(Rarity.Legendary, "f"));
        Assert.Equal("Редкий", CompanionLabels.Rarity(Rarity.Rare, "m"));
        Assert.Equal("Устала: сегодня много перевода", CompanionLabels.Mood(new MoodNow(Mood.Tired, MoodReason.Lines), "f"));
        Assert.StartsWith("Устал:", CompanionLabels.Mood(new MoodNow(Mood.Tired, MoodReason.Words), "m"));
        Assert.Equal("Рад: учёба на сегодня пройдена", CompanionLabels.Mood(new MoodNow(Mood.Happy, MoodReason.StudyDone), "m"));
        Assert.Equal(("Вместе 1 день", "Вместе 3 дня", "Вместе 11 дней"),
            (CompanionLabels.Together(1), CompanionLabels.Together(3), CompanionLabels.Together(11)));
    }

    [Fact]
    public void About_one_rest_in_four_blinks_and_never_two_in_a_row()
    {
        var rng = new Random(7);
        var last = false;
        var blinks = 0;
        for (var i = 0; i < 3000; i++)
        {
            var now = IdleTimeline.Blinks(rng, last);
            Assert.False(now && last);
            blinks += now ? 1 : 0;
            last = now;
        }
        Assert.InRange(blinks, 650, 850);
    }
}
