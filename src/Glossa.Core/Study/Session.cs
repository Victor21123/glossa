using Glossa.Core.Library;

namespace Glossa.Core.Study;

/// <summary>Where a word in the session came from: the three columns on the study screen.</summary>
public enum StudyBucket { Pinned, Due, New }

public sealed record SessionCard(SavedWord Word, ReviewState State, StudyBucket Bucket);

/// <summary>Which new words come first: looked up most often (then most recently met), most recently met, or at random.</summary>
public enum NewWordOrder { Lookups, Recent, Random }

/// <summary>Glossa's session around Anki's scheduler: at most <see cref="Size"/> cards, pinned words up to <see cref="PinnedShare"/>.</summary>
public sealed record StudyLimits(int Size = 20, int NewPerDay = 20, int ReviewsPerDay = 200, double PinnedShare = 0.5,
    NewWordOrder NewOrder = NewWordOrder.Lookups);

/// <summary>A session to start: the cards in order and each bucket for the start screen.</summary>
public sealed record StudyPlan(
    IReadOnlyList<SessionCard> Cards,
    IReadOnlyList<SessionCard> Pinned,
    IReadOnlyList<SessionCard> Due,
    IReadOnlyList<SessionCard> New,
    int NewToday,
    int NewPerDay);

/// <summary>
/// Picks a session: pinned words (up to half, the longest unstudied first), then what is due as Anki orders it
/// (learning steps by time, reviews by due day with ties at random), then new words (looked up most often, then most
/// recently met) within the daily limit. Pinned words not yet due get Anki's early review of a filtered deck.
/// </summary>
public static class SessionBuilder
{
    public static StudyPlan Build(IReadOnlyList<SavedWord> words, IReadOnlyDictionary<string, ReviewState> states,
        IReadOnlyList<ReviewAnswer> today, StudyLimits limits, StudyConfig config, StudyClock clock, DateTime nowUtc,
        Func<SavedWord, bool>? include = null)
    {
        var day = clock.Day(nowUtc);
        var cards = words.Where(w => include?.Invoke(w) ?? true)
            .Select(w => new SessionCard(w, states.TryGetValue(w.Id, out var s) ? s : ReviewState.New(w.Id), StudyBucket.New))
            .ToList();

        var pinned = cards.Where(c => c.Word.Pinned)
            .OrderBy(c => c.State.AnsweredUtc ?? DateTime.MinValue).ThenBy(c => c.Word.LastSeenUtc)
            .Take((int)(limits.Size * limits.PinnedShare))
            .Select(c => c with { Bucket = StudyBucket.Pinned })
            .ToList();
        var rest = cards.Where(c => !c.Word.Pinned).ToList();

        // Learning steps are not limited; reviews (and steps moved to another day) count against the review limit.
        var reviewsLeft = Math.Max(limits.ReviewsPerDay - today.Count(a => a.QueueBefore == CardQueue.Review), 0);
        var learning = rest.Where(c => c.State.DueAt is { } at && at <= nowUtc).OrderBy(c => c.State.DueAt);
        var reviews = rest.Where(c => c.State is { Queue: not CardQueue.New, DueAt: null, DueDay: { } due } && due <= day)
            .OrderBy(c => c.State.DueDay).ThenBy(c => StudySeed.Of(c.Word.Id, day.DayNumber))
            .Take(reviewsLeft);
        var room = limits.Size - pinned.Count;
        var dueCards = learning.Concat(reviews).Take(room).Select(c => c with { Bucket = StudyBucket.Due }).ToList();
        room -= dueCards.Count;

        // The new-word limit counts words started in earlier sessions today; as in Anki, the review limit caps it too.
        var newToday = today.Count(a => a.QueueBefore == CardQueue.New);
        var newLeft = Math.Min(limits.NewPerDay - newToday, reviewsLeft - dueCards.Count(c => c.State.DueAt is null));
        var candidates = rest.Where(c => c.State.Queue == CardQueue.New);
        var fresh = (limits.NewOrder switch
            {
                NewWordOrder.Recent => candidates.OrderByDescending(c => c.Word.LastSeenUtc),
                NewWordOrder.Random => candidates.OrderBy(c => StudySeed.Of(c.Word.Id, day.DayNumber)), // the same order all day
                _ => candidates.OrderByDescending(c => c.Word.Lookups).ThenByDescending(c => c.Word.LastSeenUtc),
            })
            .Take(Math.Max(Math.Min(newLeft, room), 0))
            .ToList();

        // Steps due now first, then reviews with new words mixed in, pinned words spread through the whole.
        var main = Interleave.Mix(Interleave.Mix(dueCards.Where(c => c.State.DueAt is null).ToList(), fresh), pinned);
        return new StudyPlan([.. dueCards.Where(c => c.State.DueAt is not null), .. main], pinned, dueCards, fresh, newToday, limits.NewPerDay);
    }
}

