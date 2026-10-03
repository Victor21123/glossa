using Glossa.Core.Study;

namespace Glossa.Core.Companions;

/// <summary>
/// What the home page says about the companion, in Russian, agreeing with its grammatical gender ("f" or "m"). No
/// phrases of the character yet: only who it is, how it feels and why, and the days together.
/// </summary>
public static class CompanionLabels
{
    public static string Rarity(Rarity rarity, string gender)
    {
        var she = gender == "f";
        return rarity switch
        {
            Companions.Rarity.Legendary => she ? "Легендарная" : "Легендарный",
            Companions.Rarity.Epic => she ? "Эпическая" : "Эпический",
            Companions.Rarity.Rare => she ? "Редкая" : "Редкий",
            _ => she ? "Обычная" : "Обычный",
        };
    }

    /// <summary>The mood and its reason, so the user knows what changes it.</summary>
    public static string Mood(MoodNow now, string gender)
    {
        var she = gender == "f";
        var happy = she ? "Рада" : "Рад";
        var tired = she ? "Устала" : "Устал";
        return (now.Mood, now.Reason) switch
        {
            (Companions.Mood.Happy, MoodReason.FullDay) => $"{happy}: сегодня полный день",
            (Companions.Mood.Happy, MoodReason.StudyDone) => $"{happy}: учёба на сегодня пройдена",
            (Companions.Mood.Happy, _) => happy,
            (Companions.Mood.Tired, MoodReason.Words) => $"{tired}: сегодня много слов",
            (Companions.Mood.Tired, MoodReason.Lines) => $"{tired}: сегодня много перевода",
            (Companions.Mood.Tired, MoodReason.Answers) => $"{tired}: много учёбы сегодня",
            (Companions.Mood.Tired, _) => tired,
            (Companions.Mood.Sad, _) => "Грустит: вчера был пропуск, серию спасла заморозка",
            (Companions.Mood.Asleep, _) => "Спит: серия прервалась. Поиск, учёба или перевод разбудят",
            _ => she ? "Спокойна" : "Спокоен",
        };
    }

    /// <summary>"Вместе 1 день": the day they met is the first.</summary>
    public static string Together(int days) => $"Вместе {StudyLabels.Days(Math.Max(1, days))}";

    /// <summary>The companion away, caught forged (the user: "Спутник в вас разочаровался и покинул вас").</summary>
    public static string Left(string gender) => gender == "f"
        ? "Разочаровалась в тебе и ушла: ты пытался сжульничать"
        : "Разочаровался в тебе и ушёл: ты пытался сжульничать";

    /// <summary>The way back, a week on (the user: "Это ещё не конец, а только начало! Мы ещё обязательно встретимся").</summary>
    public static string ComesBack(int daysLeft) =>
        $"Это ещё не конец, а только начало. Мы ещё обязательно встретимся - через {Study.StudyLabels.Days(daysLeft)}.";
}
