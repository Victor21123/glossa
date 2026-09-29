using System.Text;
using Glossa.Core.Ocr;

namespace Glossa.Core.Text;

/// <summary>The word under the cursor together with the text around it.</summary>
/// <param name="Score">The recognizer's confidence in the word, its least sure letter or piece (1 for a typed word).</param>
public sealed record WordHit(
    string Word,
    PixelRect Box,
    string Line,
    string Context,
    int ContextOffset,
    Script Script,
    float Score = 1f);

/// <summary>
/// Finds word boundaries in text written without spaces (Japanese, Chinese).
/// Given the index of the character under the cursor, returns the span of the word covering it.
/// </summary>
public interface ITermMatcher
{
    (int Start, int Length) Match(string text, int index);
}

public sealed class HitTester
{
    private const int MaxContextChars = 400;

    /// <summary>Returns the word nearest to (x, y) or null when there is no text close enough.</summary>
    public WordHit? Hit(OcrPage page, double x, double y, ITermMatcher? cjkMatcher = null)
    {
        var lines = DropFurigana(page.Lines);
        var line = NearestLine(lines, x, y);
        if (line is null || line.Words.Count == 0) return null;

        var unitIndex = NearestUnit(line.Words, x);
        var unit = line.Words[unitIndex];
        var script = Scripts.Dominant(unit.Text);

        string word;
        PixelRect box;
        float score;
        if (Scripts.IsCjk(script) || (script == Script.Other && Scripts.ContainsCjk(line.Text)))
        {
            (word, box, score) = CjkWord(line, unitIndex, cjkMatcher);
            if (script == Script.Other) script = Scripts.Dominant(word);
        }
        else
        {
            word = TrimToWord(unit.Text);
            box = unit.Box;
            score = unit.Score;
            if (word.Length == 0) return null;
        }

        var paragraph = ParagraphOf(lines, line);
        var (text, lineStarts) = JoinParagraph(paragraph);
        var lineIdx = paragraph.IndexOf(line);
        var searchFrom = lineStarts[lineIdx];
        var offset = text.IndexOf(word, searchFrom, StringComparison.Ordinal);
        var (context, contextOffset) = SentenceAround(text, offset, word.Length);

        return new WordHit(word, box, line.Text, context, contextOffset, script, score);
    }

