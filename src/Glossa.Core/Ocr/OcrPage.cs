namespace Glossa.Core.Ocr;

/// <summary>Axis-aligned rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public PixelRect Offset(double dx, double dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public PixelRect Union(PixelRect other) => new(
        Math.Min(Left, other.Left), Math.Min(Top, other.Top),
        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    /// <summary>Distance from a point to the rectangle (0 when inside).</summary>
    public double DistanceTo(double x, double y)
    {
        var dx = Math.Max(Math.Max(Left - x, 0), x - Right);
        var dy = Math.Max(Math.Max(Top - y, 0), y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static PixelRect FromPoints(IEnumerable<(double X, double Y)> points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var (x, y) in points)
        {
            l = Math.Min(l, x); t = Math.Min(t, y);
            r = Math.Max(r, x); b = Math.Max(b, y);
        }
        return new PixelRect(l, t, r, b);
    }
}

/// <summary>
/// A recognized unit inside a line. For Latin/Cyrillic text this is a whitespace-separated
/// word; for CJK lines the OCR engine returns one unit per character.
/// </summary>
public sealed record OcrWord(string Text, PixelRect Box, float Score);

/// <summary>
/// A recognized line. <paramref name="Vertical"/> is a column of vertical text (tategaki): its units run top to bottom and
/// its columns follow each other right to left; the engine sets it, nothing here guesses it from the box.
/// </summary>
public sealed record OcrLine(string Text, PixelRect Box, IReadOnlyList<OcrWord> Words, float Score, bool Vertical = false)
{
    /// <summary>The size of the font across the line: the height of a row, the width of a column.</summary>
    public double Thickness => Vertical ? Box.Width : Box.Height;
}

/// <summary>«Зона» (Только перевод): a rectangle drawn around the text to translate.</summary>
public static class Zones
{
    /// <summary>The height of screen a lookup reads around the cursor, the scale the recognizer was measured at.</summary>
    public const int Reach = 480;

    /// <summary>
    /// The piece of screen to recognize for a zone: the zone, grown to at least <see cref="Reach"/> each way around its
    /// middle. The detector sizes a picture by its short side (up to 736 px): a low zone alone was blown up four times
    /// and its lines cut into pieces at the spaces (2026-09-29, P5R); only the words inside the zone are kept after.
    /// </summary>
    public static PixelRect Around(PixelRect zone) => new(
        Math.Min(zone.Left, zone.CenterX - Reach / 2.0), Math.Min(zone.Top, zone.CenterY - Reach / 2.0),
        Math.Max(zone.Right, zone.CenterX + Reach / 2.0), Math.Max(zone.Bottom, zone.CenterY + Reach / 2.0));

    /// <summary>
    /// The piece of a zone read once more when a column of vertical text reaches the edge of <paramref name="piece"/> (see
    /// <see cref="LookupRegion.NeedsTaller(OcrPage, PixelRect, PixelRect, double, double)"/>): <see cref="Reach"/> more above
    /// and below, inside the frame, and no taller than <see cref="LookupRegion.TallerHeight"/> (the detector looks at a longer
    /// side shrunk): a piece already that tall stays as it is.
    /// </summary>
    public static PixelRect Taller(PixelRect piece, PixelRect frame)
    {
        var top = Math.Max(frame.Top, piece.Top - Reach);
        var bottom = Math.Min(frame.Bottom, piece.Bottom + Reach);
        if (bottom - top > LookupRegion.TallerHeight)
        {
            var height = Math.Max(LookupRegion.TallerHeight, piece.Height);
            top = Math.Clamp(piece.CenterY - height / 2, frame.Top, Math.Max(frame.Top, frame.Bottom - height));
            bottom = Math.Min(frame.Bottom, top + height);
        }
        return new PixelRect(piece.Left, top, piece.Right, bottom);
    }
}

/// <summary>OCR output for one captured region, already mapped to screen coordinates.</summary>
public sealed record OcrPage(IReadOnlyList<OcrLine> Lines, PixelRect Region, TimeSpan Elapsed)
{
    public static OcrPage Empty(PixelRect region) => new([], region, TimeSpan.Zero);

    /// <summary>
    /// What lies inside <paramref name="zone"/>: the lines whose middle height is in it, each with only the words whose
    /// middle is in it (a line the zone cuts keeps its words inside, joined as the recognizer joins them).
    /// </summary>
    public OcrPage Within(PixelRect zone)
    {
        var lines = new List<OcrLine>();
        foreach (var line in Lines)
        {
            // A column is tall: its middle may lie outside a zone that still holds some of its words.
            if (!line.Vertical && (line.Box.CenterY < zone.Top || line.Box.CenterY > zone.Bottom)) continue;
            var words = line.Words.Where(w => zone.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
            if (words.Count == 0) continue;
            if (words.Count == line.Words.Count)
            {
                lines.Add(line);
                continue;
            }
            var text = string.Join(Text.Scripts.ContainsCjk(line.Text) ? "" : " ", words.Select(w => w.Text));
            lines.Add(new OcrLine(text, words.Select(w => w.Box).Aggregate((a, b) => a.Union(b)), words, words.Average(w => w.Score), line.Vertical));
        }
        return this with { Lines = lines, Region = zone };
    }
}
