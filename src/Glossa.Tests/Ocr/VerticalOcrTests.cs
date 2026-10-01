using Glossa.Core.Ocr;
using Glossa.Core.Text;
using SkiaSharp;
using Xunit.Abstractions;

namespace Glossa.Tests.Ocr;

/// <summary>
/// Reading vertical Japanese (tategaki): the engine reads each tall detector box itself, rotated counter-clockwise (the
/// library rotates it clockwise and returns garbage). The logic around the read is tested without models; the real
/// models run on the synthetic frames in D:\GlossaData\test\tategaki (columns: make_frames.py).
/// </summary>
public class VerticalOcrTests(ITestOutputHelper output)
{
    private const string ModelsDir = @"D:\GlossaData\models\ocr";
    private const string Frames = @"D:\GlossaData\test\tategaki";

    // ---------------------------------------------------------------- CTC decoding and character boxes

    private static readonly string[] Chars = ["あ", "い", "う"]; // classes: blank, あ, い, う, space

    /// <summary>Logits where the given class wins each timestep with probability 0.9.</summary>
    private static float[] Logits(int[] winners, int classes = 5)
    {
        var data = new float[winners.Length * classes];
        for (var t = 0; t < winners.Length; t++)
        {
            for (var c = 0; c < classes; c++) data[t * classes + c] = 0.025f;
            data[t * classes + winners[t]] = 0.9f;
        }
        return data;
    }

    [Fact]
    public void Decode_collapses_repeats_and_drops_blanks()
    {
        // blank, あ, あ, blank, あ, い, い, blank, space: a blank between two あ keeps both, a run is one character
        var read = VerticalReader.Decode(Logits([0, 1, 1, 0, 1, 2, 2, 0, 4]), 9, 5, Chars);

        Assert.Equal(["あ", "あ", "い", " "], read.Select(c => c.Text));
        Assert.Equal([1, 4, 5, 8], read.Select(c => c.First));
        Assert.Equal([2, 4, 6, 8], read.Select(c => c.Last));
        Assert.All(read, c => Assert.Equal(0.9f, c.Score, 3));
    }

    [Fact]
    public void Decode_of_silence_is_empty()
    {
        Assert.Empty(VerticalReader.Decode(Logits([0, 0, 0, 0]), 4, 5, Chars));
        Assert.Empty(VerticalReader.Decode([], 0, 5, Chars));
    }

    [Fact]
    public void Decode_ignores_a_class_outside_the_dictionary()
    {
        // 6 classes but 3 dictionary lines + blank + space = 5: class 5 has no character
        Assert.Equal(["あ"], VerticalReader.Decode(Logits([1, 5], 6), 2, 6, Chars).Select(c => c.Text));
    }

    [Fact]
    public void Character_boxes_follow_the_timesteps()
    {
        // 10 px per step from y 100: あ (steps 1-2) is centred at 120, い (step 5) at 155, う (steps 8-9) at 190
        var read = VerticalReader.Decode(Logits([0, 1, 1, 0, 0, 2, 0, 0, 3, 3]), 10, 5, Chars);
        var spans = VerticalReader.Spans(read, stepLength: 10, origin: 100, top: 104, bottom: 206);

        Assert.Equal(3, spans.Count);
        Assert.Equal(104, spans[0].Top);                       // the first starts at the column top
        Assert.Equal((120 + 155) / 2.0, spans[0].Bottom);       // neighbours split at the midpoint
        Assert.Equal(spans[0].Bottom, spans[1].Top);
        Assert.Equal((155 + 190) / 2.0, spans[1].Bottom);
        Assert.Equal(spans[1].Bottom, spans[2].Top);
        Assert.Equal(206, spans[2].Bottom);                    // the last ends at the column bottom
    }

    [Fact]
    public void Character_boxes_never_leave_the_column()
    {
        // a centre outside [top, bottom] (padding, a wrong stride) is clamped, the boxes stay ordered
        var read = VerticalReader.Decode(Logits([1, 0, 0, 0, 0, 0, 0, 0, 0, 2]), 10, 5, Chars);
        var spans = VerticalReader.Spans(read, stepLength: 100, origin: 0, top: 50, bottom: 80);

        Assert.All(spans, s => { Assert.InRange(s.Top, 50, 80); Assert.InRange(s.Bottom, 50, 80); Assert.True(s.Bottom >= s.Top); });
    }

