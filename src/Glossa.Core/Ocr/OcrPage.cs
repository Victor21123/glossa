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

public sealed record OcrLine(string Text, PixelRect Box, IReadOnlyList<OcrWord> Words, float Score);

/// <summary>OCR output for one captured region, already mapped to screen coordinates.</summary>
public sealed record OcrPage(IReadOnlyList<OcrLine> Lines, PixelRect Region, TimeSpan Elapsed)
{
    public static OcrPage Empty(PixelRect region) => new([], region, TimeSpan.Zero);
}
