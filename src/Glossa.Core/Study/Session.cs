using Glossa.Core.Library;

namespace Glossa.Core.Study;

/// <summary>Where a card in the session came from: the three columns on the study screen.</summary>
public enum StudyBucket { Pinned, Due, New }

public sealed record SessionCard(SavedWord Word, ReviewState State, StudyBucket Bucket)
{
    public CardDirection Direction => State.Direction;
    public CardKey Key => State.Key;
}

/// <summary>Which new words come first: looked up most often (then most recently met), most recently met, or at random.</summary>
public enum NewWordOrder { Lookups, Recent, Random }

/// <summary>The cards a word gets: слово -> перевод, перевод -> слово, or both, each on its own schedule.</summary>
public enum StudyDirection { Forward, Reverse, Both }

public static class StudyDirections
{
    private static readonly CardDirection[] Forward = [CardDirection.Forward], Reverse = [CardDirection.Reverse],
        Both = [CardDirection.Forward, CardDirection.Reverse];

    /// <summary>The cards each word gets, forward first: it wins a tie when siblings are buried.</summary>
    public static IReadOnlyList<CardDirection> Cards(this StudyDirection direction) => direction switch
    {
        StudyDirection.Reverse => Reverse,
        StudyDirection.Both => Both,
        _ => Forward,
    };
}

/// <summary>
/// Glossa's session around Anki's scheduler: at most <see cref="Size"/> cards, pinned words up to <see cref="PinnedShare"/>
/// of them. Limits count cards, as Anki's do: with both directions 20 new cards a day are about 10 words.
/// <see cref="BurySiblings"/>: one card of a word per session (Anki's "bury siblings", but per session).
/// <see cref="ExtraNew"/>: new cards allowed on top of the day's limit (Anki's "increase today's new card limit").
/// </summary>
public sealed record StudyLimits(int Size = 20, int NewPerDay = 20, int ReviewsPerDay = 200, double PinnedShare = 0.5,
    NewWordOrder NewOrder = NewWordOrder.Lookups, StudyDirection Direction = StudyDirection.Forward, bool BurySiblings = true,
    int ExtraNew = 0)
{
    /// <summary>
    /// "Ещё N слов": a portion of <see cref="Size"/> cards, what is due first, then new words past the day's limit. Nothing is
    /// stored, so it works after a restart and any number of times a day. Pinned words stay with the regular session.
    /// </summary>
    public StudyLimits More() => this with { ExtraNew = Size, PinnedShare = 0 };
}

/// <summary>What the start screen of a regular plan and a "more" portion offers: the button, and what Space starts.</summary>
public enum StudyStart { None, Regular, More }

/// <summary>
/// "Ещё N слов" appears once the day's new limit is used up (<see cref="StudyPlan.NewLeft"/> is 0, not merely no new cards in
/// a session filled by pinned and due ones) and the portion would bring some, so it is never beside new words still free.
/// Not at all with NewPerDay 0: that is "reviews only" by choice. Space starts the regular plan; only when that is empty does
/// it start the portion.
/// </summary>
public sealed record StudyOffer(bool ShowMore, StudyStart Space)
{
    public static StudyOffer Of(StudyPlan regular, StudyPlan more)
    {
        // NewPerDay 0 is a choice (reviews only): no offer to break it.
        var show = regular.NewPerDay > 0 && regular.NewLeft == 0 && more.New.Count > 0;
        return new StudyOffer(show, regular.Cards.Count > 0 ? StudyStart.Regular : show ? StudyStart.More : StudyStart.None);
    }
}

/// <summary>
/// A session to start: the cards in order, each bucket for the start screen, and the directions it drew from.
/// <see cref="NewLeft"/>: new cards the day's limits still allow, before <see cref="StudyLimits.ExtraNew"/> (0: the limit is used up,
/// whatever the session size cut off). <see cref="Unstudied"/>: cards never answered among the words included, pinned ones too.
/// </summary>
public sealed record StudyPlan(
    IReadOnlyList<SessionCard> Cards,
    IReadOnlyList<SessionCard> Pinned,
    IReadOnlyList<SessionCard> Due,
    IReadOnlyList<SessionCard> New,
    int NewToday,
    int NewPerDay,
    StudyDirection Direction,
    int NewLeft = 0,
    int Unstudied = 0);

