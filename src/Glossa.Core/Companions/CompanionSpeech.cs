using Glossa.Core.Study;
using Glossa.Core.Text;

namespace Glossa.Core.Companions;

/// <summary>The moments a companion speaks, as the keys of <c>"phrases"</c> in its <c>&lt;id&gt;.json</c>.</summary>
public static class SpeechEvents
{
    /// <summary>The home page opened and nothing presses.</summary>
    public const string Greet = "greet";

    /// <summary>Asleep before, awake now.</summary>
    public const string Wake = "wake";

    /// <summary>Cards are due today ({cards}).</summary>
    public const string StudyDue = "study_due";

    /// <summary>Evening, nothing done today, the series ({days}) about to break.</summary>
    public const string StreakRisk = "streak_risk";

    /// <summary>The companion's last words, caught forged, the minute it leaves for its week away.</summary>
    public const string Leave = "leave";

    /// <summary>A click on one of the companion's zones (<see cref="CompanionZone"/>).</summary>
    public static string Poke(string zone) => "poke." + zone;

    /// <summary>Clicked again and again (<see cref="CompanionPokes"/>): the companion has had enough.</summary>
    public const string Enough = "poke.enough";

    /// <summary>A click on the companion, in its mood.</summary>
    public static string Click(Mood mood) => "click." + Key(mood);

    /// <summary>The mood just became this.</summary>
    public static string Changed(Mood mood) => "mood." + Key(mood);

    private static string Key(Mood mood) => mood.ToString().ToLowerInvariant();
}

/// <summary>
/// What a companion says in its speech bubble (user, 2026-10-03): scripted lines of the character (no AI, decided
/// 2026-09-29), chosen by the moment, never the same twice in a row, with the numbers put in: {cards} - "12 карточек",
/// {days} - "5 дней".
/// </summary>
public static class CompanionSpeech
{
    /// <summary>The evening from which a day with nothing done warns of the series (the reminders' hours are 18 and 20).</summary>
    public const int EveningHour = 18;

    /// <summary>
    /// A line for the moment, or null when the companion has none for it. A line about the cards or the series is
    /// left out while its number is unknown or zero ("0 карточек" is never said).
    /// </summary>
    public static string? Say(Companion companion, string moment, Random rng, string? last, int cards = 0, int days = 0)
    {
        if (companion.Phrases is null || !companion.Phrases.TryGetValue(moment, out var lines))
            return null;
        var said = lines.Where(l => !string.IsNullOrWhiteSpace(l)
                                    && (cards > 0 || !l.Contains("{cards}", StringComparison.Ordinal))
                                    && (days > 0 || !l.Contains("{days}", StringComparison.Ordinal)))
            .Select(l => Fill(l, cards, days)).ToList();
        if (said.Count == 0)
            return null;
        var fresh = said.Where(l => l != last).ToList();
        var pool = fresh.Count > 0 ? fresh : said;
        return pool[rng.Next(pool.Count)];
    }

    private static string Fill(string line, int cards, int days) => line
        .Replace("{cards}", $"{cards} {Russian.Plural(cards, "карточка", "карточки", "карточек")}")
        .Replace("{days}", StudyLabels.Days(days));

    /// <summary>
    /// What to say as the home page opens: the series about to break in the evening, else cards due (not in "Только
    /// перевод", which has no study), else a greeting.
    /// </summary>
    public static string OnOpen(DateTime localNow, int todayPoints, int streakDays, int cardsDue, bool translate)
    {
        if (localNow.Hour >= EveningHour && todayPoints == 0 && streakDays > 0)
            return SpeechEvents.StreakRisk;
        return !translate && cardsDue > 0 ? SpeechEvents.StudyDue : SpeechEvents.Greet;
    }

    /// <summary>What to say when the mood changes: waking up, or the new mood (calm needs no words); null - nothing.</summary>
    public static string? OnMoodChange(Mood? before, Mood now)
    {
        if (before is null || before == now)
            return null;
        if (before == Mood.Asleep)
            return SpeechEvents.Wake;
        return now == Mood.Calm ? null : SpeechEvents.Changed(now);
    }
}
