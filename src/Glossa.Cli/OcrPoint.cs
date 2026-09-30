using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using SkiaSharp;

/// <summary>
/// <c>ocr-point &lt;cases.json&gt; --vision &lt;api root&gt; [flow|center|marker|boxes] [--piece WxH] [--schema] [--ban]</c>:
/// a point on stylized text the recognizer does not find (neon menu buttons, 2026-09-30) read by the model with sight from
/// the piece of screen around it. <c>flow</c> is the lookup's own way (<see cref="VisionReading.Settle"/>): the region
/// recognized first, the model asked only on no text or a scrap. The others are the ways measured to choose it: the piece
/// centred on the point, a red ring drawn at it, every line with its box. Each case is compared with the whole label and
/// the word expected there; a case without a label is a point on no text, where nothing must be found.
/// </summary>
public static class OcrPoint
{
    private const int HalfWidth = 900, Up = 260, Down = 220; // LookupController's region around the cursor

    /// <param name="Word">The word a lookup should give at the point (any of "a|b"); null on no text.</param>
    /// <param name="Label">The whole line or button text at the point (any of "a|b"); null on no text.</param>
    public sealed record Case(string Id, string Image, double X, double Y, string? Word, string? Label, string? Lang);

    private const string CenterPrompt = "The picture is centred on the mouse pointer. Transcribe exactly the one line of text " +
        "under the centre of the picture. Output only that text. If there is no text at the centre, output nothing.";

    private const string MarkerPrompt = "Transcribe exactly the one line of text marked by the red circle. Output only that " +
        "text. If the circle marks no text, output nothing.";

