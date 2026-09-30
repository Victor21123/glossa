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
    /// <param name="Box">The line's own box [left, top, width, height] for the <c>line</c> mode: what the eyes read at best.</param>
    public sealed record Case(string Id, string Image, double X, double Y, string? Word, string? Label, string? Lang, double[]? Box = null);

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
        var mode = args.FirstOrDefault(a => a is "flow" or "center" or "marker" or "boxes" or "sentry" or "line" or "two") ?? "flow";
        var (pw, ph) = (VisionReading.PointHalfWidth * 2, VisionReading.PointHalfHeight * 2);
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] == "--piece" && args[i + 1].Split('x') is [var w, var h])
                (pw, ph) = (int.Parse(w, CultureInfo.InvariantCulture), int.Parse(h, CultureInfo.InvariantCulture));
        // "sentry" without --vision shows only where the detector would wake the eyes.
        var eyes = OcrEval.Options.Parse(args, 2).Client();
        if (eyes is null && mode != "sentry") throw new ArgumentException("--vision <api root> is needed");
        var vision = eyes!;
        using var ocr = new OcrEngine(DataPaths.OcrModels);
        using var low = OcrEval.Engine();

        int labels = 0, wordsRight = 0, empties = 0, emptyRight = 0, asked = 0;
        long ms = 0;
        foreach (var c in cases)
        {
            using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
            // --auto: the game's language left to the app (no language forced), as a game not set up.
            var forced = c.Lang is "en" or "ja" or "zh" && !args.Contains("--auto") ? c.Lang : null;
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
                    if (args.Contains("--two"))
                    {
                        // The app's own two looks (EyesReading): the lines with boxes, cut at the point, then the line under
                        // it read again alone with its language named. GLOSSA_POINT_SCALE, GLOSSA_LINE_HEIGHT and
                        // GLOSSA_LINE_MARGIN change the measured settings.
                        var read = await EyesReading.ReadPointAsync(vision, Cropper(decoded), c.X, c.Y, page,
                            new PointWords(p => words.Normalize(p, forced), (p, x, y) => words.Hit(p, x, y, cjk)), forced, null, Options(),
                            CancellationToken.None);
                        if (read is not null)
                        {
                            answer = $"{read.Page.Lines.Count} lines" + (read.Line is { } box ? $" => {Where(box)} {read.Language ?? "-"} -> {read.Reading}" : "")
                                + string.Create(CultureInfo.InvariantCulture, $" ({read.Pass1.TotalMilliseconds:0} + {read.Pass2.TotalMilliseconds:0} ms)");
                            hit = read.Direct ? read.Hit : VisionReading.Settle(hit, null, read.Page);
                        }
                    }
                    else
                    {
                        answer = await VisionReading.ReadLinesAsync(vision, png, VisionReading.Banned(forced, page), CancellationToken.None);
                        // An answer that is not the lines' JSON is a failed reading: the recognizer's word stays.
                        if (VisionReading.LinesPage(answer, piece) is { } lines)
                        {
                            var seen = words.Normalize(lines, forced);
                            hit = VisionReading.Settle(hit, words.Hit(seen, c.X, c.Y, cjk), seen);
                        }
                    }
                }
                (label, word) = (hit?.Line, hit?.Word);
            }
            else if (mode == "line" && c.Box is [var bl, var bt, var bw, var bh])
            {
                // The line's true box cut out and enlarged: the most a reading of that line can give.
                var prompt = Environment.GetEnvironmentVariable("GLOSSA_POINT_PROMPT") is { Length: > 0 } own ? own : EyesReading.LinePrompt;
                answer = await VisionReading.ReadAsync(vision, LinePiece(decoded, new PixelRect(bl, bt, bl + bw, bt + bh)), CancellationToken.None, prompt);
                (label, word) = (VisionReading.ZoneText(answer)?.Split('\n')[0], null);
            }
            else if (mode == "sentry")
            {
                // The region recognized as usual; with nothing at the point (or a scrap), again with the thresholds from
                // GLOSSA_OCR_*: only a line found there wakes the eyes, which read just that line, cut out and enlarged.
                var region = new PixelRect(c.X - HalfWidth, c.Y - Up, c.X + HalfWidth, c.Y + Down);
                var hit = words.Hit(words.Normalize(await Recognize(ocr, decoded, region), forced), c.X, c.Y, cjk);
                (label, word) = (hit?.Line, hit?.Word);
                if (hit is null || VisionReading.Scrap(hit))
                {
                    was = hit?.Word ?? "-";
                    var line = (await Recognize(low, decoded, region)).Lines.Where(l => EyesReading.Near(l.Box, c.X, c.Y))
                        .OrderBy(l => Math.Abs(l.Box.CenterY - c.Y)).FirstOrDefault();
                    if (line is null) (label, word, answer) = (null, null, "asleep");
                    else if (eyes is null) (label, word, answer) = (line.Text, null, $"awake {Where(line.Box)} score {line.Score:0.00}");
                    else
                    {
                        asked++;
                        var prompt = Environment.GetEnvironmentVariable("GLOSSA_POINT_PROMPT") is { Length: > 0 } own ? own : EyesReading.LinePrompt;
                        var read = await VisionReading.ReadAsync(eyes, LinePiece(decoded, line.Box), CancellationToken.None, prompt);
                        (label, word) = (VisionReading.ZoneText(read)?.Split('\n')[0], null);
                        answer = $"awake {Where(line.Box)} score {line.Score:0.00} [{line.Text}] -> {read}";
                    }
                }
            }
            else
            {
                var (png, piece) = Piece(decoded, new PixelRect(c.X - pw / 2.0, c.Y - ph / 2.0, c.X + pw / 2.0, c.Y + ph / 2.0),
                    mode == "marker" ? (c.X, c.Y) : null, pad: true);
                if (mode is "boxes" or "two")
                {
                    // --schema: the lines' JSON held by the server; --ban: no "get" (set per case language here); --xy:
                    // boxes in Qwen3-VL's own order.
                    var xy = args.Contains("--xy");
                    var request = new LlmRequest([new LlmMessage("user", xy ? VisionReading.LinesPromptXy : VisionReading.LinesPrompt, png)],
                        args.Contains("--schema") ? VisionReading.LinesSchema(xy) : null, Temperature: 0, MaxTokens: 600,
                        BannedTokens: args.Contains("--ban") && c.Lang is "ja" or "zh" ? ["get"] : null);
                    var sb = new StringBuilder();
                    await foreach (var part in vision.StreamAsync(request, CancellationToken.None)) sb.Append(part);
                    answer = sb.ToString();
                    var page = VisionReading.LinesPage(answer, piece) ?? OcrPage.Empty(piece);
                    var hit = words.Hit(words.Normalize(page, forced), c.X, c.Y, cjk);
                    (label, word) = (hit?.Line, hit?.Word);
                    // "two": the line under the point cut out by the model's own box, enlarged and read again on its own.
                    if (mode == "two" && page.Lines.Where(l => EyesReading.Near(l.Box, c.X, c.Y)).OrderBy(l => Math.Abs(l.Box.CenterY - c.Y)).FirstOrDefault() is { } line)
                    {
                        asked++;
                        var prompt = Environment.GetEnvironmentVariable("GLOSSA_POINT_PROMPT") is { Length: > 0 } own ? own : EyesReading.LinePrompt;
                        var read = await VisionReading.ReadAsync(vision, LinePiece(decoded, line.Box), CancellationToken.None, prompt);
                        (label, word) = (VisionReading.ZoneText(read)?.Split('\n')[0], null);
                        answer += $" => {Where(line.Box)} -> {read}";
                    }
                    else if (mode == "two") (label, word) = (null, null);
                }
                else
                {
                    // GLOSSA_POINT_PROMPT: a model's own request instead (PaddleOCR-VL knows only "OCR:").
                    var prompt = Environment.GetEnvironmentVariable("GLOSSA_POINT_PROMPT") is { Length: > 0 } own ? own
                        : mode == "marker" ? MarkerPrompt : CenterPrompt;
                    answer = await VisionReading.ReadAsync(vision, png, CancellationToken.None, prompt);
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
            $"{(mode is "flow" or "sentry" or "two" ? $", model asked {asked}" : "")}, mean {ms / Math.Max(cases.Count, 1)} ms"));
    }

    private static string Where(PixelRect box) => string.Create(CultureInfo.InvariantCulture,
        $"[{box.Left:0},{box.Top:0} {box.Width:0}x{box.Height:0}]");

    /// <summary>
    /// The detector's line cut out as the app cuts it (<see cref="EyesReading.LineRegion"/>) and enlarged to the line
    /// height of <see cref="Options"/>; GLOSSA_LINE_DUMP: a folder the pieces are also written to, to look at.
    /// </summary>
    private static byte[] LinePiece(SKBitmap image, PixelRect box)
    {
        var o = Options();
        var png = EyesReading.Enlarge(Cropper(image)(EyesReading.LineRegion(box, o)), EyesReading.LineScale(box.Height, o.LineHeight));
        if (Environment.GetEnvironmentVariable("GLOSSA_LINE_DUMP") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, string.Create(CultureInfo.InvariantCulture, $"line_{box.Left:0}_{box.Top:0}.png")), png);
        return png;
    }

    /// <summary>The eyes' settings as the app uses them, changed for measurements by GLOSSA_POINT_SCALE, GLOSSA_LINE_HEIGHT, GLOSSA_LINE_MARGIN.</summary>
    private static EyesOptions Options()
    {
        static double? Env(string name) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var o = new EyesOptions();
        return o with
        {
            PointScale = Env("GLOSSA_POINT_SCALE") ?? o.PointScale,
            LineHeight = (int?)Env("GLOSSA_LINE_HEIGHT") ?? o.LineHeight,
            LineSideMargin = Env("GLOSSA_LINE_MARGIN") ?? o.LineSideMargin,
        };
    }

    /// <summary>Pieces of a decoded image, cut at its edges as the app cuts a captured frame.</summary>
    private static Func<PixelRect, (byte[] Bgra, int Width, int Height, int Stride, PixelRect Region)> Cropper(SKBitmap image) => r =>
    {
        var left = (int)Math.Clamp(Math.Floor(r.Left), 0, image.Width);
        var top = (int)Math.Clamp(Math.Floor(r.Top), 0, image.Height);
        var right = (int)Math.Clamp(Math.Ceiling(r.Right), 0, image.Width);
        var bottom = (int)Math.Clamp(Math.Ceiling(r.Bottom), 0, image.Height);
        if (right - left < 1 || bottom - top < 1) return ([], 0, 0, 0, new PixelRect(left, top, left, top));
        using var part = new SKBitmap();
        image.ExtractSubset(part, SKRectI.Create(left, top, right - left, bottom - top));
        using var copy = part.Copy(SKColorType.Bgra8888);
        return (copy.GetPixelSpan().ToArray(), copy.Width, copy.Height, copy.RowBytes, new PixelRect(left, top, right, bottom));
    };

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
        // GLOSSA_POINT_SCALE: the piece enlarged before it is sent (small models see a few hundred image tokens).
        var scale = double.TryParse(Environment.GetEnvironmentVariable("GLOSSA_POINT_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? k : 1;
        if (scale > 1)
        {
            using var big = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale), SKColorType.Bgra8888, SKAlphaType.Opaque),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            return (VisionReading.Png(big.GetPixelSpan().ToArray(), big.Width, big.Height, big.RowBytes), piece);
        }
        return (VisionReading.Png(bitmap.GetPixelSpan().ToArray(), bitmap.Width, bitmap.Height, bitmap.RowBytes), piece);
    }

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
