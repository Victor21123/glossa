using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glossa.Core.Llm;
using Glossa.Core.Text;
using SkiaSharp;

namespace Glossa.Core.Ocr;

/// <summary>How text the eyes read is cut into words: the app's WordLookup (normalized, CJK split by its dictionaries), a plain HitTester in tests.</summary>
public sealed record PointWords(Func<OcrPage, OcrPage> Normalize, Func<OcrPage, double, double, WordHit?> Hit);

/// <summary>The two looks as measured best on 2026-09-30 (<c>docs/research/ocr.md</c>); the CLI changes them for measurements.</summary>
/// <param name="PointScale">The first look's piece enlarged this many times (1.5 and 1 read the tester's neon 1 of 3).</param>
/// <param name="LineHeight">The second look's line enlarged to this height in pixels.</param>
/// <param name="LineSideMargin">Margins beside the line, in its heights: 0.6 cut the last letter when the first look's box
/// came a few pixels short (オプション read as "オブジェクト").</param>
/// <param name="LineVerticalMargin">Margins above and below the line, in its heights.</param>
public sealed record EyesOptions(double PointScale = 2, int LineHeight = 96, double LineSideMargin = 1.2,
    double LineVerticalMargin = 0.35, int MaxTokens = 600);

/// <summary>
/// What the eyes read at a point. <paramref name="Direct"/>: the line under the point was read again on its own and
/// <paramref name="Hit"/> is its word there, to be taken as it is; otherwise <paramref name="Hit"/> is null and
/// <paramref name="Page"/> holds the first look's lines (no line at the point - no text; a line the second look could not
/// read - for the caller to settle against the recognizer's word).
/// </summary>
public sealed record EyesResult(OcrPage Page, WordHit? Hit, bool Direct, string? Language, PixelRect? Line, string? Reading,
    TimeSpan Pass1, TimeSpan Pass2);

/// <summary>
/// The eyes' two looks at a lookup's point, for the models whose own sight misreads stylized text. First the piece
/// around the point, enlarged, as lines with boxes (Qwen's bbox_2d) - cut short as soon as the line under the point, or
/// one wholly below it, has come: the rest costs generation time and is never read. Then that line alone, cut out with
/// margins, enlarged and read again with its language named. Measured on 2026-09-30: the tester's neon 3 of 3, nothing
/// read on 40 of 40 empty points.
/// </summary>
public static class EyesReading
{
    public const string LinePrompt = "Transcribe exactly the text in this picture. Output only that text. " +
        "If there is no text, output nothing.";

