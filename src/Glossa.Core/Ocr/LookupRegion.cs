using Glossa.Core.Text;

namespace Glossa.Core.Ocr;

/// <summary>
/// The piece of screen a lookup recognizes around the cursor, and when it has to be taller: horizontal text is read in a
/// wide, low window (<see cref="Around"/>), but a column of vertical text is long, and a window that cuts it reads half
/// of it. When a column near the cursor reaches the window's top or bottom, the lookup reads once more in a window as tall
/// as the screen (<see cref="Taller"/>) - one more recognition, only for vertical text near the edge.
/// </summary>
public static class LookupRegion
{
    /// <summary>The window around the cursor: 1800 wide, 480 high (260 up, 220 down), the scale the recognizer was measured at.</summary>
    public const int HalfWidth = 900, Up = 260, Down = 220;

    /// <summary>The tall window is this wide in all: the columns of a paragraph beside the cursor, not the screen.</summary>
    public const int TallerWidth = 900;

    /// <summary>
    /// The tallest the tall window gets: the detector looks at a longer side shrunk to 960 px, and a 1440p column of
    /// 1300 px would come out thinner than it reads. A 1080p screen is taken whole.
    /// </summary>
    public const int TallerHeight = 1080;

    /// <summary>A line's edge this close to the window's edge counts as cut by it (the detector's boxes are loose by a few pixels).</summary>
    private const double EdgeSlack = 3;

    /// <summary>The column's reach: it is the cursor's column when the cursor is within this many column widths of it.</summary>
    private const double NearColumns = 2;

    /// <summary>The window of a lookup at a point (screen pixels; the capture clamps it to the monitor).</summary>
    public static PixelRect Around(double x, double y) => new(x - HalfWidth, y - Up, x + HalfWidth, y + Down);

    /// <summary>
    /// Whether the page read in <paramref name="roi"/> has a column near x (<paramref name="x"/> is the cursor's) that the
    /// window cuts at its top or bottom, so that a taller window may read more of it. Edges that are the frame's own do not
    /// count: there is no more screen to read. A column is a vertical line, or a tall box of CJK text the vertical read
    /// could not turn into one.
    /// </summary>
    public static bool NeedsTaller(OcrPage page, PixelRect roi, PixelRect frame, double x) => NeedsTaller(page, roi, frame, x, x);

    /// <summary>The same for a span of x (a zone drawn on the screen): columns within reach of any part of it.</summary>
    public static bool NeedsTaller(OcrPage page, PixelRect roi, PixelRect frame, double xFrom, double xTo)
    {
        var top = Math.Max(roi.Top, frame.Top);
        var bottom = Math.Min(roi.Bottom, frame.Bottom);
        var canGrowUp = top > frame.Top + 0.5;
        var canGrowDown = bottom < frame.Bottom - 0.5;
        if (!canGrowUp && !canGrowDown) return false;

        foreach (var line in page.Lines)
        {
            if (!IsColumn(line)) continue;
            var reach = NearColumns * line.Box.Width;
            if (line.Box.Left - reach > xTo || line.Box.Right + reach < xFrom) continue;
            if ((canGrowUp && line.Box.Top <= top + EdgeSlack) || (canGrowDown && line.Box.Bottom >= bottom - EdgeSlack)) return true;
        }
        return false;
    }

    /// <summary>
    /// The first page of a lookup together with its taller second one. The tall window is narrower than the first, so the
    /// first page keeps its horizontal lines whole (a subtitle 1400 px wide is not cut at the tall window's sides); from the
    /// tall page come the vertical lines - the columns read whole in place of their cut versions - and any other line the
    /// first page has not got. A first-page line is dropped when a column of the tall page lies over it (a glyph cut out
    /// of the column, a cut copy of it); one of the tall page that overlaps a line of the first is its cut copy, dropped.
    /// The columns come last, right to left.
    /// </summary>
    public static OcrPage Merge(OcrPage first, OcrPage taller)
    {
        var columns = taller.Lines.Where(l => l.Vertical).ToList();
        // a first-page line is the column's own if the column's box holds its middle, or covers most of it
        bool UnderColumn(OcrLine l) => columns.Any(c => c.Box.Contains(l.Box.CenterX, l.Box.CenterY) && (l.Vertical || l.Box.Width <= 1.2 * c.Box.Width));
        var kept = first.Lines.Where(l => !UnderColumn(l)).ToList();
        var rows = kept.Where(l => !l.Vertical).ToList();
        var others = taller.Lines.Where(l => !l.Vertical && !kept.Any(k => !k.Vertical && Overlap(k.Box, l.Box))).ToList();
        var allColumns = kept.Where(l => l.Vertical).Concat(columns).OrderByDescending(l => l.Box.Right).ToList();
        var region = first.Region.Union(taller.Region);
        return new OcrPage([.. rows, .. others, .. allColumns], region, first.Elapsed + taller.Elapsed);
    }

    /// <summary>Whether two boxes lie over each other by more than half of the smaller one.</summary>
    private static bool Overlap(PixelRect a, PixelRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 && w * h > 0.5 * Math.Min(a.Width * a.Height, b.Width * b.Height);
    }

    private static bool IsColumn(OcrLine line) =>
        line.Vertical; // a tall box read as something else is not evidence of a column: one stray glyph must not cost a second recognition

    /// <summary>
    /// The tall window: <see cref="TallerWidth"/> wide around the cursor and as high as the screen (at most
    /// <see cref="TallerHeight"/>), moved, not cut, to lie inside the frame.
    /// </summary>
    public static PixelRect Taller(double x, double y, PixelRect frame)
    {
        var width = Math.Min(TallerWidth, frame.Width);
        var height = Math.Min(TallerHeight, frame.Height);
        var left = Math.Clamp(x - width / 2, frame.Left, frame.Right - width);
        var top = Math.Clamp(y - height / 2, frame.Top, frame.Bottom - height);
        return new PixelRect(left, top, left + width, top + height);
    }
}
