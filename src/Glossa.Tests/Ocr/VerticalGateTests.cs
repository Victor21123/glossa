using Glossa.Core.Ocr;
using SkiaSharp;
using Xunit.Abstractions;

namespace Glossa.Tests.Ocr;

/// <summary>
/// The vertical path must cost nothing where there is no vertical Japanese or Chinese: no second session, no reads thrown
/// away. Gates: the language of the lookup, the content of the page, the geometry of the box. And the horizontal result
/// of a page is the same with vertical reading on and off.
/// </summary>
public class VerticalGateTests(ITestOutputHelper output)
{
    private const string ModelsDir = @"D:\GlossaData\models\ocr";
    private const string Frames = @"D:\GlossaData\test\tategaki";
    private const string Screens = @"D:\GlossaData\test\screens";

    private static PixelRect R(double l, double t, double r, double b) => new(l, t, r, b);

    private static OcrLine Tall(PixelRect box, string text, float score) =>
        new(text, box, text.Length == 0 ? [] : [new OcrWord(text, box, score)], score);

    private static OcrLine Row(string text, double left, double top, float score = 0.95f) =>
        new(text, R(left, top, left + 20 * text.Length, top + 30), [new OcrWord(text, R(left, top, left + 20 * text.Length, top + 30), score)], score);

    private sealed class Reader(string? text = "今日はいい天気ですね")
    {
        public int Reads;
        public PixelRect LastBox;

        public VerticalRead? Read(PixelRect box)
        {
            Reads++;
            LastBox = box;
            if (text is null) return null;
            var step = box.Height / text.Length;
            return new VerticalRead(text, text.Select((c, i) => new OcrWord(c.ToString(), R(box.Left, box.Top + i * step, box.Right, box.Top + (i + 1) * step), 0.9f)).ToList(), 0.9f);
        }
    }

    // ---------------------------------------------------------------- language

    [Theory]
    [InlineData(null, true)]
    [InlineData("auto", true)]
    [InlineData("ja", true)]
    [InlineData("zh", true)]
    [InlineData("en", false)]
    [InlineData("ru", false)]
    public void Columns_are_read_for_japanese_chinese_and_unknown_languages_only(string? language, bool allowed) =>
        Assert.Equal(allowed, OcrEngine.ColumnsAllowed(language));

    // ---------------------------------------------------------------- content

    [Fact]
    public void A_confidently_read_Latin_or_digit_box_is_never_a_column()
    {
        var reader = new Reader();
        var lines = new[] { Tall(R(100, 100, 120, 200), "I", 0.95f), Tall(R(200, 100, 230, 400), "12345", 0.9f), Row("ひらがな", 400, 600) };

        var result = VerticalColumns.Apply(lines, reader.Read, 0.5f);

        Assert.Equal(0, reader.Reads);
        Assert.Equal(lines, result);
    }