    /// <summary>
    /// Reads the text at (<paramref name="x"/>, <paramref name="y"/>) of the frame <paramref name="crop"/> cuts pieces
    /// from; null when the point is off the frame or the first look is not the lines' JSON (a failed reading, not "no
    /// text"). A newer lookup's cancellation passes through; the early stop of the first look does not.
    /// </summary>
    public static async Task<EyesResult?> ReadPointAsync(ILlmClient eyes,
        Func<PixelRect, (byte[] Bgra, int Width, int Height, int Stride, PixelRect Region)> crop, double x, double y,
        OcrPage recognized, PointWords words, string? forced, ReadingMemory? memory, EyesOptions? options, CancellationToken ct)
    {
        var o = options ?? new EyesOptions();
        var piece = crop(VisionReading.PointPiece(x, y));
        if (piece.Width < 4 || piece.Height < 4) return null;
        var png = Enlarge(piece, o.PointScale);
        var watch = Stopwatch.StartNew();
        // The first look is cut at the point, so it is the reading of this picture for this point only.
        var key = ReadingMemory.Key("eyes", eyes.Endpoint.Model, Convert.ToHexString(SHA256.HashData(png)),
            Math.Round(x - piece.Region.Left), Math.Round(y - piece.Region.Top));
        if (memory is null || !memory.TryGet(key, out var answer))
        {
            answer = await FirstLookAsync(eyes, png, piece.Region, x, y, o, ct).ConfigureAwait(false);
            if (VisionReading.LinesPage(answer, piece.Region) is not null) memory?.Set(key, answer);
        }
        var pass1 = watch.Elapsed;
        if (VisionReading.LinesPage(answer, piece.Region) is not { } lines) return null;
        var seen = words.Normalize(lines);
        if (LineUnder(lines, x, y) is not { } line) return new EyesResult(seen, null, false, null, null, null, pass1, TimeSpan.Zero);

        var language = forced ?? ScriptLanguage(recognized, lines);
        var box = line.Box;
        var cut = crop(LineRegion(box, o));
        if (cut.Width < 1 || cut.Height < 1) return new EyesResult(seen, null, false, language, box, null, pass1, TimeSpan.Zero);
        watch.Restart();
        var read = await VisionReading.ReadAsync(eyes, Enlarge(cut, LineScale(box.Height, o.LineHeight)), ct, LinePromptFor(language))
            .ConfigureAwait(false);
        var pass2 = watch.Elapsed;
        var reading = VisionReading.ZoneText(read)?.Split('\n')[0].Trim();
        // A reading that cannot be the line (an apology, the prompt said back, a sentence read into an arrow) is none.
        if (reading is not { Length: > 0 } || !Fits(reading, box)) return new EyesResult(seen, null, false, language, box, reading, pass1, pass2);
        // The line is under the point: its word is taken at the point pulled into its box, with no same-row check against
        // the recognizer (small letters put the recognizer's and the eyes' boxes 7-12 px apart and lost right readings).
        var page = words.Normalize(OnePage(reading, box));
        var hit = words.Hit(page, Into(x, box.Left + 1, box.Right - 1), Into(y, box.Top + 1, box.Bottom - 1));
        return hit is null
            ? new EyesResult(seen, null, false, language, box, reading, pass1, pass2)
            : new EyesResult(page, hit, true, language, box, reading, pass1, pass2);
    }

    private static async Task<string> FirstLookAsync(ILlmClient eyes, byte[] png, PixelRect region, double x, double y, EyesOptions o,
        CancellationToken ct)
    {
        var request = new LlmRequest([new LlmMessage("user", VisionReading.LinesPromptXy, png)], VisionReading.LinesSchema(xy: true),
            Temperature: 0, MaxTokens: o.MaxTokens);
        var sb = new StringBuilder();
        // Cut by cancelling the request (the client must not drain the answer, or the server goes on generating it).
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopped = false;
        try
        {
            await foreach (var part in eyes.StreamAsync(request, cut.Token).ConfigureAwait(false))
            {
                sb.Append(part);
                if (!part.Contains('}') || VisionReading.LinesPage(sb.ToString(), region) is not { } sofar || !Decided(sofar, x, y)) continue;
                stopped = true;
                cut.Cancel();
                break;
            }
        }
        catch (OperationCanceledException) when (stopped && !ct.IsCancellationRequested)
        {
            // our own early stop
        }
        return sb.ToString();
    }

    /// <summary>A line counts as under the point when the point lies in its box widened by a third of its height.</summary>
    public static bool Near(PixelRect box, double x, double y)
    {
        var m = box.Height / 3;
        return x >= box.Left - m && x <= box.Right + m && y >= box.Top - m && y <= box.Bottom + m;
    }

    /// <summary>
    /// The lines so far settle the point: one holds it (between its top and bottom, near it across), or the last lies
    /// wholly below it (lines come top to bottom). A line near the point only by its margin is not enough: the next one
    /// may be the line under it (menu items a few pixels apart).
    /// </summary>
    public static bool Decided(OcrPage sofar, double x, double y) =>
        sofar.Lines.Any(l => l.Box.Top <= y && y <= l.Box.Bottom && Near(l.Box, x, y))
        || sofar.Lines.Count > 0 && sofar.Lines[^1].Box is var last && last.Top - last.Height / 3 > y;