    /// <summary>
    /// Furigana/ruby lines are much smaller than the text below them; they would pollute the context
    /// and are never what the user points at when a full-size line is under the cursor.
    /// </summary>
    internal static List<OcrLine> DropFurigana(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count < 2) return [.. lines];
        var heights = lines.Select(l => l.Box.Height).OrderBy(h => h).ToArray();
        var median = heights[heights.Length / 2];
        return lines
            .Where(l => !(l.Box.Height < median * 0.6 && Scripts.Dominant(l.Text) == Script.Kana
                          && lines.Any(o => o != l && o.Box.Top >= l.Box.Bottom - 2
                                            && o.Box.Top - l.Box.Bottom < median * 0.5
                                            && Overlaps(o.Box, l.Box))))
            .ToList();
    }

    private static OcrLine? NearestLine(List<OcrLine> lines, double x, double y)
    {
        OcrLine? best = null;
        var bestDist = double.MaxValue;
        foreach (var l in lines)
        {
            var d = l.Box.DistanceTo(x, y);
            if (d < bestDist) { bestDist = d; best = l; }
        }
        if (best is null) return null;
        var tolerance = Math.Max(best.Box.Height * 0.75, 12);
        return bestDist <= tolerance ? best : null;
    }

    private static int NearestUnit(IReadOnlyList<OcrWord> words, double x)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < words.Count; i++)
        {
            var b = words[i].Box;
            var d = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static (string Word, PixelRect Box, float Score) CjkWord(OcrLine line, int unitIndex, ITermMatcher? matcher)
    {
        // CJK lines come back as one unit per character; rebuild the line and map char index <-> unit.
        var sb = new StringBuilder();
        var unitAt = new List<int>();
        for (var i = 0; i < line.Words.Count; i++)
        {
            foreach (var _ in line.Words[i].Text) unitAt.Add(i);
            sb.Append(line.Words[i].Text);
        }
        var text = sb.ToString();
        var charIndex = unitAt.IndexOf(unitIndex);
        if (charIndex < 0) return (line.Words[unitIndex].Text, line.Words[unitIndex].Box, line.Words[unitIndex].Score);

        var (start, length) = matcher?.Match(text, charIndex) ?? (charIndex, 1);
        start = Math.Clamp(start, 0, text.Length - 1);
        length = Math.Clamp(length, 1, text.Length - start);

        var box = line.Words[unitAt[start]].Box;
        var score = line.Words[unitAt[start]].Score;
        for (var c = start + 1; c < start + length; c++)
        {
            box = box.Union(line.Words[unitAt[c]].Box);
            score = Math.Min(score, line.Words[unitAt[c]].Score);
        }
        return (text.Substring(start, length), box, score);
    }

    internal static string TrimToWord(string token)
    {
        var s = 0;
        var e = token.Length - 1;
        while (s <= e && !IsCore(token[s])) s++;
        while (e >= s && !IsCore(token[e])) e--;
        return s > e ? "" : token[s..(e + 1)];

        static bool IsCore(char c) => Scripts.Of(c) is Script.Latin or Script.Cyrillic or Script.Digit;
    }

    /// <summary>
    /// Lines of the same paragraph: from the anchor up and down its column, each next line the nearest one above or below
    /// that shares some width with it, while it is close and of the same font size. Lines of a column beside it (a second
    /// panel, another window) do not break the paragraph.
    /// </summary>
    internal static List<OcrLine> ParagraphOf(List<OcrLine> lines, OcrLine anchor)
    {
        var result = new List<OcrLine> { anchor };
        for (var current = anchor; Neighbour(lines, current, above: true, result) is { } up && SameParagraph(up, current); current = up)
            result.Insert(0, up);
        for (var current = anchor; Neighbour(lines, current, above: false, result) is { } down && SameParagraph(current, down); current = down)
            result.Add(down);
        return result;
    }

    /// <summary>The nearest line above (or below) <paramref name="line"/> in its column, not yet taken.</summary>
    private static OcrLine? Neighbour(List<OcrLine> lines, OcrLine line, bool above, List<OcrLine> taken)
    {
        OcrLine? best = null;
        foreach (var l in lines)
        {
            if (!Overlaps(l.Box, line.Box) || taken.Contains(l)) continue;
            var side = l.Box.CenterY - line.Box.CenterY;
            if (above ? side >= 0 : side <= 0) continue;
            if (best is null || Math.Abs(side) < Math.Abs(best.Box.CenterY - line.Box.CenterY)) best = l;
        }
        return best;
    }

    private static bool SameParagraph(OcrLine upper, OcrLine lower)
    {
        var h = Math.Max(upper.Box.Height, lower.Box.Height);
        var gap = lower.Box.Top - upper.Box.Bottom;
        // Lines of one paragraph share a font size; a speaker name or a note under the text is usually smaller or
        // bolder, and must not leak into the context. Kana and kanji are as wide as the font is big, so their size is
        // the width per character: the height of their boxes changes with the characters (おすすめ is lower than 観察力).
        var cjk = IsCjkLine(upper) && IsCjkLine(lower);
        double a = Size(upper, cjk), b = Size(lower, cjk);
        var ratio = Math.Max(a, b) / Math.Max(1, Math.Min(a, b));
        return gap < h * 0.9 && gap > -h * 0.5 && ratio < 1.2 && Overlaps(upper.Box, lower.Box);
    }

    private static bool IsCjkLine(OcrLine line) => Scripts.Dominant(line.Text) is Script.Kana or Script.Han;

    private static double Size(OcrLine line, bool cjk) =>
        cjk ? line.Box.Width / Math.Max(1, line.Text.Count(c => !char.IsWhiteSpace(c))) : line.Box.Height;

    private static bool Overlaps(PixelRect a, PixelRect b) => a.Left < b.Right && b.Left < a.Right;

    internal static (string Text, int[] LineStarts) JoinParagraph(List<OcrLine> paragraph)
    {
        var sb = new StringBuilder();
        var starts = new int[paragraph.Count];
        for (var i = 0; i < paragraph.Count; i++)
        {
            var t = paragraph[i].Text.Trim();
            if (i > 0 && sb.Length > 0)
            {
                var prev = sb[^1];
                if (prev == '-' && t.Length > 0 && char.IsLower(t[0]))
                    sb.Length--; // re-join a word hyphenated across lines
                else if (!(Scripts.IsCjk(prev) || (t.Length > 0 && Scripts.IsCjk(t[0]))))
                    sb.Append(' ');
            }
            starts[i] = sb.Length;
            sb.Append(t);
        }
        return (sb.ToString(), starts);
    }

    /// <summary>The sentence containing [offset, offset+length); falls back to the whole (capped) text.</summary>
    internal static (string Context, int Offset) SentenceAround(string text, int offset, int length)
    {
        if (offset < 0) return (Cap(text), -1);

        var start = offset;
        while (start > 0 && !Scripts.IsSentenceEnd(text[start - 1])) start--;
        var end = offset + length;
        while (end < text.Length && !Scripts.IsSentenceEnd(text[end])) end++;
        while (end < text.Length && (Scripts.IsSentenceEnd(text[end]) || text[end] is '"' or '」' or '』' or '”' or ')')) end++;

        while (start < offset && char.IsWhiteSpace(text[start])) start++;
        var sentence = text[start..end];

        // Very short sentences ("Hello.") carry little meaning on their own: widen to the paragraph.
        if (sentence.Length < 25 && text.Length > sentence.Length)
        {
            var capped = Cap(text);
            var off = capped.IndexOf(text.Substring(offset, length), StringComparison.Ordinal);
            return (capped, off);
        }
        if (sentence.Length > MaxContextChars)
        {
            var from = Math.Max(0, offset - start - MaxContextChars / 2);
            var take = Math.Min(MaxContextChars, sentence.Length - from);
            return (sentence.Substring(from, take), offset - start - from);
        }
        return (sentence, offset - start);
    }

    private static string Cap(string text) => text.Length <= MaxContextChars ? text : text[..MaxContextChars];
}
