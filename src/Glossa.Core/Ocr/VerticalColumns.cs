using Glossa.Core.Text;

namespace Glossa.Core.Ocr;

/// <summary>
/// What the engine does with the tall boxes the detector finds: pieces of one column are joined before reading (each
/// column is read once), a read with at least two kana or han becomes a vertical line that replaces the library's
/// clockwise garbage, and the small box the detector cuts out for a column-final "。" is attached to its column.
/// Pure logic: the read itself is a function, so the rules are testable without models.
/// </summary>
internal static class VerticalColumns
{
    /// <summary>A box this much taller than wide is a column candidate: the same trigger the library rotates on.</summary>
    private const double TallRatio = 1.5;

    /// <summary>Pieces share a column when their x-ranges overlap by more than this share of the narrower one.</summary>
    private const double SameColumnOverlap = 0.6;

    /// <summary>... and the gap between them along the column is under this many column widths.</summary>
    private const double PieceGap = 1.5;

    public static bool IsTall(PixelRect box) => box.Width > 0 && box.Height >= TallRatio * box.Width;

    /// <summary>A column is at least this many cells long: a lone I, a bar or a digit pair is taller than wide but not a column.</summary>
    private const double ColumnRatio = 2.5;

    /// <summary>... and at most this many: past it the box is a scrollbar or a rule, not a line of text.</summary>
    private const double LongestRatio = 40;

    /// <summary>Whether a detector box is long and thin enough to be a column of text (a candidate for the vertical read).</summary>
    public static bool IsColumn(PixelRect box) =>
        box.Width > 0 && box.Height >= ColumnRatio * box.Width && box.Height <= LongestRatio * box.Width;

    /// <summary>A Latin or digit reading this sure is text, not what the clockwise read makes of a column (that tops out at 0.84, mostly 0.4-0.65).</summary>
    private const float ConfidentScore = 0.8f;

    private static bool IsLatinLike(string text) => text.Any(c => Scripts.Of(c) is Script.Latin or Script.Digit or Script.Cyrillic);

    /// <summary>
    /// Whether a long thin box is worth the vertical read: its own text is Chinese or Japanese, or the page has some and the
    /// library did not read it with confidence as Latin letters or digits (a lone I, a number). Garbage and punctuation
    /// do not count as confident; a page with no Chinese or Japanese on it is never worth it.
    /// </summary>
    internal static bool CouldBeColumn(OcrLine line, bool pageHasCjk, float minScore) =>
        CountKanaHan(line.Text) >= EvidenceChars
        || (pageHasCjk && !(line.Score >= Math.Max(minScore, ConfidentScore) && IsLatinLike(line.Text)));

    /// <summary>
    /// Whether any line of the page, whatever its score, has Chinese or Japanese in it: the evidence that this page is worth
    /// a vertical read at all. A page of English and numbers never is.
    /// </summary>
    public static bool PageHasCjk(IReadOnlyList<OcrLine> lines, float minScore = 0.5f) =>
        lines.Where(l => l.Vertical || l.Score >= minScore).Sum(l => CountKanaHan(l.Text)) >= EvidenceChars;

    /// <summary>
    /// How many kana or han a page has to show (read with confidence), or a box in its own read, before it is worth a vertical
    /// read: one misread icon (口, 一, 目) on an English page is not a page of Japanese.
    /// </summary>
    private const int EvidenceChars = 2;

