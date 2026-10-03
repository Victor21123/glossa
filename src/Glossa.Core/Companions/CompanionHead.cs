namespace Glossa.Core.Companions;

/// <summary>
/// Where a companion's head is on its frame, for the speech bubble's tail (user's sketch 2026-10-03: the bubble over
/// the head, the tail down to it). The top of the figure is not the head: a raised flask (Уэно), a scythe (Ренне), a
/// sword on the shoulder (Клауд) or a hair ornament (Мику) stand higher. The head is the densest blob at the top
/// near the middle of the torso: arms, blades and staffs are thin. Checked on the eleven shipped companions.
/// </summary>
public static class CompanionHead
{
    /// <summary>The head's middle and its top row, in frame pixels; null for an empty frame.</summary>
    /// <param name="alpha">Row by row, one byte a pixel: 0 - transparent.</param>
    public static (int X, int Y)? Find(ReadOnlySpan<byte> alpha, int width, int height)
    {
        if (width <= 0 || height <= 0 || alpha.Length < width * height)
            return null;
        int top = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            if (alpha.Slice(y * width, width).IndexOfAnyExcept((byte)0) < 0)
                continue;
            if (top < 0) top = y;
            bottom = y;
        }
        if (top < 0)
            return null;
        var tall = bottom - top;

        // The torso's middle: the median column of the band below the head (shoulders to the belt).
        var torso = new List<int>();
        for (var y = top + (int)(tall * 0.2); y <= Math.Min(bottom, top + (int)(tall * 0.45)); y++)
            for (var x = 0; x < width; x++)
                if (alpha[y * width + x] != 0) torso.Add(x);
        if (torso.Count == 0)
            return null;
        torso.Sort();
        var middle = torso[torso.Count / 2];

        // The densest columns at the top, a head's width together, not far from the torso.
        var counts = new int[width];
        for (var y = top; y <= Math.Min(bottom, top + (int)(tall * 0.22)); y++)
            for (var x = 0; x < width; x++)
                if (alpha[y * width + x] != 0) counts[x]++;
        const int Window = 15;
        var reach = (int)(tall * 0.12);
        // The middle of the first run of the best sum: a head wider than the window gives a plateau.
        int best = -1, bestSum = -1, runEnd = -1;
        for (var x = Math.Max(0, middle - reach); x <= Math.Min(width - 1, middle + reach); x++)
        {
            var sum = 0;
            for (var i = Math.Max(0, x - Window / 2); i <= Math.Min(width - 1, x + Window / 2); i++)
                sum += counts[i];
            if (sum > bestSum)
            {
                best = runEnd = x;
                bestSum = sum;
            }
            else if (sum == bestSum && runEnd == x - 1)
                runEnd = x;
        }
        best = (best + runEnd) / 2;
        for (var y = top; y <= bottom; y++)
            if (alpha[y * width + best] != 0)
                return (best, y);
        return (best, top);
    }
}