    /// <summary>The line under the point: of those <see cref="Near"/> it, the one whose middle is closest to it.</summary>
    public static OcrLine? LineUnder(OcrPage lines, double x, double y) =>
        lines.Lines.Where(l => Near(l.Box, x, y)).OrderBy(l => Math.Abs(l.Box.CenterY - y)).FirstOrDefault();

    /// <summary>
    /// The language to name when the game's is not set: kana anywhere read around the point - Japanese; Latin letters and
    /// no CJK - English; kanji alone (Japanese or Chinese) or nothing - none named.
    /// </summary>
    public static string? ScriptLanguage(params OcrPage[] pages)
    {
        var text = string.Concat(pages.SelectMany(p => p.Lines).Select(l => l.Text));
        if (text.Any(ch => Scripts.Of(ch) == Script.Kana)) return "ja";
        if (Scripts.ContainsCjk(text)) return null;
        return text.Any(ch => Scripts.Of(ch) == Script.Latin) ? "en" : null;
    }

    /// <summary>The second look's request: with the language named (without it オプション came back as "オブジェクト").</summary>
    public static string LinePromptFor(string? language) => language switch
    {
        "ja" => Named("Japanese"),
        "zh" => Named("Chinese"),
        "en" => Named("English"),
        _ => LinePrompt,
    };

    private static string Named(string language) => $"This picture is one line of {language} text from a game. " +
        "Transcribe it exactly. Output only that text. If there is no text, output nothing.";

    /// <summary>
    /// Whether a reading can be the line in <paramref name="box"/>: its letters' width (a CJK character about a line height,
    /// any other about half, a space a third) within two and a half times the box's own proportion and two more. On the
    /// three sets (2026-09-30) this cut only wrong readings, never a right one.
    /// </summary>
    public static bool Fits(string text, PixelRect box) =>
        text.Sum(ch => char.IsWhiteSpace(ch) ? 0.3 : Scripts.IsCjk(ch) ? 1.0 : 0.5) <= box.Width / Math.Max(1, box.Height) * 2.5 + 2;

    /// <summary>One line read alone, over its box, as a page (the word under the point is cut from it as usual).</summary>
    public static OcrPage OnePage(string text, PixelRect box) =>
        VisionReading.LinesPage(JsonSerializer.Serialize(new[] { new { text, box_2d = new[] { 0, 0, 1000, 1000 } } }), box)
        ?? OcrPage.Empty(box);

    /// <summary>Where the line is cut out for the second look: with margins, so a box a little short keeps every letter.</summary>
    public static PixelRect LineRegion(PixelRect line, EyesOptions o)
    {
        var (mx, my) = (line.Height * o.LineSideMargin, line.Height * o.LineVerticalMargin);
        return new PixelRect(line.Left - mx, line.Top - my, line.Right + mx, line.Bottom + my);
    }

    /// <summary>How much a line is enlarged to reach <paramref name="target"/> pixels of height; never shrunk.</summary>
    public static double LineScale(double lineHeight, int target) => Math.Max(1, target / Math.Max(1, lineHeight));

    /// <summary>A BGRA piece of screen enlarged (Mitchell, as measured) and encoded as the PNG the eyes are sent.</summary>
    public static byte[] Enlarge((byte[] Bgra, int Width, int Height, int Stride, PixelRect Region) piece, double scale)
    {
        if (scale <= 1) return VisionReading.Png(piece.Bgra, piece.Width, piece.Height, piece.Stride);
        var info = new SKImageInfo(piece.Width, piece.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(piece.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), piece.Stride))
                throw new InvalidOperationException("the piece of screen could not be read");
            using var big = bitmap.Resize(new SKImageInfo((int)(piece.Width * scale), (int)(piece.Height * scale), SKColorType.Bgra8888,
                SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("the piece of screen could not be enlarged");
            return VisionReading.Png(big.GetPixelSpan().ToArray(), big.Width, big.Height, big.RowBytes);
        }
        finally
        {
            handle.Free();
        }
    }

    private static double Into(double v, double low, double high) => low > high ? (low + high) / 2 : Math.Clamp(v, low, high);
}
