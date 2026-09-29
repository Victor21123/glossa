namespace Glossa.Core.Library;

/// <summary>
/// The home page's numbers: how big the dictionary is, how it grew, on which days words were looked up and how many
/// days in a row, and which languages and games they came from. A day starts at 4:00, as in Anki: a lookup at 2 a.m.
/// belongs to the evening before.
/// </summary>
public sealed record LibraryStats(
    int Words,
    int AddedThisWeek,
    int Lookups,
    int Pinned,
    int Streak,
    int BestStreak,
    IReadOnlyDictionary<DateOnly, int> PerDay,
    IReadOnlyList<(string Language, int Count)> Languages,
    IReadOnlyList<(string Game, int Count)> Games)
{
    public static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(utc.ToLocalTime().AddHours(-4));

    /// <param name="game">The game a sentence came from, by its program and window title (profile names win).</param>
    public static LibraryStats Of(IReadOnlyList<SavedWord> words, DateTime nowUtc, Func<string?, string?, string?> game)
    {
        var today = Day(nowUtc);
        var perDay = new Dictionary<DateOnly, int>();
        var games = new Dictionary<string, int>();
        foreach (var w in words)
        {
            // Each sentence a word was met in is one lookup day; a word saved by hand without one counts on its own day.
            var met = w.Contexts.Count > 0 ? w.Contexts.Select(c => (c.CreatedUtc, c.AppExe, c.WindowTitle)).ToList()
                : [(w.CreatedUtc, w.AppExe, w.WindowTitle)];
            foreach (var (at, _, _) in met)
                perDay[Day(at)] = perDay.GetValueOrDefault(Day(at)) + 1;
            foreach (var name in met.Select(m => game(m.AppExe, m.WindowTitle)).OfType<string>().Distinct())
                games[name] = games.GetValueOrDefault(name) + 1;
        }

        // The current series may end yesterday: today is not over yet.
        var streak = 0;
        for (var d = perDay.ContainsKey(today) ? today : today.AddDays(-1); perDay.ContainsKey(d); d = d.AddDays(-1)) streak++;
        var best = 0;
        foreach (var d in perDay.Keys.Order())
        {
            if (perDay.ContainsKey(d.AddDays(-1))) continue; // count each series once, from its first day
            var run = 0;
            for (var x = d; perDay.ContainsKey(x); x = x.AddDays(1)) run++;
            best = Math.Max(best, run);
        }

        return new LibraryStats(
            words.Count,
            words.Count(w => Day(w.CreatedUtc) > today.AddDays(-7)),
            words.Sum(w => w.Lookups),
            words.Count(w => w.Pinned),
            streak,
            Math.Max(best, streak),
            perDay,
            words.GroupBy(w => w.Language).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList(),
            games.OrderByDescending(g => g.Value).Take(5).Select(g => (g.Key, g.Value)).ToList());
    }
}