    [Fact]
    public void What_the_clockwise_read_makes_of_a_column_is_read_even_when_it_looks_sure()
    {
        var reader = new Reader();
        var page = Row("ひらがな", 600, 700);

        // "G27" at 0.63 and punctuation at 0.80 are what the library made of real columns (book frame, moan frame)
        VerticalColumns.Apply([Tall(R(1483, 795, 1531, 983), "G27", 0.63f), page], reader.Read, 0.5f);
        VerticalColumns.Apply([Tall(R(867, 148, 933, 603), "…、", 0.80f), page], reader.Read, 0.5f);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public void Pieces_of_one_column_are_read_together_when_one_of_them_could_be_a_column()
    {
        var reader = new Reader();
        var upper = Tall(R(1315, 193, 1374, 362), "、Λ", 0.58f);
        var lower = Tall(R(1313, 334, 1374, 618), "ツ", 0.3f);

        var result = VerticalColumns.Apply([upper, lower, Row("ひらがな", 100, 800)], reader.Read, 0.5f);

        Assert.Equal(1, reader.Reads);
        Assert.Equal(R(1313, 193, 1374, 618), reader.LastBox);
        Assert.DoesNotContain(upper, result);
    }

    [Fact]
    public void A_column_read_in_this_pass_is_evidence_for_the_boxes_beside_it()
    {
        var reader = new Reader();

        // nothing on the page is Japanese until the first box reads as a column (moan frame: "…、" beside a column)
        var result = VerticalColumns.Apply([Tall(R(867, 148, 933, 603), "…、", 0.8f), Tall(R(796, 146, 865, 726), "ツ晕、M判", 0.3f)], reader.Read, 0.5f);

        Assert.Equal(2, reader.Reads);
        Assert.Equal(2, result.Count(l => l.Vertical));
    }

    [Fact]
    public void One_stray_glyph_on_an_English_page_does_not_make_a_tall_box_a_candidate()
    {
        var reader = new Reader();

        VerticalColumns.Apply([Tall(R(100, 100, 150, 500), "", 0f), Row("Hello there", 300, 600), Row("口", 700, 100)], reader.Read, 0.5f);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public void A_garbage_box_with_a_single_Han_in_its_own_text_is_not_enough_either()
    {
        var reader = new Reader();

        VerticalColumns.Apply([Tall(R(100, 100, 150, 500), "口", 0.3f), Row("Hello there", 300, 600)], reader.Read, 0.5f);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public void The_region_does_not_grow_for_a_tall_box_that_is_not_a_vertical_line()
    {
        var roi = LookupRegion.Around(1000, 500);
        var tall = new OcrLine("目目目", new PixelRect(990, roi.Top, 1040, roi.Top + 200), [new OcrWord("目目目", new PixelRect(990, roi.Top, 1040, roi.Top + 200), 0.9f)], 0.9f);
        var column = tall with { Vertical = true };

        Assert.False(LookupRegion.NeedsTaller(new OcrPage([tall], roi, TimeSpan.Zero), roi, new PixelRect(0, 0, 1920, 1080), 1000));
        Assert.True(LookupRegion.NeedsTaller(new OcrPage([column], roi, TimeSpan.Zero), roi, new PixelRect(0, 0, 1920, 1080), 1000));
    }

    [Fact]
    public async Task An_English_page_with_one_stray_Han_glyph_never_creates_the_vertical_session()
    {
        if (!Ready()) return;
        using var bmp = new SKBitmap(new SKImageInfo(1400, 800, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(30, 34, 48));
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 34);
            using var cjk = new SKFont(SKTypeface.FromFamilyName("Yu Gothic"), 40);
            using var paint = new SKPaint { Color = new SKColor(235, 235, 240), IsAntialias = true };
            canvas.DrawText("The old harbor master looked at the broken wheel", 60, 90, font, paint);
            canvas.DrawText("口", 1200, 90, cjk, paint);
            canvas.DrawRect(1300, 100, 14, 600, paint);
            canvas.DrawRect(1250, 200, 20, 120, paint);
        }
        using var ocr = new OcrEngine(ModelsDir);

        var page = await Read(ocr, (bmp.Bytes, bmp.Width, bmp.Height, bmp.RowBytes)); // language auto

        Assert.Equal(0, ocr.VerticalSessionsCreated);
        Assert.DoesNotContain(page.Lines, l => l.Vertical);
    }

    [Fact]
    public void A_blank_tall_box_on_a_page_without_Japanese_or_Chinese_is_not_read()
    {
        var reader = new Reader();

        VerticalColumns.Apply([Tall(R(100, 100, 120, 400), "", 0f), Row("Hello there, stranger", 300, 600)], reader.Read, 0.5f);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public void A_blank_tall_box_on_a_page_with_Japanese_is_read()
    {
        var reader = new Reader();

        var result = VerticalColumns.Apply([Tall(R(100, 100, 150, 500), "", 0f), Row("ひらがな", 300, 600)], reader.Read, 0.5f);

        Assert.Equal(1, reader.Reads);
        Assert.Contains(result, l => l.Vertical);
    }

    [Fact]
    public void A_garbage_tall_box_whose_own_text_has_Han_is_read_even_on_a_page_without_other_text()
    {
        var reader = new Reader();

        VerticalColumns.Apply([Tall(R(100, 100, 150, 500), "ツ晕、M判", 0.4f)], reader.Read, 0.5f);

        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void A_shredded_column_needs_Japanese_or_Chinese_on_the_page()
    {
        var digits = Enumerable.Range(0, 5).Select(i => Tall(R(500, 100 + i * 60, 540, 140 + i * 60), (i * 2 + 3).ToString(), 0.3f)).ToList();
        Assert.True(VerticalColumns.LooksShredded(digits)); // the shape and poor reads say so ...
        Assert.False(VerticalColumns.PageHasCjk(digits)); // ... the content does not

        Assert.True(VerticalColumns.PageHasCjk([.. digits, Row("名前", 100, 100)]));
        Assert.False(VerticalColumns.PageHasCjk([Tall(R(0, 0, 40, 40), "ツ", 0.1f)])); // garbage is not evidence
        // one stray glyph (an icon read as 口) is not a page of Japanese; two characters read with confidence are
        Assert.False(VerticalColumns.PageHasCjk([Row("Hello", 0, 0), Row("口", 100, 100)]));
        Assert.False(VerticalColumns.PageHasCjk([Row("口", 0, 0), Row("目", 100, 100, 0.3f)])); // the second is not trusted
        Assert.True(VerticalColumns.PageHasCjk([Row("口", 0, 0), Row("目", 100, 100)]));
        Assert.False(VerticalColumns.PageHasCjk([Row("Hello", 0, 0), Tall(R(0, 0, 40, 40), "", 0f)]));
    }

    // ---------------------------------------------------------------- geometry

    [Fact]
    public void A_box_needs_two_and_a_half_cells_of_height_and_not_forty()
    {
        var reader = new Reader();
        var page = Row("ひらがな", 600, 700);

        VerticalColumns.Apply([Tall(R(100, 100, 160, 249), "", 0f), page], reader.Read, 0.5f); // 2.48
        Assert.Equal(0, reader.Reads);
        VerticalColumns.Apply([Tall(R(100, 100, 160, 250), "", 0f), page], reader.Read, 0.5f); // 2.5
        Assert.Equal(1, reader.Reads);
        VerticalColumns.Apply([Tall(R(100, 100, 110, 501), "", 0f), page], reader.Read, 0.5f); // 40.1: a scrollbar
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void A_long_column_is_read_in_chunks_that_fit_the_model_input()
    {
        var chunks = VerticalReader.Chunks(length: 8000, width: 50);

        Assert.True(chunks.Count > 1);
        Assert.Equal(0, chunks[0].From);
        Assert.Equal(8000, chunks[^1].To, 6);
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].To, chunks[i].From, 6);
        // each chunk, scaled to the model height of 48, is within the width PaddleOCR caps recognition at
        Assert.All(chunks, c => Assert.True((c.To - c.From) * 48 / 50 <= VerticalReader.MaxInputWidth + 1e-6));
        Assert.Single(VerticalReader.Chunks(length: 600, width: 60));
    }

    // ---------------------------------------------------------------- second look must not read twice

    [Fact]
    public void A_group_that_failed_in_the_first_look_is_not_read_again()
    {
        var failed = Tall(R(100, 100, 150, 500), "ツ晕、M判", 0.3f);
        var reader = new Reader(null);
        var tried = new HashSet<OcrLine>(ReferenceEqualityComparer.Instance);

        VerticalColumns.Apply([failed], reader.Read, 0.5f, tried);
        VerticalColumns.Apply([failed], reader.Read, 0.5f, tried);

        Assert.Equal(1, reader.Reads);
        Assert.Contains(failed, tried);
    }

    // ---------------------------------------------------------------- what a column swallows

    [Fact]
    public void A_real_horizontal_label_over_the_column_area_is_kept_a_glyph_piece_is_dropped()
    {
        var column = Tall(R(1471, 191, 1529, 680), "ツ晕、M判", 0.3f);
        var label = new OcrLine("ドンッ", R(1472, 300, 1528, 340), [new OcrWord("ドンッ", R(1472, 300, 1528, 340), 0.9f)], 0.9f);
        var glyph = Tall(R(1480, 400, 1520, 440), "十", 0.84f);
        var junk = Tall(R(1480, 500, 1520, 540), "X", 0.2f);

        var result = VerticalColumns.Apply([column, label, glyph, junk], new Reader().Read, 0.5f);

        Assert.Contains(label, result);
        Assert.DoesNotContain(glyph, result); // one glyph, cell sized: a piece of the column
        Assert.DoesNotContain(junk, result);
    }

    [Fact]
    public void A_stray_glyph_a_little_taller_than_wide_is_still_a_piece_of_the_column()
    {
        var column = Tall(R(1672, 115, 1726, 714), "ツ晕、M判", 0.3f);
        var stray = Tall(R(1683, 692, 1705, 728), "1", 0.66f); // 22 x 36: the book frame's "1"

        Assert.DoesNotContain(stray, VerticalColumns.Apply([column, stray], new Reader().Read, 0.5f));
    }

    [Fact]
    public void A_big_round_box_under_a_column_is_not_its_period()
    {
        var column = Tall(R(1471, 191, 1529, 680), "ツ晕、M判", 0.3f);
        var circle = Tall(R(1480, 690, 1520, 760), "O", 0.5f); // 40 x 70 in a 58 cell: a letter, not a full stop

        var result = VerticalColumns.Apply([column, circle], new Reader().Read, 0.5f);

        Assert.Contains(circle, result);
    }

    // ---------------------------------------------------------------- decoding edges

    [Fact]
    public void The_dictionary_is_read_with_either_line_ending_and_blank_lines_keep_their_place()
    {
        var path = Path.Combine(Path.GetTempPath(), "vr-dict-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "あ\r\n\r\nい\r\nう\r\n");
            Assert.Equal(["あ", "", "い", "う"], VerticalReader.ReadDictionary(path));
            File.WriteAllText(path, "あ\nい\nう");
            Assert.Equal(["あ", "い", "う"], VerticalReader.ReadDictionary(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Decode_maps_the_last_class_to_the_space_and_an_empty_dictionary_entry_to_nothing()
    {
        // classes: blank, あ, "" (blank line), space
        var logits = new float[] { 0.1f, 0.9f, 0, 0, /**/ 0.1f, 0, 0.9f, 0, /**/ 0, 0, 0, 0.9f };

        var read = VerticalReader.Decode(logits, 3, 4, ["あ", ""]);

        Assert.Equal(["あ", "", " "], read.Select(c => c.Text));
    }

    [Fact]
    public void Spans_of_characters_past_the_bottom_keep_a_height_and_stay_inside()
    {
        // twelve characters whose timesteps all lie beyond the column: every centre is clamped to the bottom
        var chars = Enumerable.Range(0, 12).Select(i => new CtcChar("あ", 100 + i, 100 + i, 0.9f)).ToList();

        var spans = VerticalReader.Spans(chars, stepLength: 10, origin: 0, top: 0, bottom: 120);

        Assert.All(spans, s => Assert.True(s.Bottom - s.Top > 1, $"{s.Top}..{s.Bottom}"));
        Assert.Equal(0, spans[0].Top);
        Assert.Equal(120, spans[^1].Bottom);
        for (var i = 1; i < spans.Count; i++) Assert.Equal(spans[i - 1].Bottom, spans[i].Top, 6);
    }

    [Fact]
    public void Spans_clamp_at_both_edges_of_the_frame()
    {
        var chars = new List<CtcChar> { new("あ", 0, 0, 1), new("い", 1, 1, 1), new("う", 500, 500, 1) };

        var spans = VerticalReader.Spans(chars, stepLength: 8, origin: -50, top: 10, bottom: 90);

        Assert.All(spans, s => { Assert.InRange(s.Top, 10, 90); Assert.InRange(s.Bottom, 10, 90); Assert.True(s.Bottom > s.Top); });
        Assert.Equal(10, spans[0].Top);
        Assert.Equal(90, spans[^1].Bottom);
    }

    // ---------------------------------------------------------------- real models

    private static bool Ready() =>
        OcrEngine.ModelsPresent(ModelsDir) && File.Exists(Path.Combine(ModelsDir, "v6", "PP-OCRv6_rec_medium.onnx"))
        && File.Exists(Path.Combine(ModelsDir, "v6", "ppocrv6_small_dict.txt")) && Directory.Exists(Frames) && Directory.Exists(Screens);

    private static (byte[] Bgra, int W, int H, int Stride) Load(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException(path);
        using var bmp = decoded.Copy(SKColorType.Bgra8888);
        return (bmp.Bytes, bmp.Width, bmp.Height, bmp.RowBytes);
    }

    private static Task<OcrPage> Read(OcrEngine ocr, (byte[] Bgra, int W, int H, int Stride) img, string? language = null) =>
        ocr.RecognizeAsync(img.Bgra, img.W, img.H, img.Stride, new PixelRect(0, 0, img.W, img.H), OcrModelFamily.CjkLatin, CancellationToken.None, language: language);

    /// <summary>English text, a lone I and bars of every height, a column of digits: tall boxes, none of them Japanese.</summary>
    private static (byte[] Bgra, int W, int H, int Stride) LatinPage()
    {
        using var bmp = new SKBitmap(new SKImageInfo(1400, 800, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(30, 34, 48));
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 34);
            using var big = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 110);
            using var paint = new SKPaint { Color = new SKColor(235, 235, 240), IsAntialias = true };
            canvas.DrawText("The old harbor master looked at the broken wheel", 60, 90, font, paint);
            canvas.DrawText("and said it was a mistake to sail tonight.", 60, 140, font, paint);
            canvas.DrawText("I", 100, 400, big, paint);
            canvas.DrawText("|", 200, 400, big, paint);
            canvas.DrawRect(1300, 100, 14, 600, paint); // a scrollbar
            for (var i = 0; i < 7; i++) canvas.DrawText((3 + i * 2).ToString(), 600, 250 + i * 50, font, paint); // a column of numbers
        }
        return (bmp.Bytes, bmp.Width, bmp.Height, bmp.RowBytes);
    }

    [Fact]
    public async Task A_Latin_page_with_tall_boxes_never_creates_the_vertical_session_and_reads_as_without_it()
    {
        if (!Ready()) return;
        var pages = new[] { LatinPage(), Load(Path.Combine(Frames, "en_word_list.png")) };
        using var on = new OcrEngine(ModelsDir);
        using var off = new OcrEngine(ModelsDir) { VerticalReading = false };

        foreach (var img in pages)
        {
            var a = await Read(on, img);
            var b = await Read(off, img);
            foreach (var l in a.Lines) output.WriteLine($"{l.Box} {l.Text}");
            Assert.Equal(b.Lines.Select(l => (l.Text, l.Box)), a.Lines.Select(l => (l.Text, l.Box)));
        }

        Assert.Equal(0, on.VerticalSessionsCreated);
    }

    [Fact]
    public async Task An_English_lookup_never_reads_columns_a_Japanese_one_does()
    {
        if (!Ready()) return;
        var img = Load(Path.Combine(Frames, "ja_vert_vn.png"));
        using var ocr = new OcrEngine(ModelsDir);

        var en = await Read(ocr, img, "en");
        Assert.DoesNotContain(en.Lines, l => l.Vertical);
        Assert.Equal(0, ocr.VerticalSessionsCreated);

        var ja = await Read(ocr, img, "ja");
        Assert.Equal(3, ja.Lines.Count(l => l.Vertical));
        Assert.Equal(1, ocr.VerticalSessionsCreated);
        await Read(ocr, img, "ja");
        Assert.Equal(1, ocr.VerticalSessionsCreated); // one session for all the reads
    }

    [Fact]
    public async Task Horizontal_frames_read_the_same_with_vertical_reading_on_and_off()
    {
        if (!Ready()) return;
        // zh_taleofimmortal_00 has real vertical text (that is the intended difference); every other frame has none
        var files = Directory.GetFiles(Screens, "*.jpg").Where(f => !f.Contains("taleofimmortal")).OrderBy(f => f).ToList();
        Assert.NotEmpty(files);
        using var on = new OcrEngine(ModelsDir);
        using var off = new OcrEngine(ModelsDir) { VerticalReading = false };

        foreach (var file in files)
        {
            var img = Load(file);
            var a = await Read(on, img);
            var b = await Read(off, img);
            Assert.True(b.Lines.Select(l => (l.Text, l.Box, l.Score)).SequenceEqual(a.Lines.Select(l => (l.Text, l.Box, l.Score))),
                Path.GetFileName(file));
            Assert.DoesNotContain(a.Lines, l => l.Vertical);
        }
        output.WriteLine($"{files.Count} frames identical, vertical sessions created: {on.VerticalSessionsCreated}");
    }
}
