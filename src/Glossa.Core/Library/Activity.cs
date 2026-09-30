namespace Glossa.Core.Library;

/// <summary>
/// What makes a day with Glossa (decided 2026-09-30): a word looked up, a study answer or a line translated, in any
/// mode. One counter for all of them; the companion will feed on it.
/// </summary>
public static class DayAction
{
    public const string Lookup = "lookup";
    public const string Study = "study";
    public const string Line = QuoteSource.Line;
    public const string Zone = QuoteSource.Zone;
    public const string Screen = QuoteSource.Screen;
    public const string Live = QuoteSource.Live;

    public static bool IsTranslation(string kind) => kind is Line or Zone or Screen or Live;
}

/// <summary>One day (from 4:00) and how much of each kind was done in it.</summary>
public sealed record DayActivity(DateOnly Day, IReadOnlyDictionary<string, int> Counts)
{
    /// <summary>Every action counts one: a lookup, an answer, a line.</summary>
    public int Points => Counts.Values.Sum();

    public int Translations => Counts.Where(c => DayAction.IsTranslation(c.Key)).Sum(c => c.Value);
    public int Lookups => Counts.GetValueOrDefault(DayAction.Lookup);
    public int Answers => Counts.GetValueOrDefault(DayAction.Study);
}

/// <summary>
/// The series of days with Glossa, one for every mode (decided 2026-09-30). Any action keeps a day; a full day is
/// <c>goal</c> actions (partial days keep the series but show paler). Seven days in a row earn a freeze (at most two),
/// and a freeze covers a missed day instead of breaking the series. Today does not break it before it is over.
/// </summary>
public sealed record ActivityStreak(int Days, int Best, int Freezes, IReadOnlySet<DateOnly> Frozen, int TodayPoints, bool TodayFull)
{
    public const int FreezeEvery = 7;
    public const int MaxFreezes = 2;

    public static ActivityStreak Of(IEnumerable<DayActivity> days, DateOnly today, int goal)
    {
        var points = days.Where(d => d.Points > 0).ToDictionary(d => d.Day, d => d.Points);
        var frozen = new HashSet<DateOnly>();
        int streak = 0, best = 0, freezes = 0, run = 0;
        if (points.Count > 0)
        {
            for (var d = points.Keys.Min(); d <= today; d = d.AddDays(1))
            {
                if (points.ContainsKey(d))
                {
                    streak++;
                    if (++run == FreezeEvery)
                    {
                        freezes = Math.Min(MaxFreezes, freezes + 1);
                        run = 0;
                    }
                }
                else if (d == today)
                {
                    // the day is not over yet
                }
                else if (streak > 0 && freezes > 0)
                {
                    freezes--;
                    frozen.Add(d);
                    run = 0;
                }
                else
                {
                    streak = 0;
                    run = 0;
                }
                best = Math.Max(best, streak);
            }
        }
        var todayPoints = points.GetValueOrDefault(today);
        return new ActivityStreak(streak, best, freezes, frozen, todayPoints, todayPoints >= goal);
    }
}
