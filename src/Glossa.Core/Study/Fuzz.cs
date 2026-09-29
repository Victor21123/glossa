namespace Glossa.Core.Study;

/// <summary>Anki's review fuzz (scheduler/states/fuzz.rs): day intervals spread so words learned together part ways.</summary>
public static class Fuzz
{
    /// <summary>Half-width of the range: none under 2.5 days, then a day plus 15%, 10% and 5% of the parts above 2.5, 7 and 20.</summary>
    public static float Delta(float interval) => interval < 2.5f ? 0f
        : 1f + 0.15f * MathF.Max(MathF.Min(interval, 7f) - 2.5f, 0f)
             + 0.1f * MathF.Max(MathF.Min(interval, 20f) - 7f, 0f)
             + 0.05f * MathF.Max(interval - 20f, 0f);

    /// <summary>The days an interval may land on, within [minimum, maximum]; a one-day range above 2 days gets a second day.</summary>
    public static (int Lo, int Hi) Bounds(float interval, int minimum, int maximum)
    {
        minimum = Math.Min(minimum, maximum);
        interval = Math.Clamp(interval, minimum, maximum);
        var delta = Delta(interval);
        var lo = Math.Clamp(Round(interval - delta), minimum, maximum);
        var hi = Math.Clamp(Round(interval + delta), minimum, maximum);
        if (hi == lo && hi > 2 && hi < maximum) hi = lo + 1;
        return (lo, hi);
    }

    /// <summary>The day picked by <paramref name="factor"/> in [0, 1), the same factor for all four buttons.</summary>
    public static int Apply(float interval, int minimum, int maximum, float factor)
    {
        var (lo, hi) = Bounds(interval, minimum, maximum);
        return (int)MathF.Floor(lo + factor * (1 + hi - lo));
    }

    /// <summary>Rust's <c>f32::round() as u32</c>: half away from zero, negatives to 0.</summary>
    public static int Round(float x) => (int)MathF.Max(MathF.Round(x, MidpointRounding.AwayFromZero), 0f);
}

/// <summary>
/// Anki's load balancer (states/load_balancer.rs): within the fuzz range, a day with fewer reviews due is likelier.
/// Weight 1 for an empty day, else (1/n)^2.15 x (1/day)^3; "siblings" and "easy days" are neutral (one card per word,
/// all weekdays at 100%). Intervals over 90 days are only fuzzed.
/// </summary>
public static class LoadBalancer
{
    private const int MaxInterval = 90;

    /// <param name="dueOn">How many reviews are already due that many days from today.</param>
    public static int? Pick(float interval, int minimum, int maximum, Func<int, int> dueOn, ulong seed)
    {
        // Anki compares `interval as usize`: truncated, so 90.7 is still balanced.
        if ((int)interval > MaxInterval || minimum > MaxInterval) return null;
        var (lo, hi) = Fuzz.Bounds(interval, minimum, maximum);
        var weights = new float[hi - lo + 1];
        for (var day = lo; day <= hi; day++)
        {
            var n = dueOn(day);
            weights[day - lo] = n == 0 ? 1f : MathF.Pow(1f / n, 2.15f) * MathF.Pow(1f / day, 3);
        }
        var total = weights.Sum();
        if (!(total > 0)) return null;
        var point = StudyRandom.Float(seed) * total;
        for (var i = 0; i < weights.Length; i++)
        {
            point -= weights[i];
            if (point < 0) return lo + i;
        }
        return hi;
    }
}

/// <summary>Anki seeds the fuzz with the card's id plus its answer count, so the buttons and the answer agree.</summary>
public static class StudySeed
{
    public static ulong Of(string wordId, int reps)
    {
        // FNV-1a: string.GetHashCode changes between runs.
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var ch in wordId)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            return hash + (ulong)reps;
        }
    }
}

/// <summary>The first draw of SplitMix64 for a seed, in [0, 1). Rust's ChaCha12 is not repeated bit for bit anyway.</summary>
internal static class StudyRandom
{
    public static float Float(ulong seed)
    {
        unchecked
        {
            var z = seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 40) / (float)(1UL << 24);
        }
    }
}
