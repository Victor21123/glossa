using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class TextBlocksTests
{
    private static OcrLine Line(string text, double left, double top, double height = 20) =>
        new(text, new PixelRect(left, top, left + text.Length * 10, top + height),
            [new OcrWord(text, new PixelRect(left, top, left + text.Length * 10, top + height), 1)], 1);

    private static OcrPage Page(params OcrLine[] lines) => new(lines, new PixelRect(0, 0, 1920, 1080), TimeSpan.Zero);

    // A HUD label on top, a two-line dialogue low on the screen, the answers below it.
    private static readonly OcrPage Dialogue = Page(
        Line("Day 3", 1700, 40),
        Line("I'm like three or maybe four years into mine.", 400, 800),
        Line("Wait no, make it five.", 400, 826),
        Line("1. What do you guys do around here?", 400, 900, 26));

    [Fact]
    public void Lines_of_one_paragraph_become_one_block()
    {
        var blocks = TextBlocks.Of(Dialogue);
        Assert.Equal(3, blocks.Count);
        Assert.Equal("I'm like three or maybe four years into mine. Wait no, make it five.", blocks[1].Text);
        Assert.Equal(2, blocks[1].Lines);
    }

    [Fact]
    public void A_zone_is_its_paragraphs_one_per_line() =>
        Assert.Equal("Day 3\nI'm like three or maybe four years into mine. Wait no, make it five.\n1. What do you guys do around here?",
            TextBlocks.Joined(Dialogue));

    [Fact]
    public void Two_pieces_of_one_row_do_not_put_the_lines_around_them_in_twice()
    {
        // The recognizer cut a line in two ("from" | "ching strong"): both pieces sit under the line above and over the
        // one below, and the paragraph used to be built from each of them, the outer lines twice.
        var page = Page(
            Line("In very rare cases, some people may", 400, 100),
            Line("symptoms from", 400, 124),
            Line("ching strong light", 560, 124),
            Line("lights, or the TV screen.", 450, 148));

        var text = TextBlocks.Joined(page);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "rare cases"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "TV screen"));
    }

    [Fact]
    public void A_zone_keeps_the_lines_and_words_inside_it()
    {
        var words = new List<OcrWord>
        {
            new("Special", new PixelRect(0, 0, 70, 20), 1), new("Sensation", new PixelRect(80, 0, 170, 20), 1),
            new("Oil", new PixelRect(180, 0, 210, 20), 1),
        };
        var page = Page(new OcrLine("Special Sensation Oil", new PixelRect(0, 0, 210, 20), words, 1), Line("Menu", 0, 300));

        var inside = page.Within(new PixelRect(75, -5, 260, 40));

        var line = Assert.Single(inside.Lines);
        Assert.Equal("Sensation Oil", line.Text);
        Assert.Equal(["Sensation", "Oil"], line.Words.Select(w => w.Text));
    }

    [Fact]
    public void A_low_zone_is_read_within_the_lookups_height_of_screen()
    {
        var around = Zones.Around(new PixelRect(730, 570, 1800, 750));
        Assert.Equal(Zones.Reach, around.Height);
        Assert.Equal(new PixelRect(730, 420, 1800, 900), around);
        var big = new PixelRect(0, 0, 1000, 800);
        Assert.Equal(big, Zones.Around(big));
    }

    [Fact]
    public void The_block_under_the_cursor_is_its_whole_paragraph()
    {
        Assert.Equal(TextBlocks.Of(Dialogue)[1], TextBlocks.At(Dialogue, 500, 835));
        Assert.Null(TextBlocks.At(Dialogue, 100, 500)); // nothing close enough
    }

    [Fact]
    public void The_dialogue_is_the_lowest_block_with_real_text()
    {
        var blocks = TextBlocks.Of(Dialogue);
        Assert.Equal("1. What do you guys do around here?", TextBlocks.Dialogue(blocks)!.Text);
        Assert.Equal("I'm like three or maybe four years into mine. Wait no, make it five.", TextBlocks.Dialogue(blocks.Take(2).ToList())!.Text);
    }

    [Theory]
    [InlineData("Wait no, make it five.", "ru", true)]
    [InlineData("合体していく", "ru", true)]
    [InlineData("上", "ru", false)]          // one character of a HUD
    [InlineData("160/160", "ru", false)]     // numbers only
    [InlineData("Day 1 1000.00", "ru", false)] // the interface's counters
    [InlineData("不40.9516:36Day3", "ru", false)]
    [InlineData("Уже по-русски", "ru", false)]
    [InlineData("Уже по-русски", "en", true)] // a Russian game read by a user whose own language is English
    public void Only_foreign_text_worth_reading_is_translated(string text, string target, bool expected) =>
        Assert.Equal(expected, TextBlocks.Translatable(new TextBlock(text, default, 1), target));

    [Theory]
    [InlineData("スライムたちが合体していく", "ja")]
    [InlineData("新技能学完了", "zh")]
    [InlineData("Wait no, make it five.", "en")]
    [InlineData("Уже по-русски", "ru")]
    public void The_language_of_a_block_comes_from_its_script(string text, string expected) =>
        Assert.Equal(expected, Languages.DetectText(text, "zh"));
}

public class LiveTextWatcherTests
{
    private static byte[] Frame(byte shade, int changedCells = 0)
    {
        var print = Enumerable.Repeat(shade, ScreenFingerprint.Width * ScreenFingerprint.Height).ToArray();
        for (var i = 0; i < changedCells; i++) print[i] = (byte)(shade + 40);
        return print;
    }

    [Fact]
    public void Text_is_read_once_the_picture_settles_and_not_again_while_it_stays()
    {
        var watcher = new LiveTextWatcher();
        var t = DateTime.UtcNow;
        Assert.True(watcher.ShouldRecognize(Frame(10), t));                                // first frame
        Assert.False(watcher.ShouldRecognize(Frame(10), t.AddMilliseconds(400)));          // unchanged
        Assert.False(watcher.ShouldRecognize(Frame(10, 30), t.AddMilliseconds(800)));      // a line being typed out
        Assert.True(watcher.ShouldRecognize(Frame(10, 30), t.AddMilliseconds(1200)));      // settled on a new picture
        Assert.False(watcher.ShouldRecognize(Frame(10, 30), t.AddMilliseconds(1600)));
    }

    [Fact]
    public void A_scene_that_never_stops_moving_is_still_read_every_two_seconds()
    {
        var watcher = new LiveTextWatcher();
        var t = DateTime.UtcNow;
        watcher.ShouldRecognize(Frame(10), t);
        var reads = 0;
        for (var i = 1; i <= 10; i++)
            if (watcher.ShouldRecognize(Frame((byte)(10 + i * 10)), t.AddMilliseconds(400 * i))) reads++;
        Assert.Equal(2, reads); // at 2.0 s and 4.0 s
    }

    [Fact]
    public void The_same_line_is_not_translated_twice()
    {
        var watcher = new LiveTextWatcher();
        TextBlock Block(string text) => new(text, new PixelRect(0, 800, 600, 840), 1);
        Assert.NotNull(watcher.NewLine([Block("Wait no, make it five.")], _ => "ru"));
        Assert.Null(watcher.NewLine([Block("Wait, no - make it five!")], _ => "ru")); // same words, other punctuation
        Assert.NotNull(watcher.NewLine([Block("He takes a sip from his beer.")], _ => "ru"));
        Assert.Null(watcher.NewLine([Block("Уже по-русски, ничего не нужно")], _ => "ru"));
    }
}
