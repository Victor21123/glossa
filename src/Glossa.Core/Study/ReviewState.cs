namespace Glossa.Core.Study;

/// <summary>Anki's card queues, as far as Glossa needs them (a card per word and direction).</summary>
public enum CardQueue { New, Learning, Review, Relearning }

/// <summary>The four answer buttons, 1-4 on the keyboard.</summary>
public enum Rating { Again = 1, Hard = 2, Good = 3, Easy = 4 }

/// <summary>What a card asks: the word for its translation (слово -> перевод) or the translation for the word.</summary>
public enum CardDirection { Forward, Reverse }

/// <summary>A card: a word asked one way. With both directions on, a word has two cards, each on its own schedule.</summary>
public readonly record struct CardKey(string WordId, CardDirection Direction);

/// <summary>
/// Where a card stands in study: Anki's scheduling fields. A card that was never answered has no stored state and is
/// <see cref="CardQueue.New"/>.
/// </summary>
public sealed record ReviewState
{
    public required string WordId { get; init; }

    /// <summary>Which of the word's cards this is; everything before directions existed is forward.</summary>
    public CardDirection Direction { get; init; }

    public CardKey Key => new(WordId, Direction);

    public CardQueue Queue { get; init; } = CardQueue.New;

    /// <summary>Learning or relearning steps still ahead, the current one included (Anki's "left").</summary>
    public int RemainingSteps { get; init; }

    /// <summary>A learning step due within the study day: the moment it is due.</summary>
    public DateTime? DueAt { get; init; }

    /// <summary>A review, or a learning step that crossed the 4:00 boundary: the study day it is due.</summary>
    public DateOnly? DueDay { get; init; }

    /// <summary>The review interval in days; while relearning, the interval the word returns to reviews with.</summary>
    public int IntervalDays { get; init; }

    /// <summary>Anki's ease factor (2.5 = 250%); 0 until the word graduates from learning.</summary>
    public float Ease { get; init; }

    public int Reps { get; init; }
    public int Lapses { get; init; }

    /// <summary>Forgotten too often (Anki's leech): only marked, as Anki's code does.</summary>
    public bool Leech { get; init; }

    public DateTime? AnsweredUtc { get; init; }

    public static ReviewState New(string wordId, CardDirection direction = CardDirection.Forward) =>
        new() { WordId = wordId, Direction = direction };
}

/// <summary>One answer, as Anki's review log keeps it.</summary>
public sealed record ReviewAnswer
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string WordId { get; init; }
    public CardDirection Direction { get; init; }
    public CardKey Key => new(WordId, Direction);
    public required DateTime AnsweredUtc { get; init; }
    public required Rating Rating { get; init; }
    public required CardQueue QueueBefore { get; init; }

    /// <summary>A review answered before it was due (a pinned word).</summary>
    public bool Early { get; init; }

    /// <summary>Days, or minus seconds for a learning step (Anki's revlog ivl and lastIvl).</summary>
    public int IntervalBefore { get; init; }

    public int IntervalAfter { get; init; }
    public float Ease { get; init; }

    /// <summary>How long the word was on screen before the answer.</summary>
    public int TakenMs { get; init; }

    /// <summary>The log line for answering <paramref name="before"/> with <paramref name="rating"/> from <paramref name="choices"/>.</summary>
    public static ReviewAnswer Of(ReviewState before, Choices choices, Rating rating, int takenMs)
    {
        var after = choices[rating];
        return new ReviewAnswer
        {
            WordId = before.WordId,
            Direction = before.Direction,
            AnsweredUtc = after.State.AnsweredUtc ?? DateTime.UtcNow,
            Rating = rating,
            QueueBefore = before.Queue,
            Early = choices.Early,
            IntervalBefore = before.Queue switch
            {
                CardQueue.New => 0,
                CardQueue.Review => before.IntervalDays,
                _ => before is { DueAt: { } due, AnsweredUtc: { } at } ? -(int)(due - at).TotalSeconds : 0,
            },
            IntervalAfter = after.State.Queue == CardQueue.Review ? after.State.IntervalDays : -(int)after.Interval.TotalSeconds,
            Ease = after.State.Ease,
            TakenMs = takenMs,
        };
    }
}

/// <summary>Anki's deck options with Anki's defaults (rslib deckconfig/mod.rs); steps are in minutes.</summary>
public sealed record StudyConfig
{
    public IReadOnlyList<float> LearnSteps { get; init; } = [1, 10];
    public IReadOnlyList<float> RelearnSteps { get; init; } = [10];
    public int GraduatingInterval { get; init; } = 1;
    public int EasyInterval { get; init; } = 4;
    public float StartingEase { get; init; } = 2.5f;
    public float EasyBonus { get; init; } = 1.3f;
    public float HardMultiplier { get; init; } = 1.2f;
    public float IntervalModifier { get; init; } = 1f;

    /// <summary>"Новый интервал" after a lapse, as a share of the old one.</summary>
    public float LapseMultiplier { get; init; }

    public int MinimumLapseInterval { get; init; } = 1;
    public int MaximumInterval { get; init; } = 36500;
    public int LeechThreshold { get; init; } = 8;

    /// <summary>Learning cards due this soon may be shown when nothing else is left (and get "&lt;" on buttons).</summary>
    public TimeSpan LearnAhead { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>Study days: a day starts at 4:00 local time, as in Anki (an answer at 2 a.m. belongs to the evening before).</summary>
public sealed class StudyClock(TimeZoneInfo zone, int rolloverHour = 4)
{
    public static StudyClock Local { get; } = new(TimeZoneInfo.Local);

    public DateOnly Day(DateTime utc)
    {
        utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone).AddHours(-rolloverHour));
    }

    /// <summary>When the study day after the one of <paramref name="utc"/> starts.</summary>
    public DateTime Rollover(DateTime utc) => Start(Day(utc).AddDays(1));

    /// <summary>The moment a study day starts, in UTC.</summary>
    public DateTime Start(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(rolloverHour, 0));
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // a clock change skipped 4:00
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
