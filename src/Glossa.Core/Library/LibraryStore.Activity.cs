using System.Globalization;

namespace Glossa.Core.Library;

public sealed partial class LibraryStore
{
    /// <summary>
    /// One action for the days with Glossa (<see cref="DayAction"/>): counted by the day it falls in (from 4:00, as
    /// the rest of the library counts days). Only numbers are kept, no text.
    /// </summary>
    public void AddActivity(string kind, DateTime utc, int count = 1)
    {
        lock (_gate) CountActivity(kind, utc, count);
    }

    /// <summary>The days with any action since <paramref name="since"/> (inclusive), oldest first.</summary>
    public IReadOnlyList<DayActivity> ActivityDays(DateOnly since)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT day, kind, count FROM activity WHERE day >= $since ORDER BY day";
            cmd.Parameters.AddWithValue("$since", DayText(since));
            using var r = cmd.ExecuteReader();
            var days = new List<DayActivity>();
            Dictionary<string, int>? counts = null;
            DateOnly? current = null;
            while (r.Read())
            {
                var day = DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != current)
                {
                    counts = [];
                    days.Add(new DayActivity(day, counts));
                    current = day;
                }
                counts![r.GetString(1)] = r.GetInt32(2);
            }
            return days;
        }
    }

    /// <summary>Under the caller's lock (and transaction, when it has one).</summary>
    private void CountActivity(string kind, DateTime utc, int count = 1) => Run("""
        INSERT INTO activity(day, kind, count) VALUES($day, $kind, $n)
        ON CONFLICT(day, kind) DO UPDATE SET count = count + $n
        """, ("$day", DayText(LibraryStats.Day(utc))), ("$kind", kind), ("$n", count));
}
