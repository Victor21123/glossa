using System.Text;
using Glossa.Core.Ocr;

namespace Glossa.Core.Text;

/// <summary>The word under the cursor together with the text around it.</summary>
/// <param name="Score">The recognizer's confidence in the word, its least sure letter or piece (1 for a typed word).</param>
/// <param name="Vertical">The word stands in a column of vertical text.</param>
public sealed record WordHit(
    string Word,
    PixelRect Box,
    string Line,
    string Context,
    int ContextOffset,
    Script Script,
    float Score = 1f,
    bool Vertical = false)
{
    /// <summary>
    /// The same place with the word as the user spelled it (a misread corrected in the card): the sentence and the line
    /// read it that way too, and the word is as sure as a typed one.
    /// </summary>
    public WordHit Respelled(string word)
    {
        // The misread word under the cursor: at its offset, else the occurrence nearest to it.
        var offset = At(Context, ContextOffset);
        var context = offset >= 0 ? Context[..offset] + word + Context[(offset + Word.Length)..] : Context;
        if (offset < 0) offset = Context.IndexOf(word, StringComparison.Ordinal);

        // The same one in the line, placed by where the line sits in the sentence.
        var lineStart = Context.IndexOf(Line, StringComparison.Ordinal);
        var inLine = At(Line, lineStart >= 0 && offset >= lineStart ? offset - lineStart : 0);
        var line = inLine >= 0 ? Line[..inLine] + word + Line[(inLine + Word.Length)..] : Line;
        return this with { Word = word, Context = context, ContextOffset = offset, Line = line, Script = Scripts.Dominant(word), Score = 1f };
    }

    private int At(string text, int near)
    {
        if (near >= 0 && near + Word.Length <= text.Length && string.CompareOrdinal(text, near, Word, 0, Word.Length) == 0) return near;
        var best = -1;
        for (var i = text.IndexOf(Word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(Word, i + 1, StringComparison.Ordinal))
            if (best < 0 || Math.Abs(i - near) < Math.Abs(best - near)) best = i;
        return best;
    }
}

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

        var unitIndex = NearestUnit(line, x, y);
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

        return new WordHit(word, box, line.Text, context, contextOffset, script, score, line.Vertical);
    }

    /// <summary>
    /// Furigana/ruby lines are much smaller than the text below them; they would pollute the context
    /// and are never what the user points at when a full-size line is under the cursor.
    /// </summary>
    internal static List<OcrLine> DropFurigana(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count < 2) return [.. lines];
        // Rows and columns are judged apart: a column's width is not a row's height.
        var rows = lines.Where(l => !l.Vertical).ToList();
        var columns = lines.Where(l => l.Vertical).ToList();
        var rowMedian = rows.Count == 0 ? 0 : rows.Select(l => l.Box.Height).OrderBy(h => h).ToArray()[rows.Count / 2];
        return lines
            .Where(l => !(l.Vertical ? IsRubyColumn(l, columns) : IsRubyRow(l, rows, rowMedian)))
            .ToList();
    }

    private static bool IsRubyRow(OcrLine l, List<OcrLine> rows, double median) =>
        l.Box.Height < median * 0.6 && Scripts.Dominant(l.Text) == Script.Kana
        && rows.Any(o => o != l && o.Box.Top >= l.Box.Bottom - 2
                         && o.Box.Top - l.Box.Bottom < median * 0.5
                         && Overlaps(o.Box, l.Box));

    /// <summary>
    /// Ruby of a column: a column of kana only, much narrower than the column of kanji it stands right next to (to its
    /// right, along it, not longer). Judged against that one neighbour, not a median: ruby fragments may outnumber the
    /// main columns, and a narrow dialogue column of kana beside a kanji title is longer than the title.
    /// </summary>
    private static bool IsRubyColumn(OcrLine l, List<OcrLine> columns) =>
        IsKanaOnly(l.Text)
        && columns.Any(o => o != l && o.Text.Any(c => Scripts.Of(c) == Script.Han)
                            && l.Box.Width < o.Box.Width * 0.6
                            && l.Box.Height <= o.Box.Height
                            && o.Box.Right <= l.Box.Left + 2
                            && l.Box.Left - o.Box.Right < o.Box.Width * 0.5
                            && OverlapsY(o.Box, l.Box));

    private static bool IsKanaOnly(string text) =>
        text.Any(c => Scripts.Of(c) == Script.Kana) && text.All(c => Scripts.Of(c) is Script.Kana or Script.Punctuation or Script.Space);

    private static OcrLine? NearestLine(List<OcrLine> lines, double x, double y)
    {
        OcrLine? best = null;
        var bestDist = double.MaxValue;
        foreach (var l in lines)
        {
            var d = l.Box.DistanceTo(x, y);
            // Between two columns at an equal distance the one read first (the right one) is meant.
            var tie = d == bestDist && l.Vertical && best is { Vertical: true } && l.Box.CenterX > best.Box.CenterX;
            if (d < bestDist || tie) { bestDist = d; best = l; }
        }
        if (best is null) return null;
        var tolerance = Math.Max(best.Thickness * 0.75, 12);
        return bestDist <= tolerance ? best : null;
    }

    private static int NearestUnit(OcrLine line, double x, double y)
    {
        var words = line.Words;
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < words.Count; i++)
        {
            var b = words[i].Box;
            var d = line.Vertical
                ? (y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0)
                : (x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0);
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
    /// panel, another window) do not break the paragraph. A list (a stack of 3+ short rows: a table, a menu, a word list)
    /// is not a paragraph: each row stands alone, and prose above or below it does not take them.
    /// </summary>
    /// <param name="used">Lines already in another paragraph (a page cut into paragraphs): never taken twice.</param>
    /// <param name="listCache">Verdicts from <see cref="NewListCache"/>, kept across the calls of one pass over the same lines.</param>
    internal static List<OcrLine> ParagraphOf(List<OcrLine> lines, OcrLine anchor, IReadOnlySet<OcrLine>? used = null,
        Dictionary<OcrLine, bool>? listCache = null)
    {
        // Columns of vertical text have their own rule (and no list rule: a list is a stack of rows).
        if (anchor.Vertical) return ColumnsOf(lines, anchor, used);

        listCache ??= NewListCache();
        // A row of a list is a block of its own.
        if (InList(lines, anchor, listCache)) return [anchor];

        var result = new List<OcrLine> { anchor };
        for (var current = anchor; Neighbour(lines, current, above: true, result, used) is { } up && SameParagraph(up, current) && !InList(lines, up, listCache); current = up)
            result.Insert(0, up);
        for (var current = anchor; Neighbour(lines, current, above: false, result, used) is { } down && SameParagraph(current, down) && !InList(lines, down, listCache); current = down)
            result.Add(down);
        return result;
    }

    /// <summary>An empty cache of list verdicts (by line, not by value) for the calls of <see cref="ParagraphOf"/> over one page.</summary>
    internal static Dictionary<OcrLine, bool> NewListCache() => new(ReferenceEqualityComparer.Instance);

    private static readonly char[] SentenceEnders = ['.', '!', '?', '\u2026'];
    private static readonly char[] LineEnders = ['.', '!', '?', '\u2026', ',', ';', ':'];
    private static readonly char[] Closers = ['"', '\'', ')', ']', '\u201D', '\u2019', '\u00BB'];
    private static readonly char[] Dashes = ['-', '\u2010', '\u2013', '\u2014'];

    /// <summary>The last character that says something about the line's end: quotes and brackets after a full stop do not count.</summary>
    private static char EndOf(OcrLine line)
    {
        var text = line.Text.TrimEnd();
        var e = text.Length - 1;
        while (e > 0 && Array.IndexOf(Closers, text[e]) >= 0) e--;
        return e >= 0 ? text[e] : ' ';
    }

    private static bool EndsSentence(OcrLine line) => Array.IndexOf(SentenceEnders, EndOf(line)) >= 0;

    private static int WordCount(OcrLine line) => line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// A line that could be a row of a list: not CJK, one or two words, and nothing at its end that carries on or closes a
    /// sentence (punctuation, or a hyphen or dash: the word continues on the next line). Punctuation inside a row ("Mr.
    /// Smith", "1,200") is part of the row.
    /// </summary>
    private static bool IsListRow(OcrLine line)
    {
        var text = line.Text.TrimEnd();
        if (line.Vertical || text.Length == 0 || Scripts.ContainsCjk(text)) return false;
        return WordCount(line) <= 2 && Array.IndexOf(Dashes, text[^1]) < 0 && Array.IndexOf(LineEnders, EndOf(line)) < 0;
    }

    /// <summary>
    /// Whether the line is a row of a list: it sits in a stack of 3+ list rows that is not a wrapped paragraph. A stack of
    /// single words is a list. Rows of two words are one when the stack is ragged: the lines of a wrapped paragraph fill
    /// the width before every break, the rows of a list are as long as their words. The stack is judged as a whole (every
    /// line, taken or not) and the verdict is kept for all its rows, so a list is cut into rows by all of them alike.
    /// </summary>
    private static bool InList(List<OcrLine> lines, OcrLine line, Dictionary<OcrLine, bool> cache)
    {
        if (!IsListRow(line)) return false;
        if (cache.TryGetValue(line, out var known)) return known;

        var run = new List<OcrLine> { line };
        for (var cur = line; Neighbour(lines, cur, above: true, run, null) is { } up && IsListRow(up) && SameParagraph(up, cur); cur = up)
            run.Insert(0, up);
        for (var cur = line; Neighbour(lines, cur, above: false, run, null) is { } down && IsListRow(down) && SameParagraph(cur, down); cur = down)
            run.Add(down);

        var verdict = run.Count >= 3 && !FlowsIntoProse(lines, run) && (run.All(r => WordCount(r) == 1) || IsRagged(run));
        foreach (var r in run) cache[r] = verdict;
        return verdict;
    }

    /// <summary>
    /// Most of the stack, but its last row, is far shorter than the longest row, and at least two are (the last row of a
    /// paragraph is short anyway, and one row decides nothing).
    /// </summary>
    private static bool IsRagged(List<OcrLine> run)
    {
        var widest = run.Max(l => l.Box.Width);
        var ragged = run.Take(run.Count - 1).Count(l => l.Box.Width < widest * ListFill);
        return ragged >= 2 && ragged * 2 >= run.Count - 1;
    }

    /// <summary>A line of a wrapped paragraph is at least this share of the widest line before it breaks.</summary>
    private const double ListFill = 0.75;

    /// <summary>
    /// A short stack that is a narrow bubble, not a list: its last line is the end of a sentence that only the full stop
    /// kept out of the stack ("I am / not going / to the / market."), or the sentence comes into it from a full line above
    /// that ends nothing and goes on in lowercase.
    /// </summary>
    private static bool FlowsIntoProse(List<OcrLine> lines, List<OcrLine> run)
    {
        if (Neighbour(lines, run[^1], above: false, run, null) is { } below && SameParagraph(run[^1], below)
            && EndsSentence(below) && WordCount(below) <= 2 && !Scripts.ContainsCjk(below.Text))
            return true;

        if (Neighbour(lines, run[0], above: true, run, null) is { } above && SameParagraph(above, run[0])
            && EndOf(above) != ':' && !EndsSentence(above) && char.IsLower(run[0].Text.TrimStart()[0]))
            return above.Box.Width >= run.Max(l => l.Box.Width) * ListFill;
        return false;
    }

    /// <summary>The nearest line above (or below) <paramref name="line"/> in its column, not yet taken.</summary>
    private static OcrLine? Neighbour(List<OcrLine> lines, OcrLine line, bool above, List<OcrLine> taken, IReadOnlySet<OcrLine>? used)
    {
        OcrLine? best = null;
        foreach (var l in lines)
        {
            if (l.Vertical != line.Vertical || !Overlaps(l.Box, line.Box) || taken.Contains(l) || used?.Contains(l) == true) continue;
            var side = l.Box.CenterY - line.Box.CenterY;
            if (above ? side >= 0 : side <= 0) continue;
            if (best is null || Math.Abs(side) < Math.Abs(best.Box.CenterY - line.Box.CenterY)) best = l;
        }
        return best;
    }

    private static bool SameParagraph(OcrLine upper, OcrLine lower)
    {
        if (upper.Vertical != lower.Vertical) return false;
        var h = Math.Max(upper.Box.Height, lower.Box.Height);
        var gap = lower.Box.Top - upper.Box.Bottom;
        // Lines of one paragraph share a font size; a speaker name or a note under the text is usually smaller or
        // bolder, and must not leak into the context. Kana and kanji are as wide as the font is big, so their size is
        // the width per character: the height of their boxes changes with the characters (おすすめ is lower than 観察力).
        // The box of a Latin line follows its letters, not only its font: 34 px without descenders, 45 with (B-40), and
        // 18 px for a short line of small letters. Lines are one size when any honest reading says so: the raw height,
        // the height taken off the letters' shapes, or (two long lines) the advance per character.
        bool sameSize;
        if (IsCjkLine(upper) && IsCjkLine(lower))
            sameSize = Ratio(upper.Box.Width / CharCount(upper), lower.Box.Width / CharCount(lower)) < SizeLimit;
        else if (Ratio(upper.Box.Height, lower.Box.Height) < SizeLimit)
            sameSize = true;
        else
            // These readings are looser than the raw height, so they join only lines that are set tight: leading of a
            // paragraph is under a quarter of a line, while a block of choices under a dialogue is ~0.35 away.
            sameSize = gap < h * TightGap
                && (Ratio(upper.Box.Height / LetterExtent(upper.Text), lower.Box.Height / LetterExtent(lower.Text)) < SizeLimit
                    || (IsLongLatin(upper) && IsLongLatin(lower) && Ratio(upper.Box.Width / CharCount(upper), lower.Box.Width / CharCount(lower)) < SizeLimit));
        return gap < h * 0.9 && gap > -h * 0.5 && sameSize && Overlaps(upper.Box, lower.Box);
    }

    private const double TightGap = 0.25;

    private const double SizeLimit = 1.2;

    private static double Ratio(double a, double b) => Math.Max(a, b) / Math.Max(1e-6, Math.Min(a, b));

    private static int CharCount(OcrLine line) => Math.Max(1, line.Text.Count(c => !char.IsWhiteSpace(c)));

    private static bool IsCjkLine(OcrLine line) => Scripts.Dominant(line.Text) is Script.Kana or Script.Han;

    /// <summary>Letters enough in a Latin line for its width per character to tell the font size.</summary>
    private const int MeanAdvanceChars = 12;

    private static bool IsLongLatin(OcrLine line) => !Scripts.ContainsCjk(line.Text) && CharCount(line) >= MeanAdvanceChars;

    // How tall a Latin line's box is, as shares of the tallest it can be (capitals or ascenders with descenders = 1):
    // x-height only 0.62, ascender or capital adds 0.18, descender adds 0.20. Calibrated on en_word_list.png (34 px with
    // ascenders only, 42-45 with a g) and the disco frames (a short line of x-height letters 18 px against 28).
    private const double XHeightShare = 0.62, AscenderShare = 0.18, DescenderShare = 0.20;

    private const string AscenderLetters = "bdfhklijБб!?\"'*";
    private const string DescenderLetters = "gjpqyдруфщцДЩЦ,;()[]{}|/@";

    /// <summary>The height a line's box has for its letters, relative to the tallest shape (1): lines of one font differ by it.</summary>
    internal static double LetterExtent(string text)
    {
        if (Scripts.ContainsCjk(text)) return 1;
        bool ascender = false, descender = false;
        foreach (var c in text)
        {
            if (char.IsUpper(c) || char.IsDigit(c) || AscenderLetters.Contains(c)) ascender = true;
            if (DescenderLetters.Contains(c)) descender = true;
        }
        return XHeightShare + (ascender ? AscenderShare : 0) + (descender ? DescenderShare : 0);
    }

    private static bool Overlaps(PixelRect a, PixelRect b) => a.Left < b.Right && b.Left < a.Right;

    /// <summary>Whether two columns run side by side: at least half of the shorter one lies within the other's height.</summary>
    private static bool OverlapsY(PixelRect a, PixelRect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * 0.5 && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// The columns of one vertical paragraph, right to left: from the anchor to the right and to the left, each next
    /// column the nearest vertical one that shares some height with it, while it is close and of the same font size.
    /// </summary>
    private static List<OcrLine> ColumnsOf(List<OcrLine> lines, OcrLine anchor, IReadOnlySet<OcrLine>? used)
    {
        var result = new List<OcrLine> { anchor };
        for (var current = anchor; NextColumn(lines, current, toRight: true, result, used) is { } right && SameColumns(right, current); current = right)
            result.Insert(0, right);
        for (var current = anchor; NextColumn(lines, current, toRight: false, result, used) is { } left && SameColumns(current, left); current = left)
            result.Add(left);
        return result;
    }

    /// <summary>The nearest vertical line right (or left) of <paramref name="line"/> that shares some of its height, not yet taken.</summary>
    private static OcrLine? NextColumn(List<OcrLine> lines, OcrLine line, bool toRight, List<OcrLine> taken, IReadOnlySet<OcrLine>? used)
    {
        OcrLine? best = null;
        foreach (var l in lines)
        {
            if (!l.Vertical || !OverlapsY(l.Box, line.Box) || taken.Contains(l) || used?.Contains(l) == true) continue;
            var side = l.Box.CenterX - line.Box.CenterX;
            if (toRight ? side <= 0 : side >= 0) continue;
            if (best is null || Math.Abs(side) < Math.Abs(best.Box.CenterX - line.Box.CenterX)) best = l;
        }
        return best;
    }

    private static bool SameColumns(OcrLine right, OcrLine left)
    {
        // The font size of a column is its pitch (height per character): the width of a column follows its characters
        // (46 to 61 px in one font, kana are narrower than kanji), and a short last column is as tall as it is long.
        var w = Math.Max(right.Box.Width, left.Box.Width);
        var gap = right.Box.Left - left.Box.Right;
        var ratio = Ratio(right.Box.Height / CharCount(right), left.Box.Height / CharCount(left));
        return gap < w * 0.9 && gap > -w * 0.5 && ratio < SizeLimit && OverlapsY(right.Box, left.Box);
    }

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