    // ---------------------------------------------------------------- columns around the read

    private static PixelRect R(double l, double t, double r, double b) => new(l, t, r, b);

    /// <summary>What the library returns for a tall box: its clockwise read, garbage.</summary>
    private static OcrLine Library(PixelRect box, string text = "ツ晕、M判") =>
        new(text, box, [new OcrWord(text, box, 0.4f)], 0.4f);

    /// <summary>A read of a whole column: one character per equal slice of the box.</summary>
    private static VerticalRead Read(PixelRect box, string text)
    {
        var step = text.Length == 0 ? 0 : box.Height / text.Length;
        var words = text.Select((c, i) => new OcrWord(c.ToString(), R(box.Left, box.Top + i * step, box.Right, box.Top + (i + 1) * step), 0.95f)).ToList();
        return new VerticalRead(text, words, 0.95f);
    }

    /// <summary>A reader that returns the given text for every box and remembers which boxes it was asked about.</summary>
    private sealed class FakeReader(Func<PixelRect, string?> text)
    {
        public List<PixelRect> Asked { get; } = [];

        public VerticalRead? Read(PixelRect box)
        {
            Asked.Add(box);
            return text(box) is { } t ? VerticalOcrTests.Read(box, t) : null;
        }
    }

    private const string Col0 = "今日はいい天気ですね。", Col1 = "散歩に行きませんか？";

    [Fact]
    public void Merge_joins_split_pieces_of_a_column_top_to_bottom()
    {
        // the app's region splits a column at a comma; both pieces are one column, read once, top to bottom
        var upper = R(1469, 191, 1530, 420);
        var lower = R(1471, 432, 1529, 726);
        var reader = new FakeReader(_ => Col0);

        var lines = VerticalColumns.Apply([Library(lower), Library(upper)], reader.Read);

        var line = Assert.Single(lines);
        Assert.True(line.Vertical);
        Assert.Equal(Col0, line.Text);
        Assert.Equal(R(1469, 191, 1530, 726), Assert.Single(reader.Asked));
        Assert.Equal(R(1469, 191, 1530, 726), line.Box);
        Assert.Equal(Col0.Select(c => c.ToString()), line.Words.Select(w => w.Text));
    }

    [Fact]
    public void Merge_never_joins_neighbouring_columns()
    {
        var right = R(1469, 191, 1530, 726);
        var left = R(1391, 194, 1450, 698); // 19 px apart: close, but side by side
        var reader = new FakeReader(b => b.Left < 1400 ? Col1 : Col0);

        var lines = VerticalColumns.Apply([Library(left), Library(right)], reader.Read);

        Assert.Equal(2, reader.Asked.Count);
        Assert.Equal([Col0, Col1], lines.Select(l => l.Text)); // right to left
        Assert.All(lines, l => Assert.True(l.Vertical));
    }

    [Fact]
    public void Pieces_too_far_apart_along_the_column_are_not_joined()
    {
        var reader = new FakeReader(_ => Col0);

        VerticalColumns.Apply([Library(R(1469, 100, 1530, 300)), Library(R(1471, 600, 1529, 800))], reader.Read);

        Assert.Equal(2, reader.Asked.Count);
    }

    [Fact]
    public void Column_final_period_box_is_attached_not_read_as_Latin()
    {
        // detector: one box for the column and a tiny separate box for its final "。", which the library reads as "o"
        var column = R(1471, 191, 1529, 680);
        var period = new OcrLine("o", R(1488, 690, 1522, 729), [new OcrWord("o", R(1488, 690, 1522, 729), 0.5f)], 0.5f);
        var reader = new FakeReader(_ => "今日はいい天気ですね");

        var lines = VerticalColumns.Apply([Library(column), period], reader.Read);

        var line = Assert.Single(lines);
        Assert.True(line.Vertical);
        Assert.Equal("今日はいい天気ですね。", line.Text);
        Assert.Equal("。", line.Words[^1].Text);
        Assert.Equal(R(1488, 690, 1522, 729), line.Words[^1].Box);
        Assert.Equal(729, line.Box.Bottom);
        Assert.DoesNotContain(lines, l => l.Text == "o");
    }

