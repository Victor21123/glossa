using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using SkiaSharp;

/// <summary>
/// <c>ocr-eval &lt;cases.json&gt; [base|v5|v6|v6m|v6sd-v6mr] [--vision &lt;api root&gt;]</c>: the lookup's own path on hard
/// frames against the true text. For each case the region around the point is cut as the lookup cuts it, recognized,
/// and the word under the point is compared with the expected word (any of "a|b"); the line is compared by character
/// error rate over letters and digits. With <c>--vision</c> a doubtful word is read again by the llama-server there
/// (started with its vision file), as the lookup does.
/// </summary>
public static class OcrEval
{
    private const int HalfWidth = 900, Up = 260, Down = 220; // LookupController's region around the cursor

    public sealed record Case(string Id, string Image, double X, double Y, string Word, string? Line, string? Lang);

    /// <summary>The recognizer to measure and the llama-server for the second reading, from the command line.</summary>
    public sealed record Options(string Model, string? Vision)
    {
        public static Options Parse(string[] args, int from)
        {
            var model = "base";
            string? vision = null;
            for (var i = from; i < args.Length; i++)
            {
                if (args[i] == "--vision" && i + 1 < args.Length) vision = args[++i];
                else model = args[i];
            }
            return new Options(model, vision);
        }

        public ILlmClient? Client() => Vision is null ? null
            : new OpenAiCompatibleClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) },
                new LlmEndpoint("vision", LlmProviderKind.LlamaServer, Vision, "local"));
    }

    public static async Task RunAsync(string casesPath, Options options, WordLookup words)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var family = Family(options.Model);
        using var ocr = Engine();
        ocr.Warm(family);
        var vision = options.Client();

        int right = 0, doubtful = 0, reread = 0;
        double cerSum = 0;
        long msSum = 0, visionMs = 0;
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
            var forced = c.Lang is "en" or "ja" or "zh" ? c.Lang : null;
            var cjk = c.Lang == "ja" ? "ja" : "zh";
            var page = words.Normalize(await ocr.RecognizeAsync(bytes, crop.Width, crop.Height, crop.RowBytes,
                new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom), family, CancellationToken.None), forced);
            sw.Stop();
            var hit = words.Hit(page, c.X, c.Y, cjk);
            var first = hit?.Word ?? "";
            var known = hit is null ? null : words.Plan(hit, cjk, "ru", new DictionarySettings(), forced).Known;
            var doubt = hit is not null && VisionReading.Doubtful(hit, known, page);
            if (doubt) doubtful++;
            long readMs = 0;
            if (vision is not null && hit is not null && doubt)
            {
                var read = Stopwatch.StartNew();
                var reading = await VisionReading.ReadAsync(vision, Png(decoded, VisionReading.Region(c.X, c.Y)), CancellationToken.None);
                readMs = read.ElapsedMilliseconds;
                visionMs += readMs;
                reread++;
                hit = words.Hit(words.Normalize(VisionReading.Correct(page, hit, reading), forced), c.X, c.Y, cjk) ?? hit;
            }
            var got = hit?.Word ?? "";
            var ok = c.Word.Split('|').Any(w => Letters(w) == Letters(got));
            var cer = Cer(hit?.Line ?? "", c.Line ?? "");
            if (ok) right++;
            cerSum += cer;
            msSum += sw.ElapsedMilliseconds;
            var change = got == first ? got : first + " -> " + got;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(ok ? "ok  " : "MISS")} {c.Id,-16} {sw.ElapsedMilliseconds,5} ms{(readMs > 0 ? $" + read {readMs} ms" : "")}  score {hit?.Score:0.000}  known {Known(known)}  {(doubt ? "doubt" : "sure ")}  word [{change}] (want {c.Word})  line [{hit?.Line}]"));
        }
        var n = Math.Max(cases.Count, 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"-- {options.Model}{(vision is null ? "" : " + vision")}: words {right}/{cases.Count}, mean line cer {cerSum / n:0.000}, mean {msSum / n} ms; doubtful {doubtful}, read again {reread}{(reread > 0 ? $" ({visionMs / reread} ms each)" : "")}"));
    }

    /// <summary>The recognizer; GLOSSA_OCR_UNCLIP widens its line boxes for a measurement.</summary>
    private static OcrEngine Engine() => new(DataPaths.OcrModels)
    {
        UnClipRatio = float.TryParse(Environment.GetEnvironmentVariable("GLOSSA_OCR_UNCLIP"), System.Globalization.NumberStyles.Float,
            CultureInfo.InvariantCulture, out var u) ? u : null,
    };

    private static string Known(bool? known) => known switch { true => "yes", false => "no ", null => "-  " };

    /// <summary>The piece of the image the model reads, clamped to the image, as PNG.</summary>
    private static byte[] Png(SKBitmap image, PixelRect region)
    {
        var left = (int)Math.Clamp(region.Left, 0, image.Width - 1);
        var top = (int)Math.Clamp(region.Top, 0, image.Height - 1);
        var rect = SKRectI.Create(left, top, Math.Max(1, (int)Math.Min(region.Right, image.Width) - left),
            Math.Max(1, (int)Math.Min(region.Bottom, image.Height) - top));
        using var part = new SKBitmap();
        image.ExtractSubset(part, rect);
        using var bgra = part.Copy(SKColorType.Bgra8888);
        return VisionReading.Png(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height, bgra.RowBytes);
    }

    /// <summary>
    /// The words of a page that would be read again, and how many words there are: each word's hit (the lookup's word
    /// under its middle) planned against the dictionaries. Chinese and Japanese lines give one hit per word the matcher cuts.
    /// </summary>
    private static (List<WordHit> Doubtful, int Total) Doubts(OcrPage page, WordLookup words, string? lang)
    {
        string? forced = null; // detected per word, as by default (a game set to one language forces it)
        var cjk = lang == "ja" ? "ja" : "zh";
        var seen = new HashSet<(string, double, double)>();
        var doubtful = new List<WordHit>();
        var total = 0;
        // GLOSSA_OCR_DOUBTS=3: how far each line's box reaches past its read words, in line heights (a dropped letter).
        if (Environment.GetEnvironmentVariable("GLOSSA_OCR_DOUBTS") == "3")
            foreach (var l in page.Lines.Where(l => l.Words.Count > 0))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  gap {(l.Box.Right - l.Words.Max(w => w.Box.Right)) / l.Box.Height:0.00} {(l.Words.Min(w => w.Box.Left) - l.Box.Left) / l.Box.Height:0.00} [{l.Text}]"));
        foreach (var unit in page.Lines.SelectMany(l => l.Words))
        {
            var hit = words.Hit(page, unit.Box.CenterX, unit.Box.CenterY, cjk);
            if (hit is null || !seen.Add((hit.Word, hit.Box.Left, hit.Box.Top)) || !hit.Word.Any(char.IsLetter)) continue;
            total++;
            var plan = words.Plan(hit, cjk, "ru", new DictionarySettings(), forced);
            var known = plan.Known;
            // GLOSSA_OCR_DOUBTS=2 lists every word with what decided it.
            if (Environment.GetEnvironmentVariable("GLOSSA_OCR_DOUBTS") == "2")
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  word [{hit.Word}] {plan.Language} known {Known(known)} score {hit.Score:0.00} sections {plan.Sections.Count} level {plan.Seed.Level}"));
            if (!VisionReading.Doubtful(hit, known, page)) continue;
            doubtful.Add(hit);
            // GLOSSA_OCR_DOUBTS=1 lists them: why each one would be read again.
            if (Environment.GetEnvironmentVariable("GLOSSA_OCR_DOUBTS") == "1")
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  doubt {(known == false ? "unknown" : hit.Score < VisionReading.MinScore ? "unsure " : "tail   ")} {hit.Score:0.00} {hit.Word}"));
        }
        return (doubtful, total);
    }

    /// <summary>
    /// <c>ocr-found &lt;eval_cases.json&gt; [v5|v6]</c>: on the translation test frames (image and word, no point), whether the
    /// word is read anywhere on its frame, recognized whole. A check that a new model does not lose ordinary text.
    /// </summary>
    public static async Task FoundAsync(string casesPath, Options options, WordLookup words)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var family = Family(options.Model);
        using var ocr = Engine();
        ocr.Warm(family);
        var pages = new Dictionary<string, (string Text, long Ms)>();
        int found = 0, doubtful = 0, total = 0;
        foreach (var c in cases.Where(c => c.Image is not null))
        {
            if (!pages.TryGetValue(c.Image, out var page))
            {
                using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
                using var bgra = decoded.Copy(SKColorType.Bgra8888);
                var sw = Stopwatch.StartNew();
                var result = words.Normalize(await ocr.RecognizeAsync(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height,
                    bgra.RowBytes, new PixelRect(0, 0, bgra.Width, bgra.Height), family, CancellationToken.None), c.Lang);
                pages[c.Image] = page = (string.Join(" ", result.Lines.Select(l => l.Text)), sw.ElapsedMilliseconds);
                var (d, t) = Doubts(result, words, c.Lang);
                doubtful += d.Count;
                total += t;
            }
            var ok = Letters(page.Text).Contains(Letters(c.Word), StringComparison.Ordinal);
            if (ok) found++;
            else Console.WriteLine($"MISS {c.Id,-22} {c.Word}");
        }
        Console.WriteLine($"-- doubtful (would be read again): {doubtful} of {total} words on the frames");
        Console.WriteLine($"-- {options.Model}: found {found}/{cases.Count(c => c.Image is not null)} words on {pages.Count} frames, mean {pages.Values.Sum(p => p.Ms) / Math.Max(pages.Count, 1)} ms per frame");
    }

    /// <summary>
    /// <c>ocr-at &lt;image&gt; &lt;x&gt; &lt;y&gt; [ja|zh|en]</c>: one lookup at a point of a saved frame, as the app makes it: the
    /// region around the point, its lines with their boxes and word pieces, and the word, line and context it gives.
    /// </summary>
    public static async Task AtAsync(string image, double x, double y, string? lang, WordLookup words)
    {
        using var decoded = SKBitmap.Decode(image) ?? throw new InvalidDataException("not an image: " + image);
        var left = (int)Math.Clamp(x - HalfWidth, 0, decoded.Width - 1);
        var top = (int)Math.Clamp(y - Up, 0, decoded.Height - 1);
        var rect = SKRectI.Create(left, top, (int)Math.Min(x + HalfWidth, decoded.Width) - left, (int)Math.Min(y + Down, decoded.Height) - top);
        using var part = new SKBitmap();
        decoded.ExtractSubset(part, rect);
        using var crop = part.Copy(SKColorType.Bgra8888);
        using var ocr = Engine();
        var forced = lang is "en" or "ja" or "zh" ? lang : null;
        var cjk = lang == "zh" ? "zh" : "ja";
        var page = words.Normalize(await ocr.RecognizeAsync(crop.GetPixelSpan().ToArray(), crop.Width, crop.Height, crop.RowBytes,
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom), OcrModelFamily.CjkLatin, CancellationToken.None), forced);
        foreach (var l in page.Lines.OrderBy(l => l.Box.Top))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{l.Box.Left:F0},{l.Box.Top:F0} {l.Box.Width:F0}x{l.Box.Height:F0}] {l.Score:0.00} {l.Text}  | {string.Join(" ", l.Words.Select(w => $"{w.Text}@{w.Box.Left:F0}-{w.Box.Right:F0}/{w.Box.Height:F0}"))}"));
        var hit = words.Hit(page, x, y, cjk);
        if (hit is null)
        {
            Console.WriteLine("-- no text under the point");
            return;
        }
        Console.WriteLine($"-- word [{hit.Word}] box {hit.Box.Left:F0},{hit.Box.Top:F0}-{hit.Box.Right:F0},{hit.Box.Bottom:F0} score {hit.Score:0.00}");
        Console.WriteLine($"-- line [{hit.Line}]");
        Console.WriteLine($"-- context [{hit.Context}] at {hit.ContextOffset}");
    }

    public sealed record LinesCase(string Id, string Image, string? Lang, List<string> Lines);

    /// <summary>
    /// <c>ocr-lines &lt;cases.json&gt; [v5|v6|v6m]</c>: pieces of game screens recognized whole; each true line is looked for
    /// in what was read (a line glued to its neighbour still counts) and scored by the letters that differ.
    /// </summary>
    public static async Task LinesAsync(string casesPath, Options options, WordLookup words)
    {
        var cases = JsonSerializer.Deserialize<List<LinesCase>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var family = Family(options.Model);
        using var ocr = Engine();
        ocr.Warm(family);
        var vision = options.Client();
        int exact = 0, total = 0, doubtful = 0, units = 0;
        double cerSum = 0;
        long msSum = 0;
        foreach (var c in cases)
        {
            using var decoded = SKBitmap.Decode(c.Image) ?? throw new InvalidDataException("not an image: " + c.Image);
            using var bgra = decoded.Copy(SKColorType.Bgra8888);
            var sw = Stopwatch.StartNew();
            var whole = new PixelRect(0, 0, bgra.Width, bgra.Height);
            var page = words.Normalize(await ocr.RecognizeAsync(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height, bgra.RowBytes,
                whole, family, CancellationToken.None), c.Lang);
            var (d, t) = Doubts(page, words, c.Lang);
            doubtful += d.Count;
            units += t;
            // With sight each doubtful word is read again as a lookup at its middle would: the piece around it.
            if (vision is not null)
                foreach (var hit in d)
                {
                    // Earlier fixes may have changed the line: take the word under the same point now.
                    var (x, y) = (hit.Box.CenterX, hit.Box.CenterY);
                    if (words.Hit(page, x, y, c.Lang == "ja" ? "ja" : "zh") is not { } now) continue;
                    var reading = await VisionReading.ReadAsync(vision, Png(decoded, VisionReading.Region(x, y)), CancellationToken.None);
                    var before = now.Line;
                    page = words.Normalize(VisionReading.Correct(page, now, reading), c.Lang);
                    if (Environment.GetEnvironmentVariable("GLOSSA_OCR_DOUBTS") == "1")
                        Console.WriteLine($"  read {now.Word}: [{before}] -> [{words.Hit(page, x, y, c.Lang == "ja" ? "ja" : "zh")?.Line}]  model: {reading.ReplaceLineEndings("|")}");
                }
            msSum += sw.ElapsedMilliseconds;
            var read = page.Lines.Select(l => (l.Text, Letters: Letters(l.Text))).ToList();
            foreach (var want in c.Lines)
            {
                var w = Letters(want);
                var best = read.Count == 0 ? (Text: "", Distance: w.Length)
                    : read.Select(r => (r.Text, Distance: SubstringDistance(w, r.Letters))).MinBy(r => r.Distance);
                total++;
                cerSum += best.Distance / (double)Math.Max(w.Length, 1);
                if (best.Distance == 0) exact++;
                var lineScore = page.Lines.FirstOrDefault(l => l.Text == best.Text)?.Score ?? 0;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(best.Distance == 0 ? "ok  " : "MISS")} {c.Id,-16} score {lineScore:0.000}  \"{want}\" <- \"{best.Text}\" ({best.Distance})"));
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"-- {options.Model}{(vision is null ? "" : " + vision")}: lines exact {exact}/{total}, mean cer {cerSum / Math.Max(total, 1):0.000}, mean {msSum / Math.Max(cases.Count, 1)} ms per image"));
        Console.WriteLine($"-- doubtful (would be read again): {doubtful} of {units} words");
    }

    /// <summary>Edits from <paramref name="want"/> to the closest stretch of <paramref name="text"/> (free start and end).</summary>
    private static int SubstringDistance(string want, string text)
    {
        var prev = new int[text.Length + 1];
        var cur = new int[text.Length + 1];
        for (var i = 1; i <= want.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= text.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (want[i - 1] == text[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev.Min();
    }

    private static OcrModelFamily Family(string model) => model switch
    {
        "v5" => OcrModelFamily.V5Mobile,
        "v6" => OcrModelFamily.V6Multi,
        "v6m" => OcrModelFamily.V6Medium,
        "v6sd-v6mr" => OcrModelFamily.V6SmallDetMediumRec,
        _ => OcrModelFamily.CjkLatin, // base: the lookup's own, v5 detector and v6 medium reader
    };

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
