using System.Runtime.InteropServices;
using System.Text;
using Glossa.Core.Llm;
using Glossa.Core.Text;
using SkiaSharp;

namespace Glossa.Core.Ocr;

/// <summary>
/// The second reading of a doubtful word: the piece of screen around the point is read again by the local model with
/// sight (Gemma 4 with its vision file), and the word under the point takes the model's spelling while the
/// recognizer's boxes stay. Measured 2026-09-29 on Persona 5 frames where the game's own cursor covers the word: the
/// recognizer got the word 4 of 7 times, with the second reading 7 of 7, about 1 s per reading at 280 image tokens.
/// Only that word changes: laid over whole lines, the model's reading also broke words the recognizer had right
/// (28 of 32 lines against 31).
/// </summary>
public static class VisionReading
{
    public const string Prompt = "Transcribe all the text in this image exactly as written, line by line. Output only the text.";

    /// <summary>The piece of screen the model reads, around the point (the measured 900x220).</summary>
    public const int HalfWidth = 450, Up = 120, Down = 100;

    /// <summary>
    /// Below this the recognizer's own confidence in the word asks for a second reading even when the word is in a
    /// dictionary (a real word read for another, 卜 for ト). The dictionary is the main sign: misread words scored up
    /// to 1.0 on the hard frames. On 911 words of ordinary frames (2026-09-29) 0.9 flagged 62 known words, 0.7 about 20.
    /// </summary>
    public const float MinScore = 0.7f;

    public static PixelRect Region(double x, double y) => new(x - HalfWidth, y - Up, x + HalfWidth, y + Down);

    /// <summary>What the model reads in the picture, as it answered (one screen line per line).</summary>
    public static async Task<string> ReadAsync(ILlmClient model, byte[] png, CancellationToken ct)
    {
        var request = new LlmRequest([new LlmMessage("user", Prompt, png)], Temperature: 0, MaxTokens: 400);
        var sb = new StringBuilder();
        await foreach (var part in model.StreamAsync(request, ct).ConfigureAwait(false)) sb.Append(part);
        return sb.ToString();
    }