    [Theory]
    [InlineData("O")]
    [InlineData("0")]
    [InlineData("°")]
    [InlineData("")]
    public void Column_final_period_box_is_recognised_by_what_the_library_made_of_it(string libraryText)
    {
        var column = R(1471, 191, 1529, 680);
        var period = new OcrLine(libraryText, R(1488, 690, 1522, 729), [], 0.5f);

        var lines = VerticalColumns.Apply([Library(column), period], new FakeReader(_ => "今日はいい天気ですね").Read);

        Assert.Equal("今日はいい天気ですね。", Assert.Single(lines).Text);
    }

    [Fact]
    public void A_period_box_is_dropped_when_the_column_already_ends_with_one()
    {
        var column = R(1471, 191, 1529, 700);
        var period = new OcrLine("o", R(1488, 695, 1522, 734), [new OcrWord("o", R(1488, 695, 1522, 734), 0.5f)], 0.5f);

        var lines = VerticalColumns.Apply([Library(column), period], new FakeReader(_ => Col0).Read);

        var line = Assert.Single(lines);
        Assert.Equal(Col0, line.Text);
        Assert.Equal(Col0.Length, line.Words.Count);
    }

    [Fact]
    public void A_small_box_away_from_every_column_stays_what_it_was()
    {
        var column = R(1471, 191, 1529, 680);
        var far = new OcrLine("o", R(1488, 900, 1522, 939), [new OcrWord("o", R(1488, 900, 1522, 939), 0.5f)], 0.5f);
        var beside = new OcrLine("o", R(1300, 690, 1334, 729), [new OcrWord("o", R(1300, 690, 1334, 729), 0.5f)], 0.5f);

        var lines = VerticalColumns.Apply([Library(column), far, beside], new FakeReader(_ => Col0).Read);

        Assert.Equal(3, lines.Count);
        Assert.Contains(far, lines);
        Assert.Contains(beside, lines);
    }

