namespace Glossa.Core.Study;

/// <summary>
/// Evening reminders (decided 2026-09-29: about 18:00 and 20:00 by the computer's clock): only on a day with nothing
/// studied yet and something to study, each time once a day, and only near its time - Glossa started at 23:00 does not
/// remind about 18:00.
/// </summary>
public static class StudyReminders
{
    /// <summary>How long after its time a reminder may still come (the computer asleep, Glossa started late).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);

    /// <summary>
    /// The reminder time due now, or null: the latest of <paramref name="times"/> whose window holds
    /// <paramref name="nowLocal"/> and that has not been shown today (<paramref name="lastShownLocal"/>).
    /// </summary>
    public static TimeOnly? Due(DateTime nowLocal, IReadOnlyList<TimeOnly> times, DateTime? lastShownLocal, bool studiedToday, int cardsToStudy)
    {
        if (studiedToday || cardsToStudy <= 0) return null;
        TimeOnly? due = null;
        foreach (var time in times)
        {
            var at = nowLocal.Date + time.ToTimeSpan();
            if (nowLocal < at || nowLocal >= at + Window) continue;
            if (lastShownLocal is { } shown && shown >= at) continue; // this one (or a later one) already came today
            due = time;
        }
        return due;
    }

    /// <summary>The reminder's words: how much waits, the way «Главная» counts it.</summary>
    public static string Text(int cardsToStudy) =>
        $"Пора повторить: {cardsToStudy} {Cards(cardsToStudy)} на сегодня. Учёба займёт несколько минут.";

    private static string Cards(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "карточка",
        (>= 2 and <= 4, < 12 or > 14) => "карточки",
        _ => "карточек",
    };
}
