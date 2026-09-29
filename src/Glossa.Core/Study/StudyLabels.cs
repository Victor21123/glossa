using Glossa.Core.Text;

namespace Glossa.Core.Study;

/// <summary>Button captions in Anki's units (scheduler/timespan.rs), in Russian.</summary>
public static class StudyLabels
{
    // Anki's own constants (timespan.rs): a month is 30.417 days, not exactly 365/12.
    private const double Minute = 60, Hour = 3600, Day = 86400, Month = 30.417 * Day, Year = 365 * Day;

    /// <summary>
    /// Seconds, minutes and days are whole; hours, months and years get one decimal. "&lt;" when the word may come back
    /// sooner: within the learn-ahead limit (20 minutes by default).
    /// </summary>
    public static string Button(TimeSpan span, TimeSpan? learnAhead = null)
    {
        var secs = span.TotalSeconds;
        var text = secs < Minute ? $"{Whole(secs)} с"
            : secs < Hour ? $"{Whole(secs / Minute)} мин"
            : secs < Day ? $"{Tenth(secs / Hour)} ч"
            : secs < Month ? Days(Whole(secs / Day))
            : secs < Year ? $"{Tenth(secs / Month)} мес"
            : Years(secs / Year);
        return secs < (learnAhead ?? TimeSpan.FromMinutes(20)).TotalSeconds ? "<" + text : text;
    }

    public static string Days(long n) => $"{n} {Russian.Plural(n, "день", "дня", "дней")}";

    private static long Whole(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string Tenth(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", Russian.Culture);

    private static string Years(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded % 1 == 0
            ? $"{rounded:0} {Russian.Plural((long)rounded, "год", "года", "лет")}"
            : $"{rounded.ToString("0.#", Russian.Culture)} года";
    }
}

/// <summary>Anki's mixing of two queues (scheduler/queue/builder/intersperser.rs).</summary>
public static class Interleave
{
    /// <summary>
    /// Spreads <paramref name="b"/> evenly through <paramref name="a"/>: ratio = (|a|+1)/(|b|+1), an item of b goes out
    /// when (ib+1) x ratio &lt; ia+1. [1, 2, 3] and [11, 22, 33] give 1, 11, 2, 22, 3, 33.
    /// </summary>
    public static List<T> Mix<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var mixed = new List<T>(a.Count + b.Count);
        var ratio = (a.Count + 1) / (double)(b.Count + 1);
        int ia = 0, ib = 0;
        while (ia < a.Count || ib < b.Count)
        {
            if (ib < b.Count && (ia >= a.Count || (ib + 1) * ratio < ia + 1)) mixed.Add(b[ib++]);
            else mixed.Add(a[ia++]);
        }
        return mixed;
    }
}
