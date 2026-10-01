using Glossa.Core.Ocr;

namespace Glossa.Core.Text;

/// <summary>A paragraph on screen: its lines joined into one text, and the box around them. <paramref name="Vertical"/>: columns of vertical text.</summary>
public sealed record TextBlock(string Text, PixelRect Box, int Lines, bool Vertical = false);

/// <summary>
/// The screen as paragraphs, for translation rather than for a word: the one under the cursor (Только перевод:
/// «Реплика»), all of them (перевод экрана), or the dialogue (живой перевод). Lines join into paragraphs by the same rule
/// as a lookup's context: close, overlapping and of one font size; furigana is dropped.
/// </summary>
public static class TextBlocks
{
    private const int RealText = 20;

    /// <summary>The page's text as translated whole («Зона»): its paragraphs top to bottom, one per line.</summary>
    public static string Joined(OcrPage page) =>
        string.Join("\n", Of(page).Select(b => b.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>Every paragraph, top to bottom.</summary>
    public static IReadOnlyList<TextBlock> Of(OcrPage page)
    {
        var lines = HitTester.DropFurigana(page.Lines).OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).ToList();
        var seen = new HashSet<OcrLine>();
        var listCache = HitTester.NewListCache();
        var blocks = new List<TextBlock>();
        foreach (var line in lines)
        {
            if (seen.Contains(line)) continue;
            var paragraph = HitTester.ParagraphOf(lines, line, seen, listCache);
            foreach (var l in paragraph) seen.Add(l);
            blocks.Add(Block(paragraph));
        }
        return blocks;
    }

    /// <summary>The paragraph of the line nearest to the point, if a line is close enough to be the one pointed at.</summary>
    public static TextBlock? At(OcrPage page, double x, double y)
    {
        var lines = HitTester.DropFurigana(page.Lines);
        OcrLine? nearest = null;
        var best = double.MaxValue;
        foreach (var l in lines)
        {
            var d = l.Box.DistanceTo(x, y);
            if (d < best) { best = d; nearest = l; }
        }
        if (nearest is null || best > Math.Max(nearest.Thickness * 0.75, 12)) return null;
        return Block(HitTester.ParagraphOf(lines, nearest));
    }

    /// <summary>
    /// The dialogue: the lowest paragraph with real text (a dialogue box, the newest log lines, the answers sit low),
    /// else the biggest; short labels («Menu», «Day 3») never count.
    /// </summary>
    public static TextBlock? Dialogue(IReadOnlyList<TextBlock> blocks) =>
        blocks.Where(b => Letters(b.Text) >= RealText).MaxBy(b => b.Box.Bottom)
        ?? blocks.Where(b => Letters(b.Text) > 0).MaxBy(b => Letters(b.Text));

    /// <summary>
    /// Worth translating: letters in a script other than the target's (Russian text stays as it is), and more than a
    /// stray symbol or a number from the interface.
    /// </summary>
    public static bool Translatable(TextBlock block, string target)
    {
        var letters = Letters(block.Text);
        if (letters < 2 || block.Text.Count(char.IsDigit) >= letters) return false; // «Day 1 1000.00», «160/160»: the interface's numbers
        var native = block.Text.Count(c => Scripts.Of(c) == (target == "ru" ? Script.Cyrillic : Script.Latin));
        if (native > letters / 2) return false;
        var cjk = block.Text.Count(Scripts.IsCjk);
        return cjk >= 1 || letters >= 3;
    }

    /// <summary>The same text, ignoring spaces, punctuation and case: a re-read of the same line is not a new line.</summary>
    public static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int Letters(string text) => text.Count(char.IsLetter);

    private static TextBlock Block(List<OcrLine> paragraph)
    {
        var (text, _) = HitTester.JoinParagraph(paragraph);
        var box = paragraph[0].Box;
        foreach (var l in paragraph.Skip(1)) box = box.Union(l.Box);
        return new TextBlock(text, box, paragraph.Count, paragraph[0].Vertical);
    }

    /// <summary>Narrowest plate for a translation laid over a vertical block: its columns are too thin to hold a line of it.</summary>
    public const double MinPlateWidth = 240;

    /// <summary>
    /// Where to lay the translation of a block over the frame: a horizontal block's own box; a vertical one's box widened
    /// to at least <see cref="MinPlateWidth"/> around its middle, the same top and height, moved inside the frame at its
    /// sides (narrower only when the frame itself is).
    /// </summary>
    public static PixelRect PlateBox(TextBlock block, PixelRect frame)
    {
        if (!block.Vertical) return block.Box;
        var width = Math.Min(Math.Max(block.Box.Width, MinPlateWidth), frame.Width);
        var left = Math.Clamp(block.Box.CenterX - width / 2, frame.Left, frame.Right - width);
        return new PixelRect(left, block.Box.Top, left + width, block.Box.Bottom);
    }

    /// <summary>
    /// The plate of a vertical block where other things already stand (<paramref name="occupied"/>: the other blocks of the
    /// frame and the plates placed): the extra width goes to the side that covers least of them - centred, all to the left
    /// or all to the right of the column (the original text stays under the plate either way). With nothing in the way it
    /// is <see cref="PlateBox(TextBlock, PixelRect)"/>; a horizontal block's plate is its box.
    /// </summary>
    public static PixelRect PlateBox(TextBlock block, PixelRect frame, IReadOnlyList<PixelRect> occupied)
    {
        var centred = PlateBox(block, frame);
        if (!block.Vertical || occupied.Count == 0 || centred.Width <= block.Box.Width) return centred;
        PixelRect At(double left)
        {
            left = Math.Clamp(left, frame.Left, frame.Right - centred.Width);
            return new PixelRect(left, centred.Top, left + centred.Width, centred.Bottom);
        }
        double Covered(PixelRect plate) => occupied.Sum(o =>
            Math.Max(0, Math.Min(plate.Right, o.Right) - Math.Max(plate.Left, o.Left))
            * Math.Max(0, Math.Min(plate.Bottom, o.Bottom) - Math.Max(plate.Top, o.Top)));
        return new[] { centred, At(block.Box.Right - centred.Width), At(block.Box.Left) }.MinBy(Covered);
    }
}

/// <summary>
/// A tiny grey thumbnail of a screen region, to notice that the picture changed (a new line of dialogue) without
/// recognizing text on every frame: 64x36 cells, each the average brightness of its patch, sampled sparsely.
/// </summary>
public static class ScreenFingerprint
{
    public const int Width = 64, Height = 36;

    public static byte[] Of(byte[] bgra, int width, int height, int stride)
    {
        var result = new byte[Width * Height];
        for (var cy = 0; cy < Height; cy++)
            for (var cx = 0; cx < Width; cx++)
            {
                int x0 = cx * width / Width, x1 = (cx + 1) * width / Width, y0 = cy * height / Height, y1 = (cy + 1) * height / Height;
                long sum = 0, n = 0;
                for (var y = y0; y < y1; y += 3)
                    for (var x = x0; x < x1; x += 3)
                    {
                        var i = y * stride + x * 4;
                        sum += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
                        n++;
                    }
                result[cy * Width + cx] = (byte)(n == 0 ? 0 : sum / n);
            }
        return result;
    }

    /// <summary>
    /// Whether two thumbnails differ by more than noise: a cell counts as changed when its brightness moved by more than
    /// 6 of 255, and a new line of text changes at least a handful of cells.
    /// </summary>
    public static bool Differs(byte[]? a, byte[]? b, int cells = 4)
    {
        if (a is null || b is null || a.Length != b.Length) return true;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 6 && ++changed >= cells) return true;
        return false;
    }
}