/// <summary>
/// A running session, Anki's way (scheduler/queue): learning steps whose time has come first, then the planned cards,
/// then steps due within the learn-ahead limit. A word leaves the session when its next showing is on another day.
/// </summary>
public sealed class StudySession
{
    private readonly Queue<SessionCard> _main;
    private readonly List<(SessionCard Card, DateTime Due)> _learning = [];
    private readonly Dictionary<DateOnly, int> _dueByDay;
    private readonly StudyConfig _config;
    private readonly StudyClock _clock;
    private readonly Action<ReviewState, ReviewAnswer> _save;
    private readonly Random _random;

    /// <param name="states">Every stored state, for load balancing.</param>
    /// <param name="save">Stores an answer; if it throws, the session does not move on.</param>
    public StudySession(StudyPlan plan, IReadOnlyDictionary<string, ReviewState> states, StudyConfig config, StudyClock clock,
        Action<ReviewState, ReviewAnswer> save, Random? random = null)
    {
        _main = new Queue<SessionCard>(plan.Cards);
        _dueByDay = states.Values.Where(s => s is { Queue: not CardQueue.New, DueDay: not null })
            .GroupBy(s => s.DueDay!.Value).ToDictionary(g => g.Key, g => g.Count());
        _config = config;
        _clock = clock;
        _save = save;
        _random = random ?? Random.Shared;
        Total = plan.Cards.Count;
    }

    public int Total { get; }

    /// <summary>Cards that left the session: answered, and not coming back today.</summary>
    public int Done { get; private set; }

    public SessionCard? Current { get; private set; }

    /// <summary>Learning steps still ahead today, and when the first of them is due.</summary>
    public (int Count, DateTime? Next) Waiting => (_learning.Count, _learning.Count == 0 ? null : _learning.Min(l => l.Due));

    public SessionCard? Next(DateTime nowUtc)
    {
        // A step whose time has come, else the planned cards, else a step within the learn-ahead limit.
        var cutoff = _main.Count == 0 ? nowUtc + _config.LearnAhead : nowUtc;
        if (_learning.Count > 0 && _learning.MinBy(l => l.Due) is var step && step.Due <= cutoff)
        {
            _learning.Remove(step);
            return Current = step.Card;
        }
        return Current = _main.TryDequeue(out var next) ? next : null;
    }

    /// <summary>What each button would do for the current card now (their captions).</summary>
    public Choices Choices(DateTime nowUtc) =>
        AnkiScheduler.Next(Current?.State ?? throw new InvalidOperationException("No card is shown."), Context(nowUtc));

    public void Answer(Rating rating, DateTime nowUtc, int takenMs)
    {
        if (Current is not { } card) return;
        var choices = Choices(nowUtc);
        var next = choices[rating].State;
        _save(next, ReviewAnswer.Of(card.State, choices, rating, takenMs));

        if (card.State is { Queue: not CardQueue.New, DueDay: { } before } && _dueByDay.TryGetValue(before, out var n))
            _dueByDay[before] = n - 1;
        if (next.DueDay is { } after) _dueByDay[after] = _dueByDay.GetValueOrDefault(after) + 1;
        if (next.DueAt is { } due) _learning.Add((card with { State = next }, Requeue(due, nowUtc)));
        else Done++;
        Current = null;
    }

    // queue/learning.rs requeue_learning_entry: with nothing else left, the word just answered goes after the next
    // step due (its time + 1 s) so the same word is not shown twice in a row; the stored due stays as it is.
    private DateTime Requeue(DateTime due, DateTime nowUtc)
    {
        var cutoff = nowUtc + _config.LearnAhead;
        if (_main.Count > 0 || due > cutoff || _learning.Count == 0) return due;
        var first = _learning.MinBy(l => l.Due).Due;
        return first >= due && first < cutoff ? first.AddSeconds(1) : due;
    }

    private SchedulerContext Context(DateTime nowUtc) =>
        new(_config, _clock, nowUtc, Fuzz: true, DueOn: day => _dueByDay.GetValueOrDefault(day), LearnJitter: _random.NextSingle());
}