    private static double XOverlap(PixelRect a, PixelRect b) => Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);

    /// <summary>Whether two tall boxes are pieces of one column: one above the other, never side by side.</summary>
    internal static bool SameColumn(PixelRect a, PixelRect b)
    {
        var gap = Math.Max(b.Top - a.Bottom, a.Top - b.Bottom);
        return XOverlap(a, b) > SameColumnOverlap * Math.Min(a.Width, b.Width) && gap < PieceGap * Math.Max(a.Width, b.Width);
    }

    /// <summary>Whether a good part of <paramref name="box"/> lies on <paramref name="line"/> (a share of the box's own area).</summary>
    public static bool Covers(PixelRect line, PixelRect box)
    {
        var w = Math.Min(line.Right, box.Right) - Math.Max(line.Left, box.Left);
        var h = Math.Min(line.Bottom, box.Bottom) - Math.Max(line.Top, box.Top);
        return w > 0 && h > 0 && w * h > CoverShare * box.Width * box.Height;
    }

    private const double CoverShare = 0.3;

    /// <summary>Whether two boxes are one box as far as the detector's jitter goes (they overlap by more than 80% of their union).</summary>
    public static bool SameBox(PixelRect a, PixelRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return false;
        var inter = w * h;
        return inter > 0.8 * (a.Width * a.Height + b.Width * b.Height - inter);
    }

    /// <summary>
    /// A small, cell-sized box inside a column is a glyph the detector cut out of it (the library reads it as a stray
    /// character, or not at all); the column's own reading has it. A wider label that the library read with confidence
    /// stays.
    /// </summary>
    private static bool IsPieceOf(OcrLine line, List<OcrLine> columns, float minScore) =>
        !line.Vertical && !IsColumn(line.Box)
        && columns.Any(c => c.Box.Contains(line.Box.CenterX, line.Box.CenterY)
                            && line.Box.Width <= 1.2 * c.Box.Width && line.Box.Height <= 1.2 * c.Box.Width
                            // one glyph, or what the library did not trust: a real label over the column is kept
                            && (line.Score < minScore || !Scripts.ContainsCjk(line.Text) || line.Text.Trim().Length <= 1));

    /// <summary>The longest side of the second look at a whole screen (see <see cref="PassSide"/>).</summary>
    public const int WholeFramePassSide = 960;

    /// <summary>
    /// The longest side the detector sees in the second look at a bitmap whose longest side is <paramref name="maxSide"/>,
    /// cut from a screen <paramref name="screenWidth"/> wide (0: not known). The column detection depends on the size of the
    /// glyphs on the detector's input (the book frame's fourth column is whole at 0.5 and cut in pieces at 0.6-0.9), so a
    /// window gets the scale a whole screen gets: <see cref="WholeFramePassSide"/> over the screen's width.
    /// </summary>
    public static int PassSide(int maxSide, int screenWidth) =>
        screenWidth <= 0 ? WholeFramePassSide : Math.Clamp((int)Math.Round(maxSide * (double)WholeFramePassSide / screenWidth), 256, WholeFramePassSide);

    /// <summary>A box has to be this much longer than a column read already to replace it (the detector's boxes differ by a few pixels).</summary>
    private const double OutgrowLength = 1.15;

    /// <summary>
    /// Whether <paramref name="box"/> (the detector's, in the second look) is the same column as the vertical
    /// <paramref name="line"/> but longer: over the same x-range (and not much wider: two columns merged into a blob are
    /// not one) and at least 15% longer. The first look at a window may cut a column's top or bottom (the book frame's
    /// "ニャーニャー" came out as 321..997 of 130..1005); the longer box reads it whole.
    /// </summary>
    public static bool Outgrows(PixelRect box, OcrLine line)
    {
        if (!line.Vertical || box.Width <= 0 || line.Box.Width <= 0) return false;
        var overlap = XOverlap(box, line.Box);
        return overlap > 0.8 * Math.Min(box.Width, line.Box.Width) && box.Width <= 1.5 * line.Box.Width
            && box.Height >= OutgrowLength * line.Box.Height && box.Top <= line.Box.Top + 3 && box.Bottom >= line.Box.Bottom - 3;
    }

    /// <summary>A glyph the detector cut out of a column is about square and no bigger than this.</summary>
    private const double MaxGlyph = 160;

    /// <summary>A stack of this many glyphs one above the other is a shredded column (three are a short list of buttons).</summary>
    private const int ShreddedStack = 4;

    /// <summary>The centres of two glyphs of one column are this close, in glyph widths (a menu's entries differ in width and wander more).</summary>
    private const double GlyphAlignment = 0.3;

    /// <summary>
    /// Whether the library read a small box as text of its own: Chinese or Japanese, letters or digits, and with confidence.
    /// What it makes of a glyph cut out of a column is a stray letter, a symbol, nothing, or a poor score.
    /// </summary>
    private static bool ReadAsText(OcrLine line) =>
        line.Score >= ConfidentScore && line.Text.Any(c => char.IsLetterOrDigit(c) || Scripts.IsCjk(c));

    /// <summary>
    /// Whether the page shows a column the detector cut into glyphs (a thin column at native size: the fourth column of the
    /// book frame came out as boxes read "?", "X", "0"): at least <see cref="ShreddedStack"/> small, roughly square boxes,
    /// each under the previous one, their centres on one line (within <see cref="GlyphAlignment"/> of a glyph), and at
    /// least one of them not read as text by the library. A menu of single kanji or a column of numbers is read with
    /// confidence and does not count; a column that is found already (vertical lines) does not either. There is no tall
    /// box to read as a column, so the engine looks once more at a size where the detector keeps a column whole; a false
    /// alarm costs one detector pass.
    /// </summary>
    public static bool LooksShredded(IReadOnlyList<OcrLine> lines)
    {
        var glyphs = lines
            .Where(l => !l.Vertical && l.Box.Width > 0 && l.Box.Height > 0 && l.Box.Width <= MaxGlyph && l.Box.Height <= MaxGlyph
                        && l.Box.Width / l.Box.Height is >= 0.6 and <= 1.6)
            .OrderBy(l => l.Box.Top).ToList();
        if (glyphs.Count < ShreddedStack) return false;

        // the longest chain of glyphs, each below the previous one (they are sorted by top); a chain remembers whether it
        // holds a glyph the library could not read
        var stack = Enumerable.Repeat(1, glyphs.Count).ToArray();
        var garbage = glyphs.Select(g => !ReadAsText(g)).ToArray();
        for (var b = 0; b < glyphs.Count; b++)
            for (var a = 0; a < b; a++)
            {
                var (upper, lower) = (glyphs[a].Box, glyphs[b].Box);
                var gap = lower.Top - upper.Bottom;
                if (Math.Abs(upper.CenterX - lower.CenterX) <= GlyphAlignment * Math.Max(upper.Width, lower.Width)
                    && gap > -0.5 * Math.Min(upper.Height, lower.Height) && gap < PieceGap * Math.Max(upper.Width, lower.Width)
                    && (stack[a] + 1 > stack[b] || (stack[a] + 1 == stack[b] && garbage[a] && !garbage[b])))
                {
                    stack[b] = stack[a] + 1;
                    garbage[b] |= garbage[a];
                }
            }
        for (var i = 0; i < glyphs.Count; i++)
            if (stack[i] >= ShreddedStack && garbage[i]) return true;
        return false;
    }

    /// <summary>
    /// The page after the second look found longer boxes for columns the first look cut short (<paramref name="outgrown"/>:
    /// the old line and the longer box) and boxes of columns it missed (<paramref name="found"/>, blank lines): the longer
    /// boxes and the new ones are read in place of the old lines, and an old line whose longer box did not read as a column
    /// stays as it was. The vertical lines come last, right to left, as <see cref="Apply(IReadOnlyList{OcrLine}, Func{PixelRect, IReadOnlyList{PixelRect}, VerticalRead?}, float, ISet{OcrLine}?)"/> leaves them.
    /// </summary>
    public static List<OcrLine> ReplaceOutgrown(IReadOnlyList<OcrLine> lines, IReadOnlyList<(OcrLine Old, PixelRect Box)> outgrown,
        IReadOnlyList<OcrLine> found, Func<PixelRect, IReadOnlyList<PixelRect>, VerticalRead?> read, float minScore, ISet<OcrLine>? tried)
    {
        var result = Apply([.. lines.Where(l => !outgrown.Any(o => ReferenceEquals(o.Old, l))), .. found], read, minScore, tried);
        foreach (var (old, box) in outgrown)
            if (!result.Any(l => l.Vertical && Covers(l.Box, box))) result.Add(old);
        // an old line put back has to find its place among the columns
        var rows = result.Where(l => !l.Vertical);
        var columns = result.Where(l => l.Vertical).OrderByDescending(l => l.Box.Right);
        return [.. rows, .. columns];
    }

    private static int CountKanaHan(string text) => text.Count(c => Scripts.Of(c) is Script.Kana or Script.Han);

    /// <summary>What the library makes of a column-final "。" (a small circle): a letter o, a zero, a degree sign, or nothing.</summary>
    private static bool LooksLikePeriod(string text) => text.Trim() is "" or "o" or "O" or "0" or "°" or "º" or "○" or "。" or "." or "．";

    /// <summary>
    /// <paramref name="lines"/> are the library's lines in screen coordinates (blank ones included, their boxes matter);
    /// <paramref name="read"/> reads the column in a screen box, or returns null. Lines that are not columns come back as
    /// they were, in their order, followed by the vertical lines, right to left.
    /// A box is read only when it can be a column: long and thin (<see cref="IsColumn"/>), and not a Latin or digit box the
    /// library read with a score of <paramref name="minScore"/> or more; and (unless its own text is Chinese or Japanese)
    /// only on a page that has some (<see cref="PageHasCjk"/>). Groups that did not read are put in <paramref name="tried"/>
    /// and skipped when the same lines come again (the second look).
    /// </summary>
    public static List<OcrLine> Apply(IReadOnlyList<OcrLine> lines, Func<PixelRect, VerticalRead?> read, float minScore = 0.5f, ISet<OcrLine>? tried = null) =>
        Apply(lines, (box, _) => read(box), minScore, tried);

    /// <summary>
    /// The same, where <paramref name="read"/> also gets the boxes of the detector's pieces the column is made of (it may
    /// crop along their quadrilaterals, so that a leaning column is not cut with its neighbours).
    /// </summary>
    public static List<OcrLine> Apply(IReadOnlyList<OcrLine> lines, Func<PixelRect, IReadOnlyList<PixelRect>, VerticalRead?> read,
        float minScore = 0.5f, ISet<OcrLine>? tried = null)
    {
        var pageHasCjk = PageHasCjk(lines, minScore);
        var candidates = Enumerable.Range(0, lines.Count).Where(i =>
            !lines[i].Vertical && IsColumn(lines[i].Box) && tried?.Contains(lines[i]) != true).ToList();
        if (candidates.Count == 0) return [.. lines];

        // Pieces of one column, joined transitively.
        var group = candidates.ToDictionary(i => i, i => i);
        int Find(int i) => group[i] == i ? i : group[i] = Find(group[i]);
        for (var a = 0; a < candidates.Count; a++)
            for (var b = a + 1; b < candidates.Count; b++)
                if (SameColumn(lines[candidates[a]].Box, lines[candidates[b]].Box))
                    group[Find(candidates[b])] = Find(candidates[a]);

        var consumed = new HashSet<int>();
        var columns = new List<OcrLine>();
        var groups = candidates.GroupBy(Find).Select(g => g.ToList()).ToList();
        // A column read here is evidence for the boxes beside it: what was not worth a read at first is looked at again.
        bool progress;
        do
        {
            progress = false;
            foreach (var members in groups.ToList())
            {
                // pieces of one column are read together when any of them could be one
                if (!members.Any(i => CouldBeColumn(lines[i], pageHasCjk, minScore))) continue;
                groups.Remove(members);
                var box = members.Select(i => lines[i].Box).Aggregate((a, b) => a.Union(b));
                var result = read(box, members.Select(i => lines[i].Box).ToList());
                if (result is null || CountKanaHan(result.Text) < 2) // not a column: the library's lines stay
                {
                    if (tried is not null) foreach (var i in members) tried.Add(lines[i]);
                    continue;
                }
                foreach (var i in members) consumed.Add(i);
                columns.Add(new OcrLine(result.Text, box, result.Words, result.Score, Vertical: true));
                pageHasCjk = true;
                progress = true;
            }
        } while (progress && groups.Count > 0);
        if (columns.Count == 0) return [.. lines];

        var rest = new List<OcrLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            if (!TryAttachPeriod(lines[i], columns) && !IsPieceOf(lines[i], columns, minScore)) rest.Add(lines[i]);
        }
        columns.Sort((a, b) => b.Box.Right.CompareTo(a.Box.Right));
        rest.AddRange(columns);
        return rest;
    }

    /// <summary>
    /// A narrow box just under the end of a column, which the library read as a letter o or nothing, is the column's final
    /// "。". It becomes the column's last unit (and stretches its box), or, when the column already ends with one, goes away.
    /// </summary>
    private static bool TryAttachPeriod(OcrLine line, List<OcrLine> columns)
    {
        if (line.Vertical || IsTall(line.Box) || !LooksLikePeriod(line.Text)) return false;
        var box = line.Box;
        var index = -1;
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c].Box;
            var cell = col.Width;
            if (box.Width > 0.8 * cell || box.Height > 1.2 * cell || XOverlap(box, col) < 0.5 * box.Width) continue;
            if (box.Top < col.Bottom - cell || box.Top > col.Bottom + cell || box.Bottom <= col.Bottom) continue;
            if (index < 0 || Math.Abs(col.CenterX - box.CenterX) < Math.Abs(columns[index].Box.CenterX - box.CenterX)) index = c;
        }
        if (index < 0) return false;

        var column = columns[index];
        if (column.Text.EndsWith('。')) return true; // already read: the box is a duplicate, not a letter
        var old = column.Box;
        columns[index] = column with
        {
            Text = column.Text + "。",
            Words = [.. column.Words, new OcrWord("。", box, 0.5f)],
            Box = new PixelRect(old.Left, old.Top, old.Right, Math.Max(old.Bottom, box.Bottom)),
        };
        return true;
    }
}
