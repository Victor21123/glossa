using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Glossa.Core.Config;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using SkiaSharp;

/// <summary>
/// <c>ocr-eval &lt;cases.json&gt; [v5|v6]</c>: the lookup's own path on hard frames against the true text. For each case the
/// region around the point is cut as the lookup cuts it, recognized, and the word under the point is compared with the
/// expected word (any of "a|b"); the line is compared by character error rate over letters and digits.
/// </summary>
public static class OcrEval
{
    private const int HalfWidth = 900, Up = 260, Down = 220; // LookupController's region around the cursor

    public sealed record Case(string Id, string Image, double X, double Y, string Word, string? Line, string? Lang);

    public static async Task RunAsync(string casesPath, string model, WordLookup words)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var family = model == "v6" ? OcrModelFamily.V6Multi : OcrModelFamily.CjkLatin;
        using var ocr = new OcrEngine(DataPaths.OcrModels);
        ocr.Warm(family);

        int right = 0;
        double cerSum = 0;
        long msSum = 0;
        foreach (var c in cases)
        {
            using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
            var left = (int)Math.Clamp(c.X - HalfWidth, 0, decoded.Width - 1);
            var top = (int)Math.Clamp(c.Y - Up, 0, decoded.Height - 1);
            var rect = SKRectI.Create(left, top, (int)Math.Min(c.X + HalfWidth, decoded.Width) - left,
                (int)Math.Min(c.Y + Down, decoded.Height) - top);
            using var part = new SKBitmap();
            decoded.ExtractSubset(part, rect);
            using var crop = part.Copy(SKColorType.Bgra8888);
            var bytes = crop.GetPixelSpan().ToArray();

            var sw = Stopwatch.StartNew();
            var page = await ocr.RecognizeAsync(bytes, crop.Width, crop.Height, crop.RowBytes,
                new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom), family, CancellationToken.None);
            sw.Stop();
            var hit = words.Hit(page, c.X, c.Y, c.Lang == "ja" ? "ja" : "zh");
            var got = hit?.Word ?? "";
            var ok = c.Word.Split('|').Any(w => Letters(w) == Letters(got));
            var cer = Cer(hit?.Line ?? "", c.Line ?? "");
            if (ok) right++;
            cerSum += cer;
            msSum += sw.ElapsedMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(ok ? "ok  " : "MISS")} {c.Id,-16} {sw.ElapsedMilliseconds,5} ms  cer {cer:0.00}  word \"{got}\" (want {c.Word})  line \"{hit?.Line}\""));
        }
        var n = Math.Max(cases.Count, 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"-- {model}: words {right}/{cases.Count}, mean line cer {cerSum / n:0.000}, mean {msSum / n} ms"));
    }

    /// <summary>
    /// <c>ocr-found &lt;eval_cases.json&gt; [v5|v6]</c>: on the translation test frames (image and word, no point), whether the
    /// word is read anywhere on its frame, recognized whole. A check that a new model does not lose ordinary text.
    /// </summary>
    public static async Task FoundAsync(string casesPath, string model)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var family = model == "v6" ? OcrModelFamily.V6Multi : OcrModelFamily.CjkLatin;
        using var ocr = new OcrEngine(DataPaths.OcrModels);
        ocr.Warm(family);
        var pages = new Dictionary<string, (string Text, long Ms)>();
        int found = 0;
        foreach (var c in cases.Where(c => c.Image is not null))
        {
            if (!pages.TryGetValue(c.Image, out var page))
            {
                using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
                using var bgra = decoded.Copy(SKColorType.Bgra8888);
                var sw = Stopwatch.StartNew();
                var result = await ocr.RecognizeAsync(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height, bgra.RowBytes,
                    new PixelRect(0, 0, bgra.Width, bgra.Height), family, CancellationToken.None);
                pages[c.Image] = page = (string.Join(" ", result.Lines.Select(l => l.Text)), sw.ElapsedMilliseconds);
            }
            var ok = Letters(page.Text).Contains(Letters(c.Word), StringComparison.Ordinal);
            if (ok) found++;
            else Console.WriteLine($"MISS {c.Id,-22} {c.Word}");
        }
        Console.WriteLine($"-- {model}: found {found}/{cases.Count(c => c.Image is not null)} words on {pages.Count} frames, mean {pages.Values.Sum(p => p.Ms) / Math.Max(pages.Count, 1)} ms per frame");
    }

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Character error rate over letters and digits: spacing and punctuation mistakes do not count.</summary>
    private static double Cer(string got, string want)
    {
        var a = Letters(got);
        var b = Letters(want);
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length] / (double)Math.Max(b.Length, 1);
    }
}
