using System.Text;
using Glossa.Core.Input;
using Glossa.Core.Ocr;

namespace Glossa.Core.Text;

/// <summary>A word on a still frame: its text and box in screen pixels, and its line.</summary>
public sealed record FrameWord(string Text, PixelRect Box, int Line);

/// <summary>
/// Choosing a word on a still frame with a gamepad (Остановить кадр, the gamepad combination): the recognized words in
/// reading order, the D-pad or stick moving the highlight, A looking up the highlighted one. Japanese and Chinese lines
/// are split into words by the same matcher as a lookup, so one step is one word, not one character.
/// </summary>
public sealed class FrameWords
{
    private readonly List<List<FrameWord>> _lines;
    private readonly List<bool> _vertical; // per line: a column of vertical text
    private readonly List<int> _paragraph; // per line: the paragraph it was read in (its columns share a number)

    private FrameWords(List<List<FrameWord>> lines, List<bool> vertical, List<int> paragraph, int line, int index)
    {
        _lines = lines;
        _vertical = vertical;
        _paragraph = paragraph;
        LineIndex = line;
        Index = index;
    }

    public int LineIndex { get; private set; }
    public int Index { get; private set; }
    public int LineCount => _lines.Count;
    public FrameWord? Current => _lines.Count == 0 ? null : _lines[LineIndex][Index];

    /// <summary>Every word on the frame, line by line.</summary>
    public IEnumerable<FrameWord> All => _lines.SelectMany(l => l);

    /// <summary>
    /// The words of a page, starting at the first word of the lowest block with real text in it: a dialogue box, the
    /// newest lines of a log or the answers to choose from sit low, while short HUD labels («Menu», «Day 3») do not count.
    /// Without such a block, the biggest one.
    /// </summary>
    public static FrameWords Build(OcrPage page, ITermMatcher? cjk)
    {
        var ocrLines = HitTester.DropFurigana(page.Lines).OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).ToList();
        var lines = new List<List<FrameWord>>();
        var vertical = new List<bool>();
        var paragraphs = new List<int>();
        var sources = new List<OcrLine>();
        var emitted = new HashSet<OcrLine>();
        foreach (var line in ocrLines)
        {
            if (emitted.Contains(line)) continue;
            // Columns are read as a whole paragraph at once, right to left; rows go by their top.
            var group = line.Vertical ? HitTester.ParagraphOf(ocrLines, line, emitted) : [line];
            var number = emitted.Count;
            foreach (var l in group)
            {
                emitted.Add(l);
                var words = WordsOf(l, lines.Count, cjk);
                if (words.Count == 0) continue;
                lines.Add(words);
                vertical.Add(l.Vertical);
                paragraphs.Add(number);
                sources.Add(l);
            }
        }
        if (lines.Count == 0) return new FrameWords(lines, vertical, paragraphs, 0, 0);