    public static async Task RunAsync(string casesPath, string[] args, WordLookup words)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (args.Contains("--scan"))
        {
            await ScanAsync(cases, words);
            return;
        }
        var mode = args.FirstOrDefault(a => a is "flow" or "center" or "marker" or "boxes") ?? "flow";
        var (pw, ph) = (VisionReading.PointHalfWidth * 2, VisionReading.PointHalfHeight * 2);
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] == "--piece" && args[i + 1].Split('x') is [var w, var h])
                (pw, ph) = (int.Parse(w, CultureInfo.InvariantCulture), int.Parse(h, CultureInfo.InvariantCulture));
        var vision = OcrEval.Options.Parse(args, 2).Client() ?? throw new ArgumentException("--vision <api root> is needed");
        using var ocr = new OcrEngine(DataPaths.OcrModels);

        int labels = 0, wordsRight = 0, empties = 0, emptyRight = 0, asked = 0;
        long ms = 0;
        foreach (var c in cases)
        {
            using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
            var forced = c.Lang is "en" or "ja" or "zh" ? c.Lang : null;
            var cjk = c.Lang == "zh" ? "zh" : "ja";
            var sw = Stopwatch.StartNew();
            string? label, word, answer = "", was = null;
            if (mode == "flow")
            {
                var page = words.Normalize(await Recognize(ocr, decoded, new PixelRect(c.X - HalfWidth, c.Y - Up, c.X + HalfWidth, c.Y + Down)), forced);
                var hit = words.Hit(page, c.X, c.Y, cjk);
                if (hit is null || VisionReading.Scrap(hit))
                {
                    asked++;
                    was = hit?.Word ?? "-";
                    var (png, piece) = Piece(decoded, VisionReading.PointPiece(c.X, c.Y), ring: null, pad: false);
                    answer = await VisionReading.ReadLinesAsync(vision, png, VisionReading.Banned(forced, page), CancellationToken.None);
                    // An answer that is not the lines' JSON is a failed reading: the recognizer's word stays.
                    if (VisionReading.LinesPage(answer, piece) is { } lines)
                    {
                        var seen = words.Normalize(lines, forced);
                        hit = VisionReading.Settle(hit, words.Hit(seen, c.X, c.Y, cjk), seen);
                    }
                }
                (label, word) = (hit?.Line, hit?.Word);
            }
            else
            {
                var (png, piece) = Piece(decoded, new PixelRect(c.X - pw / 2.0, c.Y - ph / 2.0, c.X + pw / 2.0, c.Y + ph / 2.0),
                    mode == "marker" ? (c.X, c.Y) : null, pad: true);
                if (mode == "boxes")
                {
                    // --schema: the lines' JSON held by the server; --ban: no "get" (set per case language here).
                    var request = new LlmRequest([new LlmMessage("user", VisionReading.LinesPrompt, png)],
                        args.Contains("--schema") ? VisionReading.LinesSchema() : null, Temperature: 0, MaxTokens: 600,
                        BannedTokens: args.Contains("--ban") && c.Lang is "ja" or "zh" ? ["get"] : null);
                    var sb = new StringBuilder();
                    await foreach (var part in vision.StreamAsync(request, CancellationToken.None)) sb.Append(part);
                    answer = sb.ToString();
                    var hit = words.Hit(words.Normalize(VisionReading.LinesPage(answer, piece) ?? OcrPage.Empty(piece), forced), c.X, c.Y, cjk);
                    (label, word) = (hit?.Line, hit?.Word);
                }
                else
                {
                    answer = await VisionReading.ReadAsync(vision, png, CancellationToken.None, mode == "marker" ? MarkerPrompt : CenterPrompt);
                    (label, word) = (VisionReading.ZoneText(answer)?.Split('\n')[0], null);
                }
            }
            sw.Stop();
            ms += sw.ElapsedMilliseconds;

            bool ok;
            if (c.Label is null)
            {
                empties++;
                ok = string.IsNullOrWhiteSpace(label);
                if (ok) emptyRight++;
            }
            else
            {
                ok = c.Label.Split('|').Any(l => Letters(label ?? "") == Letters(l));
                if (ok) labels++;
                if (word is not null && c.Word?.Split('|').Any(w => Letters(w) == Letters(word)) == true) wordsRight++;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(ok ? "ok  " : "MISS")} {c.Id,-18} {sw.ElapsedMilliseconds,5} ms  label [{label}] (want {c.Label ?? "nothing"})" +
                $"  word [{word}] (want {c.Word}){(was is null ? "" : $"  recognized [{was}]")}  model: {answer.ReplaceLineEndings("|")}"));
        }
        var text = cases.Count - empties;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"-- {mode}{(mode == "flow" ? "" : $" {pw}x{ph}")}: labels {labels}/{text}, words {wordsRight}/{text}, no text {emptyRight}/{empties}" +
            $"{(mode == "flow" ? $", model asked {asked}" : "")}, mean {ms / Math.Max(cases.Count, 1)} ms"));
    }

    /// <summary>
    /// <c>ocr-point &lt;eval_cases.json&gt; --scan</c>: on ordinary frames, recognized whole, every word a lookup could land
    /// on in a line of at most three letters (a button, a label - or a scrap of a stylized one), with its place and score.
    /// </summary>
    private static async Task ScanAsync(List<Case> cases, WordLookup words)
    {
        using var ocr = new OcrEngine(DataPaths.OcrModels);
        int total = 0, shortLines = 0, scraps = 0;
        foreach (var image in cases.Where(c => c.Image is not null).Select(c => (c.Image, c.Lang)).Distinct())
        {
            using var decoded = SKBitmap.Decode(image.Image) ?? throw new InvalidDataException("not an image: " + image.Image);
            var page = words.Normalize(await Recognize(ocr, decoded, new PixelRect(0, 0, decoded.Width, decoded.Height)), image.Lang);
            var cjk = image.Lang == "zh" ? "zh" : "ja";
            var seen = new HashSet<(string, double, double)>();
            foreach (var unit in page.Lines.SelectMany(l => l.Words))
            {
                if (words.Hit(page, unit.Box.CenterX, unit.Box.CenterY, cjk) is not { } hit
                    || !seen.Add((hit.Word, hit.Box.Left, hit.Box.Top)) || !hit.Word.Any(char.IsLetter)) continue;
                total++;
                if (hit.Line.Count(char.IsLetter) > 3) continue;
                shortLines++;
                if (VisionReading.Scrap(hit)) scraps++;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {(VisionReading.Scrap(hit) ? "scrap" : "     ")} {Path.GetFileNameWithoutExtension(image.Image),-24} at {hit.Box.CenterX:0},{hit.Box.CenterY:0} score {hit.Score:0.00}  word [{hit.Word}] line [{hit.Line}]"));
            }
        }
        Console.WriteLine($"-- {shortLines} of {total} words sit in a line of at most three letters, {scraps} of them scraps");
    }

    private static async Task<OcrPage> Recognize(OcrEngine ocr, SKBitmap image, PixelRect region)
    {
        var left = (int)Math.Clamp(region.Left, 0, image.Width - 1);
        var top = (int)Math.Clamp(region.Top, 0, image.Height - 1);
        var rect = SKRectI.Create(left, top, Math.Max(1, (int)Math.Min(region.Right, image.Width) - left),
            Math.Max(1, (int)Math.Min(region.Bottom, image.Height) - top));
        using var part = new SKBitmap();
        image.ExtractSubset(part, rect);
        using var crop = part.Copy(SKColorType.Bgra8888);
        return await ocr.RecognizeAsync(crop.GetPixelSpan().ToArray(), crop.Width, crop.Height, crop.RowBytes,
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom), OcrModelFamily.CjkLatin, CancellationToken.None);
    }

    /// <summary>
    /// The piece of the image as PNG and where it lies: cut at the image's edges as the app cuts a frame, or with
    /// <paramref name="pad"/> filled out in black so the point stays in the middle; with <paramref name="ring"/> a red ring
    /// drawn around that point.
    /// </summary>
    private static (byte[] Png, PixelRect Piece) Piece(SKBitmap image, PixelRect piece, (double X, double Y)? ring, bool pad)
    {
        if (!pad)
            piece = new PixelRect(Math.Max(0, piece.Left), Math.Max(0, piece.Top), Math.Min(image.Width, piece.Right), Math.Min(image.Height, piece.Bottom));
        var info = new SKImageInfo((int)piece.Width, (int)piece.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(image, (float)-piece.Left, (float)-piece.Top);
        if (ring is var (x, y))
        {
            using var paint = new SKPaint { Color = SKColors.Red, IsStroke = true, StrokeWidth = 3, IsAntialias = true };
            canvas.DrawCircle((float)(x - piece.Left), (float)(y - piece.Top), 14, paint);
        }
        using var shot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(shot);
        return (VisionReading.Png(bitmap.GetPixelSpan().ToArray(), bitmap.Width, bitmap.Height, bitmap.RowBytes), piece);
    }

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
