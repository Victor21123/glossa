using Glossa.Core.Library;

namespace Glossa.Core.Study;

/// <summary>
/// The study screen's footer: days in a row with answers (today may still come), answers this week, how many reviews
/// were remembered (not "Снова"), and seconds per card for the session estimate.
/// </summary>
public sealed record StudyStats(int Streak, int BestStreak, int WeekAnswers, int? WeekRetention, int SecondsPerCard)
{
    private const int DefaultSecondsPerCard = 18;

    public static StudyStats Empty { get; } = new(0, 0, 0, null, DefaultSecondsPerCard);

    public static StudyStats Of(IReadOnlyList<ReviewAnswer> answers, StudyClock clock, DateTime nowUtc)
    {
        if (answers.Count == 0) return Empty;
        var today = clock.Day(nowUtc);
        var days = answers.Select(a => clock.Day(a.AnsweredUtc)).ToHashSet();

        var streak = 0;
        for (var d = days.Contains(today) ? today : today.AddDays(-1); days.Contains(d); d = d.AddDays(-1)) streak++;
        var best = 0;
        foreach (var d in days)
        {
            if (days.Contains(d.AddDays(-1))) continue; // count each series once, from its first day
            var run = 0;
            for (var x = d; days.Contains(x); x = x.AddDays(1)) run++;
            best = Math.Max(best, run);
        }

        var week = answers.Where(a => clock.Day(a.AnsweredUtc) > today.AddDays(-7)).ToList();
        var reviews = week.Where(a => a.QueueBefore == CardQueue.Review).ToList();
        int? retention = reviews.Count == 0 ? null
            : (int)Math.Round(100.0 * reviews.Count(a => a.Rating != Rating.Again) / reviews.Count, MidpointRounding.AwayFromZero);
        var timed = answers.TakeLast(200).Where(a => a.TakenMs > 0).ToList();
        var perCard = timed.Count == 0 ? DefaultSecondsPerCard
            : (int)Math.Round(timed.Average(a => a.TakenMs) / 1000.0, MidpointRounding.AwayFromZero);
        return new StudyStats(streak, Math.Max(best, streak), week.Count, retention, perCard);
    }
}

/// <summary>«Не могу запомнить»: after right answers on three different days, Glossa suggests unpinning the word.</summary>
public static class StudyPins
{
    public const int DaysToUnpin = 3;

    /// <summary>Study days with a right answer (anything but "Снова") since the word was pinned.</summary>
    public static int RightDays(SavedWord word, IEnumerable<ReviewAnswer> answers, StudyClock clock) => answers
        .Where(a => a.WordId == word.Id && a.Rating != Rating.Again && a.AnsweredUtc >= (word.PinnedUtc ?? DateTime.MinValue))
        .Select(a => clock.Day(a.AnsweredUtc))
        .Distinct()
        .Count();

    public static bool SuggestUnpin(SavedWord word, IEnumerable<ReviewAnswer> answers, StudyClock clock) =>
        word.Pinned && RightDays(word, answers, clock) >= DaysToUnpin;
}
