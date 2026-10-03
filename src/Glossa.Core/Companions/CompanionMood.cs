using Glossa.Core.Library;

namespace Glossa.Core.Companions;

/// <summary>How the companion feels now; each mood has its own face (and the sleep its own pose).</summary>
public enum Mood
{
    Calm,
    Happy,
    Sad,
    Tired,
    Asleep,
}

/// <summary>Why the companion feels so, for the words under it.</summary>
public enum MoodReason
{
    None,
    FullDay,
    StudyDone,
    Words,
    Lines,
    Answers,
}

/// <summary>The mood and its reason.</summary>
public sealed record MoodNow(Mood Mood, MoodReason Reason);

/// <summary>
/// Thresholds of tiredness in a day, each kind on its own (user, 2026-10-03): more than <see cref="TiredWords"/> words
/// looked up, more than <see cref="TiredLines"/> lines translated (Реплика, Зона, Весь экран - live subtitles come by
/// the hundred and do not count), or <see cref="TiredAnswers"/> study answers and more (the user's own words: "больше
/// 30 слов", "больше 60 реплик", "50 ответов").
/// </summary>
public sealed record MoodRules(int TiredWords = 30, int TiredLines = 60, int TiredAnswers = 50);

/// <summary>
/// The mood from the days with Glossa - one series for every mode (decided 2026-09-30), so the companion lives in
/// "Только перевод" too. In this order: asleep - the series broke and nothing is done today (it wakes at any action,
/// it never leaves); tired - much more than the day's norm of one kind (<see cref="MoodRules"/>); sad - yesterday was
/// missed and only a freeze saved the series, and nothing is done yet today; happy - the day's norm met, or the day's
/// study done (<paramref name="studyDone"/>, the dictionary mode); else calm. Happy and tired are apart: the norm is
/// 10 actions, tiredness starts far above it (user, 2026-10-03). The day they met counts as a day with Glossa: a
/// library from before the companions has a broken series, and a new companion must not be found asleep.
/// </summary>
public static class CompanionMoods
{
    private const string Met = "met";

    public static MoodNow Of(IReadOnlyList<DayActivity> days, DateOnly today, int goal, DateOnly adoptedDay, MoodRules rules,
        bool studyDone = false)
    {
        IReadOnlyList<DayActivity> withMeeting = days.Any(d => d.Day == adoptedDay && d.Points > 0)
            ? days
            : [.. days, new DayActivity(adoptedDay, new Dictionary<string, int> { [Met] = 1 })];
        var series = ActivityStreak.Of(withMeeting, today, goal);
        var done = days.Where(d => d.Day == today).ToList();
        var points = done.Sum(d => d.Points);
        int Count(string kind) => done.Sum(d => d.Counts.GetValueOrDefault(kind));
        var lines = Count(DayAction.Line) + Count(DayAction.Zone) + Count(DayAction.Screen);
        if (series.Days == 0 && points == 0)
            return new MoodNow(Mood.Asleep, MoodReason.None);
        if (Count(DayAction.Study) >= rules.TiredAnswers)
            return new MoodNow(Mood.Tired, MoodReason.Answers);
        if (Count(DayAction.Lookup) > rules.TiredWords)
            return new MoodNow(Mood.Tired, MoodReason.Words);
        if (lines > rules.TiredLines)
            return new MoodNow(Mood.Tired, MoodReason.Lines);
        if (points == 0 && series.Frozen.Contains(today.AddDays(-1)))
            return new MoodNow(Mood.Sad, MoodReason.None);
        if (points >= goal)
            return new MoodNow(Mood.Happy, MoodReason.FullDay);
        return studyDone ? new MoodNow(Mood.Happy, MoodReason.StudyDone) : new MoodNow(Mood.Calm, MoodReason.None);
    }
}