    [Fact]
    public void Glyph_pieces_inside_a_column_are_dropped_wide_lines_over_it_stay()
    {
        var column = R(1471, 191, 1529, 680);
        var piece = new OcrLine("十", R(1480, 300, 1520, 340), [new OcrWord("十", R(1480, 300, 1520, 340), 0.8f)], 0.8f);
        var banner = new OcrLine("CHAPTER ONE", R(1300, 400, 1700, 440), [new OcrWord("CHAPTER", R(1300, 400, 1700, 440), 0.9f)], 0.9f);

        var lines = VerticalColumns.Apply([Library(column), piece, banner], new FakeReader(_ => Col0).Read);

        Assert.DoesNotContain(piece, lines);
        Assert.Contains(banner, lines);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Vertical_lines_are_ordered_right_to_left()
    {
        var a = R(1300, 190, 1360, 700);
        var b = R(1469, 191, 1530, 726);
        var c = R(1391, 194, 1450, 698);
        var reader = new FakeReader(x => x.Left < 1350 ? "はい喜んで" : x.Left < 1400 ? Col1 : Col0);

        var lines = VerticalColumns.Apply([Library(a), Library(b), Library(c)], reader.Read);

        Assert.Equal([Col0, Col1, "はい喜んで"], lines.Select(l => l.Text));
        Assert.Equal(lines.OrderByDescending(l => l.Box.Right).ToList(), lines);
    }

    [Fact]
    public void Merge_fragments_keeps_vertical_lines_as_they_are_and_orders_them_right_to_left()
    {
        var left = new OcrLine(Col1, R(1391, 194, 1450, 698), [], 0.9f, Vertical: true);
        var right = new OcrLine(Col0, R(1469, 191, 1530, 726), [], 0.9f, Vertical: true);

        var merged = OcrEngine.MergeFragments([left, right]);

        Assert.Equal([right, left], merged);
    }

    [Fact]
    public void A_tall_box_that_reads_as_fewer_than_two_kana_or_han_keeps_the_library_result()
    {
        var box = R(500, 100, 560, 400);
        var library = Library(box, "ツl");

        // Latin and digits, a lone kana, a dash, nothing at all, no read
        foreach (var read in new[] { "ABC123", "あ", "ー", "あ1", "", null })
        {
            var lines = VerticalColumns.Apply([library], new FakeReader(_ => read).Read);

            var kept = Assert.Single(lines);
            Assert.Same(library, kept);
            Assert.False(kept.Vertical);
        }
    }

    [Fact]
    public void Two_kana_or_han_are_enough_for_a_column()
    {
        var lines = VerticalColumns.Apply([Library(R(500, 100, 560, 300))], new FakeReader(_ => "はい").Read);

        Assert.True(Assert.Single(lines).Vertical);
    }

    [Fact]
    public void Wide_boxes_are_never_read_as_columns()
    {
        var wide = new OcrLine("Hello", R(100, 100, 400, 140), [new OcrWord("Hello", R(100, 100, 400, 140), 0.9f)], 0.9f);
        var almost = new OcrLine("ab", R(100, 200, 200, 349), [], 0.9f); // 1.49 times taller than wide
        var reader = new FakeReader(_ => Col0);

        var lines = VerticalColumns.Apply([wide, almost], reader.Read);

        Assert.Empty(reader.Asked);
        Assert.Equal([wide, almost], lines);
    }

    [Fact]
    public void A_tall_box_of_exactly_two_and_a_half_times_is_a_candidate_two_point_four_is_not()
    {
        var reader = new FakeReader(_ => Col0);

        VerticalColumns.Apply([Library(R(100, 100, 200, 349))], reader.Read);
        Assert.Empty(reader.Asked);
        VerticalColumns.Apply([Library(R(100, 100, 200, 350))], reader.Read);
        Assert.Single(reader.Asked);
    }

    [Fact]
    public void Horizontal_lines_pass_through_untouched_next_to_columns()
    {
        var plate = new OcrLine("Narrator", R(1100, 900, 1300, 940), [new OcrWord("Narrator", R(1100, 900, 1300, 940), 0.9f)], 0.9f);

        var lines = VerticalColumns.Apply([plate, Library(R(1469, 191, 1530, 726))], new FakeReader(_ => Col0).Read);

        Assert.Equal(2, lines.Count);
        Assert.Contains(plate, lines);
        Assert.Equal(Col0, lines.Single(l => l.Vertical).Text);
    }

    // ---------------------------------------------------------------- real models

    /// <summary>The models of the lookup (the v6 recognizer too: without it the engine falls back to v5) and the frames are there.</summary>
    private static bool Ready() =>
        OcrEngine.ModelsPresent(ModelsDir) && File.Exists(Path.Combine(ModelsDir, "v6", "PP-OCRv6_rec_medium.onnx"))
        && File.Exists(Path.Combine(ModelsDir, "v6", "ppocrv6_small_dict.txt")) && Directory.Exists(Frames);

    /// <summary>A frame as BGRA; with a canvas size, the picture is scaled and put on a black canvas at the given place.</summary>
    private static (byte[] Bgra, int W, int H, int Stride) Load(string name, int canvasW = 0, int canvasH = 0, int atX = 0, int atY = 0, int scale = 1)
    {
        using var decoded = SKBitmap.Decode(Path.Combine(Frames, name)) ?? throw new InvalidDataException(name);
        using var scaled = scale == 1 ? null : decoded.Resize(new SKImageInfo(decoded.Width * scale, decoded.Height * scale,
            SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var bmp = new SKBitmap(new SKImageInfo(canvasW > 0 ? canvasW : decoded.Width, canvasH > 0 ? canvasH : decoded.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(scaled ?? decoded, atX, atY);
        }
        return (bmp.Bytes, bmp.Width, bmp.Height, bmp.RowBytes);
    }

    private static string Ignoring(string s) => new(s.Where(c => !"、。，．ー―－‐-—–─|｜".Contains(c)).ToArray());

    private static double Similarity(string a, string b)
    {
        a = Ignoring(a);
        b = Ignoring(b);
        if (a.Length == 0 && b.Length == 0) return 1;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return 1.0 - prev[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    private static async Task<OcrPage> Recognize(OcrEngine ocr, (byte[] Bgra, int W, int H, int Stride) img) =>
        await ocr.RecognizeAsync(img.Bgra, img.W, img.H, img.Stride, new PixelRect(0, 0, img.W, img.H), OcrModelFamily.CjkLatin, CancellationToken.None);

    private async Task ReadsColumns(string frame, string[] expected, (byte[] Bgra, int W, int H, int Stride)? image = null, double least = 0.85)
    {
        var img = image ?? Load(frame);
        using var ocr = new OcrEngine(ModelsDir);
        ocr.Warm(OcrModelFamily.CjkLatin);
        var page = await Recognize(ocr, img);
        output.WriteLine($"{frame}: {page.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var l in page.Lines) output.WriteLine($"  {(l.Vertical ? "V" : "H")} {l.Box} {l.Text} ({l.Score:0.00})");

        var columns = page.Lines.Where(l => l.Vertical).OrderByDescending(l => l.Box.Right).ToList();
        Assert.Equal(expected.Length, columns.Count);
        for (var c = 0; c < expected.Length; c++)
        {
            var sim = Similarity(columns[c].Text, expected[c]);
            output.WriteLine($"  column {c}: {sim:0.00} {columns[c].Text} <- {expected[c]}");
            Assert.True(sim >= least, $"column {c}: {columns[c].Text} against {expected[c]} = {sim:0.00}");
            Assert.True(columns[c].Words.Count >= columns[c].Text.Length - 1);
        }
        // no clockwise garbage left behind as a horizontal line
        Assert.DoesNotContain(page.Lines, l => !l.Vertical && l.Box.Height >= 1.5 * l.Box.Width);
    }

    private static readonly string[] Vn = ["今日はいい天気ですね。", "散歩に行きませんか？", "はい、喜んで――。"];
    private static readonly string[] Moan = ["ああっ、出てるっ……", "んっ、んあああああッッっっ"];
    private static readonly string[] Book =
        ["吾輩は猫である。名前はまだ無い。", "どこで生れたかとんと見当がつかぬ。", "何でも薄暗いじめじめした所で", "ニャーニャー泣いていた事だけは記憶している。"];

    [Fact]
    public async Task Reads_each_column_of_the_vertical_vn_frame()
    {
        if (!Ready()) return;
        await ReadsColumns("ja_vert_vn.png", Vn);
    }

    [Fact]
    public async Task Reads_each_column_of_the_vertical_moan_frame()
    {
        if (!Ready()) return;
        await ReadsColumns("ja_vert_moan.png", Moan);
    }

    [Fact]
    public async Task Reads_the_tester_crop_padded_onto_a_canvas()
    {
        if (!Ready()) return;
        // The tester's real screenshot of two columns (132x341, leaning 4-6 degrees, text on a textured picture). The
        // detector needs it about three times larger and on a plain canvas to find two columns at all (at its own size it
        // finds a blob; a canvas taller than 900 px merges them), and its box for the second column stops at 233 of 339 px,
        // so its last four characters (ッッっっ) are never in the box. Read along the detector's quadrilateral (deskew) the
        // two columns score 0.78 and 0.67 against this list (on a 1000x800 canvas 0.67 and 0.67, axis-aligned 0.67 and 0.58):
        // what is in the box reads right.
        await ReadsColumns("tester_vert_crop.png", Moan, Load("tester_vert_crop.png", 1000, 900, 300, 100, scale: 3), least: 0.65);
    }

    [Fact]
    public async Task Reads_the_book_frame_columns()
    {
        if (!Ready()) return;
        await ReadsColumns("ja_vert_book.png", Book);
    }

    [Fact]
    public async Task Hit_at_each_case_point_returns_a_word_of_its_column()
    {
        if (!Ready()) return;
        var cases = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Frames, "cases.json"))).RootElement
            .EnumerateArray().Where(c => c.GetProperty("id").GetString()!.StartsWith("vert-")).ToList();
        Assert.Equal(9, cases.Count);

        using var ocr = new OcrEngine(ModelsDir);
        var pages = new Dictionary<string, OcrPage>();
        foreach (var c in cases)
        {
            var file = Path.GetFileName(c.GetProperty("image").GetString()!);
            if (!pages.TryGetValue(file, out var page)) pages[file] = page = await Recognize(ocr, Load(file));
            var (x, y, replica) = (c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(), c.GetProperty("replica").GetString()!);

            var hit = new HitTester().Hit(page, x, y);

            Assert.NotNull(hit);
            output.WriteLine($"{c.GetProperty("id").GetString()}: [{hit.Word}] in [{hit.Context}]");
            Assert.True(hit.Word.All(ch => replica.Contains(ch)), $"{hit.Word} is not from {replica}");
            // Columns this close and of one size are one paragraph for the hit tester, as lines of a horizontal one are: the
            // context holds the column's replica whole (a neighbour column may come with it).
            Assert.Contains(Ignoring(replica), Ignoring(hit.Context));
        }
    }

    private static (long WorkingSet, long Private) Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        return (me.WorkingSet64 >> 20, me.PrivateMemorySize64 >> 20);
    }

    [Fact]
    public async Task Vertical_reading_costs_are_measured()
    {
        if (!Ready()) return;
        var img = Load("ja_vert_vn.png");
        using var plain = new OcrEngine(ModelsDir) { VerticalReading = false };
        plain.Warm(OcrModelFamily.CjkLatin);
        await Recognize(plain, img);
        var before = await Recognize(plain, img);
        var memBefore = Memory(); // the library's sessions warm, no vertical session yet

        using var ocr = new OcrEngine(ModelsDir);
        ocr.Warm(OcrModelFamily.CjkLatin);
        var first = await Recognize(ocr, img); // creates the vertical session
        var memAfter = Memory();
        var after = await Recognize(ocr, img);
        output.WriteLine($"time: without vertical reading {before.Elapsed.TotalMilliseconds:F0} ms; first with {first.Elapsed.TotalMilliseconds:F0} ms; warm with {after.Elapsed.TotalMilliseconds:F0} ms");
        output.WriteLine($"memory MB (working set / private), library sessions only {memBefore.WorkingSet} / {memBefore.Private}; second engine with the vertical session {memAfter.WorkingSet} / {memAfter.Private}");

        // the vertical session alone: created, then three columns read
        var m0 = Memory();
        using var frame = SKBitmap.Decode(Path.Combine(Frames, "ja_vert_vn.png"));
        using var reader = new VerticalReader(Path.Combine(ModelsDir, "v6", "PP-OCRv6_rec_medium.onnx"), Path.Combine(ModelsDir, "v6", "ppocrv6_small_dict.txt"), 4, OcrMemory.Arena);
        var m1 = Memory();
        foreach (var line in after.Lines.Where(l => l.Vertical))
        {
            reader.Read(frame, line.Box);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) reader.Read(frame, line.Box);
            output.WriteLine($"column of {line.Text.Length} characters ({line.Box.Width:F0}x{line.Box.Height:F0}): {sw.Elapsed.TotalMilliseconds / 5:F0} ms");
        }
        var m2 = Memory();
        output.WriteLine($"memory MB (working set / private): before the session {m0.WorkingSet} / {m0.Private}, created {m1.WorkingSet} / {m1.Private}, after reading the columns {m2.WorkingSet} / {m2.Private}");
        Assert.Equal(3, after.Lines.Count(l => l.Vertical));
        Assert.DoesNotContain(before.Lines, l => l.Vertical);
        Assert.Equal(0, plain.VerticalSessionsCreated);
        Assert.Equal(1, ocr.VerticalSessionsCreated); // two recognitions, one session
        // the second session is about 80 MB; far above 300 MB would mean it is created per column or never freed
        Assert.InRange(m1.WorkingSet - m0.WorkingSet, -50, 300);
    }
}