    /// <summary>A BGRA piece of screen as PNG, the format the model is sent (lossless: thin strokes decide letters).</summary>
    /// <exception cref="InvalidOperationException">An empty piece, or Skia could not encode it.</exception>
    public static byte[] Png(byte[] bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"empty piece of screen {width}x{height}");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), stride))
                throw new InvalidOperationException("the piece of screen could not be read");
            using var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("the piece of screen could not be read");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Whether the word is worth reading again: no dictionary knows it (<paramref name="known"/> false; null when
    /// there is nothing to check against), or the recognizer was unsure of it.
    /// </summary>
    public static bool Doubtful(WordHit hit, bool? known) => known == false || hit.Score < MinScore;

    /// <summary>
    /// The page with the word of <paramref name="hit"/> spelled as in <paramref name="reading"/> (the model's answer, one
    /// screen line per line). The word's line is matched to the model's line most like it, and the word to the model's
    /// word in the same place: glued words come apart ("LOARGAME" -> "LOAD GAME"), a broken one is joined ("exper ence"
    /// -> "experience"). The rest of the page stays as recognized; with no line close enough nothing changes.
    /// </summary>
    public static OcrPage Correct(OcrPage page, WordHit hit, string reading)
    {
        var index = -1;
        for (var n = 0; n < page.Lines.Count && index < 0; n++)
            if (page.Lines[n].Text == hit.Line && Overlaps(page.Lines[n].Box, hit.Box)) index = n;
        if (index < 0) return page;
        var line = page.Lines[index];
        var boxes = LetterBoxes(line);
        // The word's letters: those whose box middle lies inside the hit's box.
        var inside = Enumerable.Range(0, boxes.Count)
            .Where(k => !char.IsWhiteSpace(line.Text[k]) && boxes[k].CenterX >= hit.Box.Left && boxes[k].CenterX <= hit.Box.Right)
            .ToList();
        if (inside.Count == 0) return page;

        // Of equally close lines the one sharing more letters wins: "OilII" is one edit from both "Oil I" and "Oil II".
        Alignment? best = null;
        string? model = null;
        foreach (var candidate in Lines(reading))
        {
            var a = Align(line.Text, candidate);
            if (a.Accepted && (best is null || a.Cost < best.Cost || (a.Cost == best.Cost && a.Matches > best.Matches)))
                (best, model) = (a, candidate);
        }
        if (best is null || model is null) return page;
        var chars = Merge(line.Text, model, best, inside[0], inside[^1] + 1, Scripts.IsCjk(hit.Script));
        if (chars is null) return page;
        var text = new string(chars.Select(c => c.Char).ToArray());
        if (text == line.Text) return page;

        var lines = page.Lines.ToList();
        lines[index] = Rebuild(line, text, chars, boxes);
        return page with { Lines = lines };
    }

    /// <summary>The model's lines without code fences and blank lines.</summary>
    internal static List<string> Lines(string reading) =>
        reading.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .ToList();

    private static bool Overlaps(PixelRect a, PixelRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <param name="Steps">Pairs of an old letter and a new one in order; -1 where a side has none.</param>
    /// <param name="Matches">Letters the same in both, case included.</param>
    internal sealed record Alignment(IReadOnlyList<(int I, int J)> Steps, int Cost, int Matches, bool Accepted);

    /// <summary>
    /// Edit alignment of the recognizer's line (whole) against a stretch of the model's line (free start and end: the
    /// model's line may hold more, or the recognizer's line run out of the piece the model saw). Letters are compared
    /// ignoring case. Accepted when at least half of the stretch are the same letters in the same places, and at least
    /// two: a line of one letter or kana is too little to tell the right line from a chance match, so it stays as read.
    /// </summary>
    internal static Alignment Align(string ocr, string model)
    {
        int n = ocr.Length, m = model.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++) d[i, 0] = i;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (Same(ocr[i - 1], model[j - 1]) ? 0 : 1));
        var end = 0;
        for (var j = 1; j <= m; j++)
            if (d[n, j] <= d[n, end]) end = j; // of equal ends the longer: "Oil II" rather than its "Oil I"

        var steps = new List<(int I, int J)>();
        int a = n, b = end, matches = 0, exact = 0;
        while (a > 0 && b > 0)
        {
            var same = Same(ocr[a - 1], model[b - 1]);
            if (d[a, b] == d[a - 1, b - 1] + (same ? 0 : 1))
            {
                if (same) matches++;
                if (ocr[a - 1] == model[b - 1]) exact++;
                steps.Add((--a, --b));
            }
            else if (d[a, b] == d[a - 1, b] + 1) steps.Add((--a, -1));
            else steps.Add((-1, --b));
        }
        while (a > 0) steps.Add((--a, -1));
        steps.Reverse();

        var first = steps.FindIndex(s => s.J >= 0);
        var span = first < 0 ? 0 : steps.FindLastIndex(s => s.J >= 0) - first + 1;
        return new Alignment(steps, d[n, end], exact, matches >= 2 && matches * 2 >= span);
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>
    /// The old line with its letters [<paramref name="from"/>, <paramref name="to"/>) replaced by the model's word in
    /// their place: the model's letters aligned to them, with letters the model adds at either edge, widened to whole
    /// words (between spaces) outside Chinese and Japanese. Every letter comes with the old letter whose box it takes.
    /// </summary>
    internal static List<(char Char, int From)>? Merge(string ocr, string model, Alignment a, int from, int to, bool cjk)
    {
        var steps = a.Steps;
        var firstStep = -1;
        var lastStep = -1;
        for (var k = 0; k < steps.Count; k++)
        {
            if (steps[k].I < from || steps[k].I >= to) continue;
            if (firstStep < 0) firstStep = k;
            lastStep = k;
        }
        if (firstStep < 0) return null;
        // Letters only the model has, right before or after the word, belong to it ("optio" + "n").
        while (firstStep > 0 && steps[firstStep - 1].I < 0) firstStep--;
        while (lastStep < steps.Count - 1 && steps[lastStep + 1].I < 0) lastStep++;
        var js = steps.Skip(firstStep).Take(lastStep - firstStep + 1).Where(s => s.J >= 0).Select(s => s.J).ToList();
        if (js.Count == 0) return null;
        int start = js.Min(), end = js.Max() + 1;
        if (!cjk)
        {
            while (start > 0 && !char.IsWhiteSpace(model[start - 1])) start--;
            while (end < model.Length && !char.IsWhiteSpace(model[end])) end++;
        }
        // The old letters that stretch of the model's line stands for, and the word's own.
        int oldStart = from, oldEnd = to;
        foreach (var (i, j) in steps)
        {
            if (i < 0 || j < start || j >= end) continue;
            oldStart = Math.Min(oldStart, i);
            oldEnd = Math.Max(oldEnd, i + 1);
        }

        // Letters of the stretch outside the alignment (the model's line runs on past the old one) take the edge boxes.
        var lastOld = Math.Max(oldStart, oldEnd - 1);
        var boxOf = new int[model.Length];
        Array.Fill(boxOf, -1);
        var previous = oldStart;
        foreach (var (i, j) in steps)
        {
            if (i >= 0) previous = i;
            if (j >= 0) boxOf[j] = Math.Clamp(previous, oldStart, lastOld);
        }
        var aligned = steps.Where(s => s.J >= 0).Select(s => s.J).DefaultIfEmpty(0).Min();
        for (var j = start; j < end; j++)
            if (boxOf[j] < 0) boxOf[j] = j < aligned ? oldStart : lastOld;
        var chars = new List<(char, int)>(ocr.Length + end - start);
        for (var i = 0; i < oldStart; i++) chars.Add((ocr[i], i));
        for (var j = start; j < end; j++) chars.Add((model[j], boxOf[j]));
        for (var i = oldEnd; i < ocr.Length; i++) chars.Add((ocr[i], i));
        return chars;
    }

    /// <summary>
    /// The line with the new text and word boxes made from the old letters' boxes. Words are split as the recognizer
    /// splits them: runs of letters between spaces, and each kana or kanji on its own.
    /// </summary>
    private static OcrLine Rebuild(OcrLine line, string text, List<(char Char, int From)> chars, List<PixelRect> boxes)
    {
        var words = new List<OcrWord>();
        var sb = new StringBuilder();
        PixelRect? box = null;

        void Flush()
        {
            if (sb.Length > 0 && box is { } b) words.Add(new OcrWord(sb.ToString(), b, 1f));
            sb.Clear();
            box = null;
        }

        foreach (var (c, from) in chars)
        {
            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }
            var letter = boxes.Count == 0 ? line.Box : boxes[Math.Clamp(from, 0, boxes.Count - 1)];
            var cjk = Scripts.IsCjk(c);
            if (cjk) Flush();
            sb.Append(c);
            box = box is { } b ? b.Union(letter) : letter;
            if (cjk) Flush();
        }
        Flush();
        return line with { Text = text, Words = words };
    }

    /// <summary>
    /// A box for each letter of the line: the word's box cut into equal parts, a space between words gets the gap.
    /// When the words do not add up to the line the whole line is cut evenly.
    /// </summary>
    internal static List<PixelRect> LetterBoxes(OcrLine line)
    {
        var text = line.Text;
        var boxes = new PixelRect?[text.Length];
        var at = 0;
        foreach (var w in line.Words)
        {
            var found = text.IndexOf(w.Text, at, StringComparison.Ordinal);
            if (found < 0) return Even(line);
            var width = w.Box.Width / Math.Max(w.Text.Length, 1);
            for (var k = 0; k < w.Text.Length; k++)
                boxes[found + k] = w.Box with { Left = w.Box.Left + width * k, Right = w.Box.Left + width * (k + 1) };
            at = found + w.Text.Length;
        }
        var result = new List<PixelRect>(text.Length);
        for (var k = 0; k < text.Length; k++)
        {
            if (boxes[k] is { } b)
            {
                result.Add(b);
                continue;
            }
            var left = k > 0 ? result[k - 1].Right : line.Box.Left;
            var right = boxes.Skip(k + 1).FirstOrDefault(x => x is not null)?.Left ?? line.Box.Right;
            result.Add(line.Box with { Left = left, Right = Math.Max(left, right) });
        }
        return result;
    }

    private static List<PixelRect> Even(OcrLine line)
    {
        var width = line.Box.Width / Math.Max(line.Text.Length, 1);
        return Enumerable.Range(0, line.Text.Length)
            .Select(k => line.Box with { Left = line.Box.Left + width * k, Right = line.Box.Left + width * (k + 1) })
            .ToList();
    }
}