        const int RealText = 20;
        int? lowest = null;
        double lowestBottom = double.MinValue;
        var biggest = 0;
        var biggestSize = -1;
        var seen = new HashSet<OcrLine>();
        var listCache = HitTester.NewListCache();
        foreach (var line in sources)
        {
            if (!seen.Add(line)) continue;
            var paragraph = HitTester.ParagraphOf(sources, line, seen, listCache);
            foreach (var l in paragraph) seen.Add(l);
            var first = sources.IndexOf(paragraph[0]);
            var size = paragraph.Sum(l => l.Text.Length);
            if (size >= biggestSize)
            {
                biggestSize = size;
                biggest = first;
            }
            var bottom = paragraph.Max(l => l.Box.Bottom);
            if (size >= RealText && bottom > lowestBottom)
            {
                lowestBottom = bottom;
                lowest = first;
            }
        }
        return new FrameWords(lines, vertical, paragraphs, lowest ?? biggest, 0);
    }

    /// <summary>Moves the highlight one word left or right (on to the next line at its end) or one line up or down.</summary>
    public bool Move(PadButtons direction)
    {
        if (_lines.Count == 0) return false;
        if (_vertical[LineIndex] && MoveInColumns(direction) is { } moved) return moved;
        if ((direction & Pad.Right) != 0)
        {
            if (Index + 1 < _lines[LineIndex].Count) Index++;
            else if (LineIndex + 1 < _lines.Count) { LineIndex++; Index = 0; }
            else return false;
            return true;
        }
        if ((direction & Pad.Left) != 0)
        {
            if (Index > 0) Index--;
            else if (LineIndex > 0) { LineIndex--; Index = _lines[LineIndex].Count - 1; }
            else return false;
            return true;
        }
        var down = (direction & Pad.Down) != 0;
        if (!down && (direction & Pad.Up) == 0) return false;
        var x = Current!.Box.CenterX;
        var y = Current.Box.CenterY;
        // The nearest line above or below by its middle, preferring lines under the same column of text.
        var candidates = Enumerable.Range(0, _lines.Count)
            .Where(i => down ? Middle(i) > y + 1 : Middle(i) < y - 1)
            .OrderBy(i => Math.Abs(Middle(i) - y) + (Covers(i, x) ? 0 : 10_000))
            .ToList();
        if (candidates.Count == 0) return false;
        LineIndex = candidates[0];
        // Words of a column share their x: the height says where to enter it.
        Index = _vertical[LineIndex] ? NearestY(_lines[LineIndex], y) : Nearest(_lines[LineIndex], x);
        return true;

        double Middle(int i) => _lines[i].Average(w => w.Box.CenterY);
        bool Covers(int i, double at) => _lines[i][0].Box.Left - 40 <= at && at <= _lines[i][^1].Box.Right + 40;
    }

    /// <summary>
    /// Steps in vertical text. Down and Up go along the column and, past its end, on to the next or previous column of
    /// the same paragraph (never into another one). Left goes to the nearest column on the left, Right on the right, and
    /// enters it at the nearest height; with none on that side the highlight stays. Null for any other button.
    /// </summary>
    private bool? MoveInColumns(PadButtons direction)
    {
        var along = (direction & (Pad.Down | Pad.Up)) != 0;
        if (along)
        {
            var step = (direction & Pad.Down) != 0 ? 1 : -1;
            if (Index + step >= 0 && Index + step < _lines[LineIndex].Count)
            {
                Index += step;
                return true;
            }
            var next = LineIndex + step;
            if (next < 0 || next >= _lines.Count || !_vertical[next] || _paragraph[next] != _paragraph[LineIndex]) return false;
            LineIndex = next;
            Index = step > 0 ? 0 : _lines[next].Count - 1;
            return true;
        }

        var left = (direction & Pad.Left) != 0;
        if (!left && (direction & Pad.Right) == 0) return null;
        var x = Current!.Box.CenterX;
        var y = Current.Box.CenterY;
        // The nearest column on that side, preferring those that reach the current height.
        var target = Enumerable.Range(0, _lines.Count)
            .Where(i => i != LineIndex && _vertical[i] && (left ? Across(i) < x : Across(i) > x))
            .OrderBy(i => (Reaches(i, y) ? 0 : 10_000) + Math.Abs(Across(i) - x))
            .Select(i => (int?)i)
            .FirstOrDefault();
        if (target is not { } t) return false;
        LineIndex = t;
        Index = NearestY(_lines[t], y);
        return true;

        double Across(int i) => _lines[i][0].Box.CenterX;
        bool Reaches(int i, double at) => _lines[i].Min(w => w.Box.Top) <= at && at <= _lines[i].Max(w => w.Box.Bottom);
    }

    private static int NearestY(List<FrameWord> words, double y)
    {
        var best = 0;
        for (var i = 1; i < words.Count; i++)
            if (Math.Abs(words[i].Box.CenterY - y) < Math.Abs(words[best].Box.CenterY - y)) best = i;
        return best;
    }

    private static int Nearest(List<FrameWord> words, double x)
    {
        var best = 0;
        for (var i = 1; i < words.Count; i++)
            if (Math.Abs(words[i].Box.CenterX - x) < Math.Abs(words[best].Box.CenterX - x)) best = i;
        return best;
    }

    private static List<FrameWord> WordsOf(OcrLine line, int lineIndex, ITermMatcher? cjk)
    {
        var result = new List<FrameWord>();
        if (!Scripts.ContainsCjk(line.Text))
        {
            foreach (var w in line.Words)
                if (HitTester.TrimToWord(w.Text) is { Length: > 0 } word) result.Add(new FrameWord(word, w.Box, lineIndex));
            return result;
        }

        // CJK lines come back one character per unit: rebuild the text and walk it word by word.
        var sb = new StringBuilder();
        var unitAt = new List<int>();
        for (var i = 0; i < line.Words.Count; i++)
        {
            foreach (var _ in line.Words[i].Text) unitAt.Add(i);
            sb.Append(line.Words[i].Text);
        }
        var text = sb.ToString();
        for (var i = 0; i < text.Length;)
        {
            if (Scripts.Of(text[i]) is Script.Punctuation or Script.Space)
            {
                i++;
                continue;
            }
            var (start, length) = cjk?.Match(text, i) ?? (i, 1);
            var end = Math.Clamp(Math.Max(start + length, i + 1), i + 1, text.Length);
            // A dictionary phrase may run over a comma; the highlight stops before it.
            for (var c = i + 1; c < end; c++)
                if (Scripts.Of(text[c]) is Script.Punctuation or Script.Space) { end = c; break; }
            var box = line.Words[unitAt[i]].Box;
            for (var c = i + 1; c < end; c++) box = box.Union(line.Words[unitAt[c]].Box);
            result.Add(new FrameWord(text[i..end], box, lineIndex));
            i = end;
        }
        return result;
    }
}
