namespace Glossa.Core.Text;

/// <summary>How far one spelling is from another, for suggesting what a misread word really was.</summary>
public static class Spelling
{
    /// <summary>
    /// Levenshtein distance (insert, delete, replace one letter), or <paramref name="limit"/> + 1 as soon as it is
    /// sure to go over: suggestions only care about near spellings, and most keys are far.
    /// </summary>
    public static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }
            if (rowBest > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return Math.Min(previous[b.Length], limit + 1);
    }
}