/// <summary>
/// Picks a session: pinned words (up to <see cref="StudyLimits.PinnedShare"/>, the longest unstudied first), then what is
/// due as Anki orders it (learning steps by time, reviews by due day with ties at random), then new cards (looked up
/// most often, then most recently met) within the daily limit. Pinned words not yet due get Anki's early review of a
/// filtered deck. With <see cref="StudyLimits.BurySiblings"/>, a word gives the session one card: the first in that order.
/// </summary>
public static class SessionBuilder
{
    public static StudyPlan Build(IReadOnlyList<SavedWord> words, IReadOnlyDictionary<CardKey, ReviewState> states,
        IReadOnlyList<ReviewAnswer> today, StudyLimits limits, StudyConfig config, StudyClock clock, DateTime nowUtc,
        Func<SavedWord, bool>? include = null)
    {
        var day = clock.Day(nowUtc);
        // Each word lists its forward card first and LINQ's sort is stable, so the forward card wins every tie below.
        var cards = words.Where(w => include?.Invoke(w) ?? true)
            .SelectMany(w => limits.Direction.Cards().Select(d =>
                new SessionCard(w, states.TryGetValue(new CardKey(w.Id, d), out var s) ? s : ReviewState.New(w.Id, d), StudyBucket.New)))
            .ToList();

        // Burying siblings, per session rather than Anki's per day (decided 2026-09-30): each word gives the session its
        // first card in the order pinned, due, new; the other waits for a later session.
        var seen = limits.BurySiblings ? new HashSet<string>() : null;

        var pinned = OnePerWord(cards.Where(c => c.Word.Pinned)
                .OrderBy(c => c.State.AnsweredUtc ?? DateTime.MinValue).ThenBy(c => c.Word.LastSeenUtc), seen)
            .Take((int)(limits.Size * limits.PinnedShare))
            .Select(c => c with { Bucket = StudyBucket.Pinned })
            .ToList();
        var rest = cards.Where(c => !c.Word.Pinned).ToList();

        // Learning steps are not limited; reviews (and steps moved to another day) count against the review limit.
        // Review ties are broken by the word, not the card, so a word's two cards due together stay forward first.
        var reviewsLeft = Math.Max(limits.ReviewsPerDay - today.Count(a => a.QueueBefore == CardQueue.Review), 0);
        var room = limits.Size - pinned.Count;
        var learning = OnePerWord(rest.Where(c => c.State.DueAt is { } at && at <= nowUtc).OrderBy(c => c.State.DueAt), seen)
            .Take(room)
            .ToList();
        var reviews = OnePerWord(rest.Where(c => c.State is { Queue: not CardQueue.New, DueAt: null, DueDay: { } due } && due <= day)
                .OrderBy(c => c.State.DueDay).ThenBy(c => StudySeed.Of(c.Word.Id, day.DayNumber)), seen)
            .Take(Math.Min(reviewsLeft, room - learning.Count))
            .ToList();
        var dueCards = learning.Concat(reviews).Select(c => c with { Bucket = StudyBucket.Due }).ToList();
        room -= dueCards.Count;

        // The new-card limit counts cards started in earlier sessions today; as in Anki, the review limit caps it too.
        var newToday = today.Count(a => a.QueueBefore == CardQueue.New);
        var allowed = Math.Max(Math.Min(limits.NewPerDay - newToday, reviewsLeft - dueCards.Count(c => c.State.DueAt is null)), 0);
        // Deliberate deviation from Anki v3: the extra new cards ignore the review limit (the user asked for more words explicitly).
        var newLeft = allowed + limits.ExtraNew;
        var candidates = rest.Where(c => c.State.Queue == CardQueue.New);
        var fresh = OnePerWord(limits.NewOrder switch
            {
                NewWordOrder.Recent => candidates.OrderByDescending(c => c.Word.LastSeenUtc),
                NewWordOrder.Random => candidates.OrderBy(c => StudySeed.Of(c.Word.Id, day.DayNumber)), // the same order all day
                _ => candidates.OrderByDescending(c => c.Word.Lookups).ThenByDescending(c => c.Word.LastSeenUtc),
            }, seen)
            .Take(Math.Max(Math.Min(newLeft, room), 0))
            .ToList();

        // Steps due now first, then reviews with new words mixed in, pinned words spread through the whole.
        var main = Interleave.Mix(Interleave.Mix(dueCards.Where(c => c.State.DueAt is null).ToList(), fresh), pinned);
        return new StudyPlan([.. dueCards.Where(c => c.State.DueAt is not null), .. main], pinned, dueCards, fresh, newToday,
            limits.NewPerDay, limits.Direction, allowed, cards.Count(c => c.State.Queue == CardQueue.New));
    }

    // The first card of each word, in order; with burying off (no set) every card. Lazy on purpose: a word counts as
    // taken only when its card is actually pulled into the session, so the queues after it skip only those words.
    private static IEnumerable<SessionCard> OnePerWord(IEnumerable<SessionCard> cards, HashSet<string>? seen)
    {
        foreach (var card in cards)
            if (seen is null || seen.Add(card.Word.Id))
                yield return card;
    }
}

/// <summary>
/// A running session, Anki's way (scheduler/queue): learning steps whose time has come first, then the planned cards,
/// then steps due within the learn-ahead limit. A card leaves the session when its next showing is on another day.
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
    public StudySession(StudyPlan plan, IReadOnlyDictionary<CardKey, ReviewState> states, StudyConfig config, StudyClock clock,
        Action<ReviewState, ReviewAnswer> save, Random? random = null)
    {
        _main = new Queue<SessionCard>(plan.Cards);
        // Cards of a direction switched off are like Anki's suspended cards: they wait, and the balancer does not count them.
        var directions = plan.Direction.Cards();
        _dueByDay = states.Values.Where(s => s is { Queue: not CardQueue.New, DueDay: not null } && directions.Contains(s.Direction))
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
