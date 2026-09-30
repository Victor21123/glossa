using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Lookup;
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

    /// <summary>
    /// A zone the recognizer found nothing in: the picture may hold no text at all, and the model must then say
    /// nothing rather than describe the picture.
    /// </summary>
    public const string ZonePrompt = Prompt + " If there is no text, output nothing.";

    /// <summary>
    /// The margin of screen sent around a zone: the model misreads a button cut tight to its frame (オプション as
    /// オフライン) and reads it with some of the screen around it; much more brings in the neighbouring buttons. About
    /// 0.6 of the zone's height (measured on a title menu, 2026-09-30).
    /// </summary>
    public static PixelRect ZonePiece(PixelRect zone)
    {
        var pad = Math.Clamp(zone.Height * 0.6, 16, 120);
        return new PixelRect(zone.Left - pad, zone.Top - pad, zone.Right + pad, zone.Bottom + pad);
    }

    /// <summary>
    /// A small zone (a menu item, a button: up to two lines) is read whole by the model when the recognizer's words are
    /// mostly doubtful, or when all it found is a scrap - at most three letters over less than half the zone's width:
    /// the pieces it gets of a stylized font are rubbish (回想モード read as 想书 over a third of the button: two real
    /// hanzi, so no doubtful word). A real line in a loosely drawn zone stays the recognizer's: the model reads the zone
    /// with a margin and would bring in the lines around it (measured on 24 zones, 2026-09-30).
    /// </summary>
    public static bool ReadWholeZone(OcrPage page, int doubtfulWords, PixelRect zone)
    {
        if (page.Lines.Count > 2) return false;
        var withLetters = page.Lines.SelectMany(l => l.Words).Count(w => w.Text.Any(char.IsLetter));
        if (withLetters == 0 || doubtfulWords * 2 >= withLetters) return true;
        var letters = page.Lines.Sum(l => l.Text.Count(char.IsLetter));
        return letters <= 3 && page.Lines.Max(l => l.Box.Width) < zone.Width / 2;
    }

    private static readonly HashSet<string> NoTextAnswers = new(StringComparer.OrdinalIgnoreCase)
    {
        "no text", "nothing", "none", "n/a", "empty", "no text found", "there is no text", "there is no text in this image",
        "the image contains no text", "no visible text",
    };

    /// <summary>
    /// The model's reading of a zone as its lines, or null when it read nothing: it answers "no text" or "nothing" on an
    /// empty picture although asked to say nothing.
    /// </summary>
    public static string? ZoneText(string reading)
    {
        var lines = reading.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !NoTextAnswers.Contains(l.Trim('.', '(', ')', '*', '"', ' ')))
            .ToList();
        var text = string.Join("\n", lines);
        return text.Any(char.IsLetter) ? text : null;
    }

    /// <summary>
    /// A point where the recognizer found no text, or only a scrap of it (<see cref="Scrap"/>): the model reads the piece
    /// of screen around the point as lines with their boxes, and its lines stand in for the recognizer's there. Measured
    /// 2026-09-30 on 13 points (neon menu buttons, Persona 5 Royal menus, 5 points on no text): asked for lines with boxes
    /// the model read 8 of 8 labels and nothing on 5 of 5 empty points, ~4.2 s with sight on the processor; asked for the
    /// text at the middle of the piece 7 of 8 and 3 of 5 (twice it answered with the prompt), with a ring drawn at the
    /// point 3 of 8.
    /// </summary>
    public const string LinesPrompt = "Find every line of text in the picture. Answer with JSON only: a list of objects " +
        "{\"text\": the line exactly as written, \"box_2d\": [ymin, xmin, ymax, xmax]} with coordinates from 0 to 1000 " +
        "relative to the picture. If there is no text, answer [].";

    /// <summary>
    /// <see cref="LinesPrompt"/> in Qwen3-VL's own grounding: "bbox_2d" is [x1, y1, x2, y2]. Asked for Gemma's box_2d it
    /// kept its own order anyway, and every line landed turned over (2026-09-30).
    /// </summary>
    public const string LinesPromptXy = "Find every line of text in the picture. Answer with JSON only: a list of objects " +
        "{\"text\": the line exactly as written, \"bbox_2d\": [x1, y1, x2, y2]} with coordinates from 0 to 1000 " +
        "relative to the picture. If there is no text, answer [].";

    /// <summary>The lines' JSON the server holds the answer to: no code fence, no prompt said back, at most 12 lines.</summary>
    public static JsonObject LinesSchema(bool xy = false) => JsonNode.Parse("""
        {"type": "array", "maxItems": 12, "items": {"type": "object", "required": ["text", "BOX"], "properties": {
          "text": {"type": "string"},
          "BOX": {"type": "array", "minItems": 4, "maxItems": 4, "items": {"type": "integer"}}}}}
        """.Replace("BOX", BoxKey(xy)))!.AsObject();

    private static string BoxKey(bool xy) => xy ? "bbox_2d" : "box_2d";

    /// <summary>
    /// Half the piece of screen read around a point: 600x180 read 8 of 8 labels; 900x220 (smaller letters at the same
    /// 280 image tokens) 6, 400x140 (a long label cut off) 7, 500x160 7 (2026-09-30).
    /// </summary>
    public const int PointHalfWidth = 300, PointHalfHeight = 90;

    public static PixelRect PointPiece(double x, double y) =>
        new(x - PointHalfWidth, y - PointHalfHeight, x + PointHalfWidth, y + PointHalfHeight);

    /// <summary>Below this a word alone in a line of up to three letters counts as a <see cref="Scrap"/>.</summary>
    public const float ScrapScore = 0.9f;

    /// <summary>
    /// A scrap of a stylized label rather than a word: a line of at most three letters the recognizer is not sure of
    /// (回想モード in neon outlines read as 回想毛, オート as オー卜). On 911 words of ordinary frames (2026-09-30) 12 were such:
    /// the model read 3 of them right, found no text on 6 (an icon, a microphone, armour read as 西, x, AA) and one in a
    /// dense paragraph it placed a line off (<see cref="Settle"/> keeps the recognizer's word there).
    /// </summary>
    public static bool Scrap(WordHit hit) => hit.Score < ScrapScore && hit.Line.Count(char.IsLetter) <= 3;

    /// <summary>
    /// Tokens the model must not write in its reading: Gemma 4 26B wrote ニューゲーム as "ニューget" and "get-game" - the same
    /// "get" it glues into Russian words. Not banned where the text is English: set so, or Latin with no CJK on the page.
    /// </summary>
    public static IReadOnlyList<string>? Banned(string? forced, OcrPage page)
    {
        if (forced == "en") return null;
        if (forced is null)
        {
            var text = string.Concat(page.Lines.Select(l => l.Text));
            if (!Scripts.ContainsCjk(text) && text.Any(c => Scripts.Of(c) == Script.Latin)) return null;
        }
        return ["get"];
    }

    /// <summary>The model's lines with boxes for the picture (<see cref="LinesPrompt"/>), as it answered.</summary>
    public static async Task<string> ReadLinesAsync(ILlmClient model, byte[] png, IReadOnlyList<string>? banned, CancellationToken ct)
    {
        var request = new LlmRequest([new LlmMessage("user", LinesPrompt, png)], LinesSchema(), Temperature: 0, MaxTokens: 600,
            BannedTokens: banned);
        var sb = new StringBuilder();
        await foreach (var part in model.StreamAsync(request, ct).ConfigureAwait(false)) sb.Append(part);
        return sb.ToString();
    }

    /// <summary>
    /// The model's lines (<see cref="LinesPrompt"/>) as the recognizer's page for the <paramref name="piece"/> of screen
    /// the model saw: each line's box from its box_2d, [ymin, xmin, ymax, xmax], or bbox_2d, [x1, y1, x2, y2] (both 0-1000
    /// of the piece, <see cref="LinesPromptXy"/>), words cut from the box by their letters (each
    /// kana and kanji its own unit, as the recognizer gives them; a Latin word's box is as wide as its share of letters, a
    /// little off in a proportional font); a sign read as lines of one item is cut into rows. An answer cut off by the
    /// token limit keeps its whole lines; null when the answer is not the lines' JSON at all (a failed reading, not "no
    /// text").
    /// </summary>
    public static OcrPage? LinesPage(string answer, PixelRect piece)
    {
        var start = answer.IndexOf('[');
        if (start < 0 || (Parse(answer[start..]) ?? Salvage(answer[start..])) is not JsonArray items) return null;
        var lines = new List<OcrLine>();
        foreach (var item in items)
        {
            if (item is not JsonObject o) continue;
            var xy = o["box_2d"] is null;
            if (o[BoxKey(xy)] is not JsonArray { Count: 4 } b) continue;
            string? text;
            double[] v;
            try
            {
                text = (string?)o["text"];
                v = b.Select(n => Math.Clamp((double?)n ?? 0, 0, 1000)).ToArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                continue; // a number where the text should be, or the other way round
            }
            if (xy) v = [v[1], v[0], v[3], v[2]];
            double top = Math.Min(v[0], v[2]), bottom = Math.Max(v[0], v[2]), left = Math.Min(v[1], v[3]), right = Math.Max(v[1], v[3]);
            var box = new PixelRect(piece.Left + left / 1000 * piece.Width, piece.Top + top / 1000 * piece.Height,
                piece.Left + right / 1000 * piece.Width, piece.Top + bottom / 1000 * piece.Height);
            var rows = (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length == 0 || box.Width < 1 || box.Height < 1) continue;
            var rowHeight = box.Height / rows.Length;
            for (var k = 0; k < rows.Length; k++)
                lines.Add(Line(rows[k], box with { Top = box.Top + k * rowHeight, Bottom = box.Top + (k + 1) * rowHeight }));
        }
        return new OcrPage(lines, piece, TimeSpan.Zero);
    }

    /// <summary>The JSON up to its last closing bracket, or null (text after it - a stray fence - is left out).</summary>
    private static JsonNode? Parse(string json)
    {
        var end = json.LastIndexOf(']');
        if (end < 0) return null;
        try
        {
            return JsonNode.Parse(json[..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The whole lines of an answer cut off mid-line: up to the last complete object, the list closed after it.</summary>
    private static JsonNode? Salvage(string json)
    {
        for (var end = json.LastIndexOf('}'); end > 0; end = json.LastIndexOf('}', end - 1))
        {
            try
            {
                return JsonNode.Parse(json[..(end + 1)] + "]");
            }
            catch (JsonException)
            {
                // a brace inside a line's text, or an object not yet complete: try the one before
            }
        }
        return null;
    }

    private static OcrLine Line(string text, PixelRect box)
    {
        var words = new List<OcrWord>();
        var step = box.Width / text.Length;
        for (var at = 0; at < text.Length;)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
                continue;
            }
            var length = Scripts.IsCjk(text[at]) ? 1
                : text.Skip(at).TakeWhile(c => !char.IsWhiteSpace(c) && !Scripts.IsCjk(c)).Count();
            words.Add(new OcrWord(text.Substring(at, length), box with { Left = box.Left + at * step, Right = box.Left + (at + length) * step }, 1f));
            at += length;
        }
        return new OcrLine(text, box, words, 1f);
    }

    /// <summary>
    /// The word at the point once the model has read the piece around it (<paramref name="seen"/>, its word there, from
    /// <paramref name="page"/>, its lines): with nothing recognized there, the model's word (null when it read no text
    /// either). With a scrap recognized, the model's word when it lies in the same line of screen - in a dense paragraph
    /// its boxes may sit a line off, and there the recognizer's word stays, as it does when the model read a line in that
    /// row that just misses the point; null when the model read nothing in that row at all: the scrap was an icon or a
    /// drawing (x on a close button, 西 on a microphone).
    /// </summary>
    public static WordHit? Settle(WordHit? recognized, WordHit? seen, OcrPage page)
    {
        if (recognized is null) return seen;
        if (seen is not null) return SameRow(recognized.Box, seen.Box) ? seen : recognized;
        return page.Lines.Any(l => SameRow(recognized.Box, l.Box)) ? recognized : null;
    }

    private static bool SameRow(PixelRect a, PixelRect b) => Math.Abs(a.CenterY - b.CenterY) <= Math.Max(a.Height, b.Height) / 2;

    /// <summary>What the model reads in the picture, as it answered (one screen line per line).</summary>
    public static async Task<string> ReadAsync(ILlmClient model, byte[] png, CancellationToken ct, string prompt = Prompt)
    {
        var request = new LlmRequest([new LlmMessage("user", prompt, png)], Temperature: 0, MaxTokens: 400);
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
    /// How far, in line heights, the line's box may reach past its last word read before something there counts as
    /// not read: "Special Sensation Oil I" came out as "Special Sensation Oil", its box 0.25 line heights past "Oil".
    /// </summary>
    public const double UnreadTail = 0.2;

    /// <summary>
    /// Whether the word is worth reading again: no dictionary knows it (<paramref name="known"/> false; null when
    /// there is nothing to check against), the recognizer was unsure of it, or it is the last word read in a line whose
    /// box goes on past it (on <paramref name="page"/>, when given).
    /// </summary>
    public static bool Doubtful(WordHit hit, bool? known, OcrPage? page = null) =>
        known == false || hit.Score < MinScore || (page is not null && UnreadAfter(page, hit));

    /// <summary>
    /// The page's words worth reading again, each once: the lookup's word under every piece the recognizer read (only
    /// words with letters), planned against the dictionaries without fetching their articles.
    /// </summary>
    public static List<WordHit> DoubtfulWords(OcrPage page, WordLookup words, string cjk, string nativeLanguage, string? forced)
    {
        var noArticles = new DictionarySettings { ShowInPopup = false, HintAi = false };
        var seen = new HashSet<(string, double, double)>();
        var doubtful = new List<WordHit>();
        foreach (var unit in page.Lines.SelectMany(l => l.Words))
        {
            if (words.Hit(page, unit.Box.CenterX, unit.Box.CenterY, cjk) is not { } hit
                || !seen.Add((hit.Word, hit.Box.Left, hit.Box.Top)) || !hit.Word.Any(char.IsLetter)) continue;
            if (Doubtful(hit, words.Plan(hit, cjk, nativeLanguage, noArticles, forced).Known, page)) doubtful.Add(hit);
        }
        return doubtful;
    }

    /// <summary>
    /// The page with its doubtful words read again: <paramref name="read"/> gives the model's reading of the piece of
    /// screen around a word (<see cref="Region"/>), and one reading serves every doubtful word inside that piece; at most
    /// <paramref name="readings"/> pieces. Continues on the caller's context (the app shows a status while reading).
    /// </summary>
    public static async Task<OcrPage> CorrectDoubtfulAsync(OcrPage page, WordLookup words, string cjk, string nativeLanguage,
        string? forced, Func<WordHit, Task<string?>> read, int readings, CancellationToken ct)
    {
        var doubtful = DoubtfulWords(page, words, cjk, nativeLanguage, forced);
        var done = new HashSet<WordHit>();
        var count = 0;
        foreach (var first in doubtful)
        {
            if (done.Contains(first)) continue;
            if (count++ >= readings) break;
            if (await read(first) is not { } reading) continue;
            ct.ThrowIfCancellationRequested();
            var piece = Region(first.Box.CenterX, first.Box.CenterY);
            foreach (var hit in doubtful.Where(d => !done.Contains(d) && piece.Contains(d.Box.CenterX, d.Box.CenterY)).ToList())
            {
                done.Add(hit);
                // Earlier fixes may have changed the line: the word under the same point now.
                if (words.Hit(page, hit.Box.CenterX, hit.Box.CenterY, cjk) is { } now)
                    page = words.Normalize(Correct(page, now, reading), forced);
            }
        }
        return page;
    }

    /// <summary>The word is the last one read in its line, and the line's box reaches on past it.</summary>
    internal static bool UnreadAfter(OcrPage page, WordHit hit)
    {
        if (LineOf(page, hit) is not { Words.Count: > 0 } line) return false;
        var lastRight = line.Words.Max(w => w.Box.Right);
        return hit.Box.Right >= lastRight - 1 && line.Box.Right - lastRight > UnreadTail * line.Box.Height;
    }

    private static OcrLine? LineOf(OcrPage page, WordHit hit) =>
        page.Lines.FirstOrDefault(l => l.Text == hit.Line && Overlaps(l.Box, hit.Box));

    /// <summary>
    /// The page with the word of <paramref name="hit"/> spelled as in <paramref name="reading"/> (the model's answer, one
    /// screen line per line). The word's line is matched to the model's line most like it, and the word to the model's
    /// word in the same place: glued words come apart ("LOARGAME" -> "LOAD GAME"), a broken one is joined ("exper ence"
    /// -> "experience"). The rest of the page stays as recognized; with no line close enough nothing changes.
    /// </summary>
    public static OcrPage Correct(OcrPage page, WordHit hit, string reading)
    {
        if (LineOf(page, hit) is not { } line) return page;
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
        // Past the last word read the line's box goes on: the model's words there come in as far as they fit the room.
        var unread = UnreadAfter(page, hit)
            ? (int)Math.Round((line.Box.Right - line.Words.Max(w => w.Box.Right)) / Math.Max(1, line.Box.Width / Math.Max(1, line.Text.Length)))
            : 0;
        var chars = Merge(line.Text, model, best, inside[0], inside[^1] + 1, Scripts.IsCjk(hit.Script), unread);
        if (chars is null) return page;
        var text = new string(chars.Select(c => c.Char).ToArray());
        if (text == line.Text) return page;

        var lines = page.Lines.ToList();
        lines[lines.IndexOf(line)] = Rebuild(line, text, chars, boxes);
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
        {
            // Of equal ends the same stretch a letter or two longer ("Oil II" rather than its "Oil I"), but not the same
            // words again further on (the model may join two columns into one line).
            if (d[n, j] < d[n, end] || (d[n, j] == d[n, end] && j - end <= 2)) end = j;
        }

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
    /// <param name="unread">Letters' room left unread past the old line's end: when the word reaches that end, the model's
    /// following words (whole ones, a space counting as one more) are taken while they fit ("Oil" + " I").</param>
    internal static List<(char Char, int From)>? Merge(string ocr, string model, Alignment a, int from, int to, bool cjk, int unread = 0)
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
        var reachesEnd = steps.LastOrDefault(s => s.I >= 0 && s.J >= 0) is var (lastI, lastJ) && lastI == ocr.Length - 1 && lastJ < end;
        if (unread > 0 && reachesEnd)
        {
            if (cjk) end = Math.Min(model.Length, end + unread);
            else
                for (var next = end; next < model.Length;)
                {
                    var word = next;
                    while (word < model.Length && char.IsWhiteSpace(model[word])) word++;
                    var wordEnd = word;
                    while (wordEnd < model.Length && !char.IsWhiteSpace(model[wordEnd])) wordEnd++;
                    if (wordEnd == word || wordEnd - end > unread + 1) break;
                    end = next = wordEnd;
                }
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
        const int unset = int.MinValue;
        var lastOld = Math.Max(oldStart, oldEnd - 1);
        var boxOf = new int[model.Length];
        Array.Fill(boxOf, unset);
        var previous = oldStart;
        foreach (var (i, j) in steps)
        {
            if (i >= 0) previous = i;
            if (j >= 0) boxOf[j] = Math.Clamp(previous, oldStart, lastOld);
        }
        var aligned = steps.Where(s => s.J >= 0).Select(s => s.J).DefaultIfEmpty(0).Min();
        for (var j = start; j < end; j++)
            if (boxOf[j] == unset) boxOf[j] = j < aligned ? oldStart : lastOld;
        // Letters past either end of the old line (a letter the recognizer dropped there, the "I" of "Oil I") get room of
        // their own beyond it: numbered on from its length, or back from -1, spaces taking no place (see Rebuild).
        var n = ocr.Length;
        var paired = steps.Where(s => s.I >= 0 && s.J >= 0).Select(s => s.J).ToList();
        if (paired.Count > 0 && oldEnd == n)
            for (int j = Math.Max(start, paired[^1] + 1), k = 0; j < end; j++)
                boxOf[j] = n + (char.IsWhiteSpace(model[j]) ? k : k++);
        if (paired.Count > 0 && oldStart == 0)
            for (int j = Math.Min(end, paired[0]) - 1, k = 0; j >= start; j--)
                boxOf[j] = -1 - (char.IsWhiteSpace(model[j]) ? k : k++);
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

        // Letters past the old line's ends share the room between its outer letters and its box's edges (a letter's
        // width each at least half the line's average, else the average).
        var n = line.Text.Length;
        var average = line.Box.Width / Math.Max(1, n);
        var after = chars.Count(c => c.From >= n && !char.IsWhiteSpace(c.Char));
        var before = chars.Count(c => c.From < 0 && !char.IsWhiteSpace(c.Char));

        PixelRect Beyond(double edge, double limit, int k, int count, int direction)
        {
            var room = Math.Abs(limit - edge);
            var width = room >= count * average * 0.5 ? room / count : average;
            double a = edge + direction * k * width, b = edge + direction * (k + 1) * width;
            return line.Box with { Left = Math.Min(a, b), Right = Math.Max(a, b) };
        }

        PixelRect Letter(int from) =>
            boxes.Count == 0 ? line.Box
            : from >= n ? Beyond(boxes[^1].Right, line.Box.Right, from - n, after, +1)
            : from < 0 ? Beyond(boxes[0].Left, line.Box.Left, -1 - from, before, -1)
            : boxes[from];

        foreach (var (c, from) in chars)
        {
            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }
            var letter = Letter(from);
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
