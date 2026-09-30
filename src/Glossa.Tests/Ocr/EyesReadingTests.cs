using System.Runtime.CompilerServices;
using Glossa.Core.Llm;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Ocr;

public class EyesReadingTests
{
    // A lookup at (1000, 500): the eyes' piece is 700..1300 x 410..590 (600 x 180); boxes are 0-1000 of it, [x1, y1, x2, y2].
    private const double X = 1000, Y = 500;
    private const string Above = """{"text": "TOP", "bbox_2d": [100, 0, 900, 150]}""";          // y 410..437
    private const string Under = """{"text": "OPTI0NS", "bbox_2d": [400, 400, 600, 600]}""";    // 940..1060 x 482..518
    private const string Below = """{"text": "EXIT", "bbox_2d": [100, 800, 900, 950]}""";        // y 554..581

    /// <summary>The eyes as a script: pass 1 streams <paramref name="lines"/> part by part, pass 2 answers <paramref name="line"/>.</summary>
    private sealed class FakeEyes(IReadOnlyList<string> lines, string line, Action<int>? onPart = null) : ILlmClient
    {
        public LlmEndpoint Endpoint { get; } = new("eyes", LlmProviderKind.LlamaServer, "http://127.0.0.1:1/v1", "eyes");
        public List<LlmRequest> Requests { get; } = [];
        public int LineParts { get; private set; }
        public bool LinesLeftEarly { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (request.JsonSchema is null)
            {
                yield return line;
                yield break;
            }
            var done = false;
            try
            {
                foreach (var part in lines)
                {
                    LineParts++;
                    yield return part;
                    onPart?.Invoke(LineParts);
                    ct.ThrowIfCancellationRequested();
                }
                done = true;
            }
            finally
            {
                LinesLeftEarly = !done;
            }
        }
    }

    /// <summary>Cuts a piece from a plain grey frame of the given size, clamped at its edges as the app's frames are.</summary>
    private static Func<PixelRect, (byte[] Bgra, int Width, int Height, int Stride, PixelRect Region)> Frame(int width = 1920, int height = 1080) => r =>
    {
        var region = new PixelRect(Math.Max(0, r.Left), Math.Max(0, r.Top), Math.Min(width, r.Right), Math.Min(height, r.Bottom));
        var (w, h) = ((int)Math.Max(0, region.Width), (int)Math.Max(0, region.Height));
        return (Enumerable.Repeat((byte)128, Math.Max(1, w * h * 4)).ToArray(), w, h, w * 4, region);
    };

    private static readonly PointWords Plain = new(p => p, (p, x, y) => new HitTester().Hit(p, x, y, null));

    private static string Json(params string[] items) => "[" + string.Join(", ", items) + "]";

    private static Task<EyesResult?> Read(FakeEyes eyes, string? forced = "en", ReadingMemory? memory = null, double x = X, double y = Y,
        Func<PixelRect, (byte[] Bgra, int Width, int Height, int Stride, PixelRect Region)>? crop = null, CancellationToken ct = default) =>
        EyesReading.ReadPointAsync(eyes, crop ?? Frame(), x, y, OcrPage.Empty(default), Plain, forced, memory, null, ct);

    [Fact]
    public void A_line_is_near_the_point_within_a_third_of_its_height()
    {
        var box = new PixelRect(100, 100, 200, 130); // 30 high: 10 around
        Assert.True(EyesReading.Near(box, 95, 125));
        Assert.True(EyesReading.Near(box, 150, 139));
        Assert.False(EyesReading.Near(box, 150, 141));
        Assert.False(EyesReading.Near(box, 89, 115));
    }

    [Fact]
    public void The_reading_is_decided_by_a_line_at_the_point_or_one_wholly_below_it()
    {
        var piece = VisionReading.PointPiece(X, Y);
        Assert.False(EyesReading.Decided(VisionReading.LinesPage(Json(Above), piece)!, X, Y));
        Assert.True(EyesReading.Decided(VisionReading.LinesPage(Json(Above, Under), piece)!, X, Y));
        Assert.True(EyesReading.Decided(VisionReading.LinesPage(Json(Above, Below), piece)!, X, Y));
    }

    [Fact]
    public void The_line_under_the_point_is_the_near_one_closest_by_its_middle()
    {
        var piece = VisionReading.PointPiece(X, Y);
        var off = """{"text": "OFF", "bbox_2d": [400, 300, 600, 500]}""";   // 464..500: near, but its middle is 18 px away
        Assert.Equal("OPTI0NS", EyesReading.LineUnder(VisionReading.LinesPage(Json(off, Under), piece)!, X, Y)!.Text);
        Assert.Null(EyesReading.LineUnder(VisionReading.LinesPage(Json(Above, Below), piece)!, X, Y));
    }

