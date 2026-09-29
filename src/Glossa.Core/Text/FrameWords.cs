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

    private FrameWords(List<List<FrameWord>> lines, int line, int index)
    {
        _lines = lines;
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
        var sources = new List<OcrLine>();
        foreach (var line in ocrLines)
        {
            var words = WordsOf(line, lines.Count, cjk);
            if (words.Count == 0) continue;
            lines.Add(words);
            sources.Add(line);
        }
        if (lines.Count == 0) return new FrameWords(lines, 0, 0);

        const int RealText = 20;
        int? lowest = null;
        double lowestBottom = double.MinValue;
        var biggest = 0;
        var biggestSize = -1;
        var seen = new HashSet<OcrLine>();
        foreach (var line in sources)
        {
            if (!seen.Add(line)) continue;
            var paragraph = HitTester.ParagraphOf(sources, line);
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
        return new FrameWords(lines, lowest ?? biggest, 0);
    }

    /// <summary>Moves the highlight one word left or right (on to the next line at its end) or one line up or down.</summary>
    public bool Move(PadButtons direction)
    {
        if (_lines.Count == 0) return false;
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
        Index = Nearest(_lines[LineIndex], x);
        return true;

        double Middle(int i) => _lines[i].Average(w => w.Box.CenterY);
        bool Covers(int i, double at) => _lines[i][0].Box.Left - 40 <= at && at <= _lines[i][^1].Box.Right + 40;
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
