namespace Glossa.Core.Ocr;

/// <summary>A point of a column's quadrilateral, in bitmap pixels.</summary>
public readonly record struct QuadPoint(double X, double Y);

/// <summary>
/// A column of vertical text as the detector sees it: a rectangle that may lean (screenshots of books and scans lean
/// 4-6 degrees; an axis-aligned crop of such a column reads 0.5-0.75 against 0.87 when the crop follows the lean). The
/// corners are named for the upright column: the top is where the text starts. Pure geometry; the pixels are cropped by
/// <see cref="VerticalReader"/>.
/// </summary>
public readonly record struct ColumnQuad(QuadPoint TopLeft, QuadPoint TopRight, QuadPoint BottomRight, QuadPoint BottomLeft)
{
    /// <summary>A lean past this is not a lean of a page but a mistake of the detector (a diagonal blob): the column is read upright.</summary>
    private const double MaxTiltDegrees = 25;

    private static double Distance(QuadPoint a, QuadPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The length of the column, top to bottom.</summary>
    public double Length => (Distance(TopLeft, BottomLeft) + Distance(TopRight, BottomRight)) / 2;

    /// <summary>The width of the column.</summary>
    public double Width => (Distance(TopLeft, TopRight) + Distance(BottomLeft, BottomRight)) / 2;

    /// <summary>How far the column leans from the vertical, in degrees; positive when its top is to the right of its bottom.</summary>
    public double TiltDegrees => Math.Atan2(TopLeft.X - BottomLeft.X, BottomLeft.Y - TopLeft.Y) * 180 / Math.PI;

    /// <summary>The rectangle of an upright column.</summary>
    public static ColumnQuad FromRect(PixelRect box) =>
        new(new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom));

    /// <summary>
    /// The one rectangle that holds every quad (the pieces of one column), leaning as they do: the lean is that of the
    /// long sides of the quads (found whatever the order of their corners), and the rectangle is the bounding one along
    /// that axis. Null for no points. A lean beyond <see cref="MaxTiltDegrees"/> is ignored.
    /// </summary>
    public static ColumnQuad? Fit(IReadOnlyList<(double X, double Y)[]> quads)
    {
        quads = [.. quads.Where(q => q.Length == 4)]; // a quadrilateral has four corners; anything else is not one
        var points = quads.SelectMany(q => q).ToList();
        if (points.Count == 0) return null;

        double ax = 0, ay = 0;
        foreach (var quad in quads)
        {
            // Of the six distances between four corners of a rectangle the two shortest are its short sides, the next two
            // its long sides (the diagonals are longest); a column is longer than wide.
            var sides = new List<(double Length, double Dx, double Dy)>();
            for (var i = 0; i < 4; i++)
                for (var j = i + 1; j < 4; j++)
                {
                    var (dx, dy) = (quad[j].X - quad[i].X, quad[j].Y - quad[i].Y);
                    if (dy < 0 || (dy == 0 && dx < 0)) (dx, dy) = (-dx, -dy); // pointing down
                    sides.Add((Math.Sqrt(dx * dx + dy * dy), dx, dy));
                }
            sides.Sort((a, b) => a.Length.CompareTo(b.Length));
            foreach (var side in sides.Skip(2).Take(2))
            {
                ax += side.Dx;
                ay += side.Dy;
            }
        }
        var tilt = ay > 0 ? Math.Atan2(-ax, ay) : 0; // the angle of the axis from straight down; positive: the top leans right
        if (Math.Abs(tilt) * 180 / Math.PI > MaxTiltDegrees) tilt = 0;

        // u runs down the column, v across it (left to right).
        var (ux, uy) = (-Math.Sin(tilt), Math.Cos(tilt));
        var (vx, vy) = (Math.Cos(tilt), Math.Sin(tilt));
        double sMin = double.MaxValue, sMax = double.MinValue, tMin = double.MaxValue, tMax = double.MinValue;
        foreach (var (x, y) in points)
        {
            var (s, t) = (x * ux + y * uy, x * vx + y * vy);
            sMin = Math.Min(sMin, s); sMax = Math.Max(sMax, s);
            tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
        }
        QuadPoint At(double s, double t) => new(s * ux + t * vx, s * uy + t * vy);
        var fitted = new ColumnQuad(At(sMin, tMin), At(sMin, tMax), At(sMax, tMax), At(sMax, tMin));
        return fitted.Length < 1 || fitted.Width < 1 ? null : fitted; // identical points, a line: nothing to read
    }

    /// <summary>The quad grown by <paramref name="margin"/> pixels on every side along its own axes.</summary>
    public ColumnQuad Grow(double margin) => Grow(margin, margin, margin, margin);

    /// <summary>The quad grown by its own margin on each side, along its own axes (so it stays a rectangle, leaning as it did).</summary>
    public ColumnQuad Grow(double top, double right, double bottom, double left)
    {
        var (ux, uy) = Unit(TopLeft, BottomLeft);
        var (vx, vy) = Unit(TopLeft, TopRight);
        QuadPoint Move(QuadPoint p, double s, double t) => new(p.X + s * ux + t * vx, p.Y + s * uy + t * vy);
        return new ColumnQuad(Move(TopLeft, -top, -left), Move(TopRight, -top, right),
            Move(BottomRight, bottom, right), Move(BottomLeft, bottom, -left));
    }

    /// <summary>
    /// The quad grown by up to <paramref name="margin"/> on each side, each side by as much as keeps its corners inside a
    /// bitmap of the given size (a column at the bitmap's edge gets no margin there, and the quad stays a rectangle: moving
    /// corners one by one into the bitmap would skew it). <c>Top</c> and <c>Bottom</c> are the margins actually given along
    /// the column, where the characters' positions start and end.
    /// </summary>
    public (ColumnQuad Quad, double Top, double Bottom) GrowWithin(double margin, double width, double height)
    {
        var self = this;
        bool Inside(ColumnQuad q) => new[] { q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft }.All(p => p.X >= 0 && p.X <= width && p.Y >= 0 && p.Y <= height);
        double Largest(Func<double, ColumnQuad> grown)
        {
            for (var m = margin; m > 0; m -= 0.5)
                if (Inside(grown(m))) return m;
            return 0;
        }
        // one side after the other, each tested with the sides before it (on a leaning quad they move the same corners)
        var top = Largest(m => self.Grow(m, 0, 0, 0));
        var bottom = Largest(m => self.Grow(top, 0, m, 0));
        var left = Largest(m => self.Grow(top, 0, bottom, m));
        var right = Largest(m => self.Grow(top, m, bottom, left));
        return (Grow(top, right, bottom, left), top, bottom);
    }

    private static (double X, double Y) Unit(QuadPoint from, QuadPoint to)
    {
        var d = Distance(from, to);
        return d < 1e-9 ? (0, 0) : ((to.X - from.X) / d, (to.Y - from.Y) / d);
    }

    /// <summary>The quad with every corner moved inside a bitmap of the given size.</summary>
    public ColumnQuad Clamp(double width, double height)
    {
        QuadPoint C(QuadPoint p) => new(Math.Clamp(p.X, 0, width), Math.Clamp(p.Y, 0, height));
        return new ColumnQuad(C(TopLeft), C(TopRight), C(BottomRight), C(BottomLeft));
    }

    /// <summary>
    /// The rectangle that holds the part of the column between <paramref name="from"/> and <paramref name="to"/> pixels
    /// from its top: for a leaning column the slice moves sideways with the lean.
    /// </summary>
    public PixelRect Slice(double from, double to)
    {
        var length = Math.Max(Length, 1e-9);
        var (tl, tr, bl, br) = (TopLeft, TopRight, BottomLeft, BottomRight);
        QuadPoint[] points = [Lerp(tl, bl, from / length), Lerp(tr, br, from / length), Lerp(tl, bl, to / length), Lerp(tr, br, to / length)];
        return PixelRect.FromPoints(points.Select(p => (p.X, p.Y)));
    }

    /// <summary>The part of the column between <paramref name="from"/> and <paramref name="to"/> pixels from its top, as a column of its own.</summary>
    public ColumnQuad Part(double from, double to)
    {
        var length = Math.Max(Length, 1e-9);
        return new ColumnQuad(Lerp(TopLeft, BottomLeft, from / length), Lerp(TopRight, BottomRight, from / length),
            Lerp(TopRight, BottomRight, to / length), Lerp(TopLeft, BottomLeft, to / length));
    }

    private static QuadPoint Lerp(QuadPoint a, QuadPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