    [Theory]
    [InlineData("オプション", "ja")]
    [InlineData("回想モード", "ja")]
    [InlineData("OPTIONS", "en")]
    [InlineData("设置", null)]
    [InlineData("", null)]
    public void The_language_to_name_follows_the_script(string text, string? language)
    {
        var page = text.Length == 0 ? OcrPage.Empty(default) : EyesReading.OnePage(text, new PixelRect(0, 0, 200, 30));
        Assert.Equal(language, EyesReading.ScriptLanguage(page));
    }

    [Fact]
    public void The_second_look_names_the_language_or_asks_plainly()
    {
        Assert.Contains("Japanese", EyesReading.LinePromptFor("ja"));
        Assert.Contains("Chinese", EyesReading.LinePromptFor("zh"));
        Assert.Contains("English", EyesReading.LinePromptFor("en"));
        Assert.Equal(EyesReading.LinePrompt, EyesReading.LinePromptFor(null));
    }

    [Fact]
    public void A_reading_too_long_for_the_line_does_not_fit()
    {
        var button = new PixelRect(0, 0, 120, 30);
        Assert.True(EyesReading.Fits("オプション", button));
        Assert.True(EyesReading.Fits("LOAD GAME", button));
        Assert.False(EyesReading.Fits("I'm sorry, the image is too blurry to read any text.", button));
        Assert.False(EyesReading.Fits("You're not supposed to be here.", new PixelRect(0, 0, 68, 45)));
    }

    [Fact]
    public void The_line_is_cut_with_side_margins_of_1_2_heights_and_0_35_above_and_below()
    {
        var region = EyesReading.LineRegion(new PixelRect(100, 200, 200, 220), new EyesOptions());
        Assert.Equal(76, region.Left, 6);
        Assert.Equal(193, region.Top, 6);
        Assert.Equal(224, region.Right, 6);
        Assert.Equal(227, region.Bottom, 6);
    }

    [Fact]
    public void The_line_is_enlarged_to_the_target_height_and_never_shrunk()
    {
        Assert.Equal(4.8, EyesReading.LineScale(20, 96), 3);
        Assert.Equal(1, EyesReading.LineScale(120, 96));
    }

    [Fact]
    public void Enlarging_multiplies_both_sides()
    {
        var png = EyesReading.Enlarge(Frame()(new PixelRect(0, 0, 60, 20)), 2);
        using var image = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((120, 40), (image.Width, image.Height));
    }

    [Fact]
    public void A_line_read_alone_gives_the_word_under_the_point()
    {
        var page = EyesReading.OnePage("LOAD GAME", new PixelRect(0, 0, 180, 20));
        Assert.Equal("GAME", new HitTester().Hit(page, 150, 10, null)!.Word);
    }

    [Fact]
    public async Task The_first_look_stops_as_soon_as_the_line_under_the_point_has_come()
    {
        var eyes = new FakeEyes(["[" + Above, ", " + Under, ", " + Below, "]"], "OPTIONS");
        var result = await Read(eyes);
        Assert.Equal(2, eyes.LineParts);
        Assert.True(eyes.LinesLeftEarly);
        Assert.Equal("OPTIONS", result!.Hit!.Word);
        Assert.True(result.Direct);
    }

    [Fact]
    public async Task The_first_look_stops_at_a_line_wholly_below_the_point()
    {
        var eyes = new FakeEyes(["[" + Above, ", " + Below, ", " + Above, "]"], "unused");
        var result = await Read(eyes);
        Assert.Equal(2, eyes.LineParts);
        Assert.Null(result!.Hit);
        Assert.Single(eyes.Requests); // no line under the point: no second look
    }

    [Fact]
    public async Task A_line_only_near_the_point_by_its_margin_does_not_stop_the_first_look()
    {
        // Menu items a few pixels apart, the point in the gap: the first item reaches it by its margin only, the second
        // is the one under it - the look goes on until a line lies wholly below.
        var first = """{"text": "ITEM", "bbox_2d": [400, 333, 600, 478]}""";   // y 470..496
        var second = """{"text": "OPTIONS", "bbox_2d": [400, 517, 600, 661]}"""; // y 503..529
        var third = """{"text": "EXIT", "bbox_2d": [400, 722, 600, 867]}""";    // y 540..566
        var eyes = new FakeEyes(["[" + first, ", " + second, ", " + third, "]"], "OPTIONS");
        var result = await Read(eyes);
        Assert.Equal(3, eyes.LineParts);
        Assert.Equal(503, result!.Line!.Value.Top, 0);
        Assert.Equal("OPTIONS", result.Hit!.Word);
    }

