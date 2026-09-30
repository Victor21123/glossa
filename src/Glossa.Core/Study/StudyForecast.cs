namespace Glossa.Core.Study;

/// <summary>
/// How many cards the schedule brings each day of the coming days (Настройки → Учёба, «Прогноз на неделю»): cards in
/// review by their due day, cards in (re)learning by the day of their due time, overdue ones today. New cards are not
/// scheduled yet, so they are not here; today's column is today's session, which the caller has (pinned, due, new).
/// </summary>
public static class StudyForecast
{
    public static int[] Due(IEnumerable<ReviewState> states, StudyDirection direction, StudyClock clock, DateOnly today, int days = 7)
    {
        var counts = new int[days];
        var studied = direction.Cards();
        foreach (var s in states)
        {
            if (!studied.Contains(s.Direction)) continue; // a direction switched off keeps its states, unused
            DateOnly? day = s.Queue switch
            {
                CardQueue.Review => s.DueDay,
                CardQueue.Learning or CardQueue.Relearning => s.DueAt is { } at ? clock.Day(at) : s.DueDay,
                _ => null,
            };
            if (day is not { } due) continue;
            var offset = Math.Max(0, due.DayNumber - today.DayNumber);
            if (offset < days) counts[offset]++;
        }
        return counts;
    }
}