    [Fact]
    public async Task A_newer_lookup_cancelling_is_not_taken_for_the_early_stop()
    {
        using var outer = new CancellationTokenSource();
        var eyes = new FakeEyes(["[" + Above, ", " + Above, ", " + Under, "]"], "OPTIONS", n => { if (n == 1) outer.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(eyes, ct: outer.Token));
    }

    [Fact]
    public async Task A_first_look_that_is_not_the_lines_json_is_a_failed_reading()
    {
        Assert.Null(await Read(new FakeEyes(["I cannot see any text."], "unused")));
    }

    [Fact]
    public async Task A_point_off_the_frame_asks_nothing()
    {
        var eyes = new FakeEyes([Json(Under)], "OPTIONS");
        Assert.Null(await Read(eyes, x: 5000, y: 5000));
        Assert.Empty(eyes.Requests);
    }

    [Fact]
    public async Task No_line_near_the_point_is_no_text_there()
    {
        var eyes = new FakeEyes([Json(Above)], "unused");
        var result = await Read(eyes);
        Assert.Null(result!.Hit);
        Assert.False(result.Direct);
        Assert.Single(result.Page.Lines);
        Assert.Single(eyes.Requests);
    }

    [Fact]
    public async Task The_second_look_reads_the_line_alone_with_its_language_named()
    {
        var kana = """{"text": "オフション", "bbox_2d": [400, 400, 600, 600]}""";
        var eyes = new FakeEyes([Json(kana)], "オプション");
        var result = await Read(eyes, forced: null);
        Assert.Equal(2, eyes.Requests.Count);
        Assert.Contains("Japanese", eyes.Requests[1].Messages[0].Content);
        Assert.Equal("ja", result!.Language);
        // The line's 36 px enlarged to 96, its margins (0.35 of the height above and below) with it: about 163.
        using var line = SkiaSharp.SKBitmap.Decode(eyes.Requests[1].Messages[0].Image!);
        Assert.InRange(line.Height, 158, 166);
    }

    [Fact]
    public async Task The_game_language_wins_over_the_script()
    {
        var kana = """{"text": "オフション", "bbox_2d": [400, 400, 600, 600]}""";
        var eyes = new FakeEyes([Json(kana)], "OPTIONS");
        var result = await Read(eyes, forced: "en");
        Assert.Contains("English", eyes.Requests[1].Messages[0].Content);
        Assert.Equal("en", result!.Language);
    }

    [Fact]
    public async Task A_second_reading_that_cannot_be_the_line_is_dropped()
    {
        var eyes = new FakeEyes([Json(Under)], "I'm sorry, but the image is too blurry for me to read any of the text.");
        var result = await Read(eyes);
        Assert.Null(result!.Hit);
        Assert.False(result.Direct);
        Assert.Contains(result.Page.Lines, l => l.Text == "OPTI0NS"); // the first look's lines stay for the caller
    }

    [Fact]
    public async Task The_word_read_the_second_time_is_taken_at_the_point_pulled_into_the_line()
    {
        var eyes = new FakeEyes([Json(Under)], "OPTIONS");
        var result = await Read(eyes, y: 471); // above the line's box (482..518), still near it
        Assert.True(result!.Direct);
        Assert.Equal("OPTIONS", result.Hit!.Word);
        Assert.Equal("OPTIONS", result.Reading);
    }

    [Fact]
    public async Task A_first_look_already_read_is_not_asked_again()
    {
        var memory = new ReadingMemory();
        var eyes = new FakeEyes([Json(Under)], "OPTIONS");
        await Read(eyes, memory: memory);
        await Read(eyes, memory: memory);
        Assert.Equal(3, eyes.Requests.Count); // one first look asked, one remembered; two second looks
        Assert.Equal(1, eyes.Requests.Count(r => r.JsonSchema is not null));
    }

    [Fact]
    public async Task A_remembered_first_look_is_for_the_same_point_in_the_piece()
    {
        // In a 500 px wide frame the piece is the whole width for any point from 200 to 300: the same picture.
        var memory = new ReadingMemory();
        var eyes = new FakeEyes([Json(Under)], "OPTIONS");
        await Read(eyes, memory: memory, x: 220, crop: Frame(500, 1080));
        await Read(eyes, memory: memory, x: 280, crop: Frame(500, 1080));
        Assert.Equal(2, eyes.Requests.Count(r => r.JsonSchema is not null));
    }
}
