using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Ocr;

public class VisionReadingTests
{
    private const double CharWidth = 10;
    private const double LineHeight = 20;

    /// <summary>A recognized line of space-separated words laid out left to right at a fixed pitch.</summary>
    private static OcrLine LatinLine(string text, double top, double left = 0)
    {
        var words = new List<OcrWord>();
        var x = left;
        foreach (var w in text.Split(' '))
        {
            words.Add(new OcrWord(w, new PixelRect(x, top, x + w.Length * CharWidth, top + LineHeight), 0.95f));
            x += (w.Length + 1) * CharWidth;
        }
        return new OcrLine(text, new PixelRect(left, top, x - CharWidth, top + LineHeight), words, 0.95f);
    }

    private static OcrLine CjkLine(string text, double top)
    {
        var units = text.Select((c, i) => new OcrWord(c.ToString(), new PixelRect(i * LineHeight, top, (i + 1) * LineHeight, top + LineHeight), 0.95f)).ToList();
        return new OcrLine(text, new PixelRect(0, top, text.Length * LineHeight, top + LineHeight), units, 0.95f);
    }

    private static OcrPage Page(params OcrLine[] lines) => new(lines, new PixelRect(0, 0, 2000, 1000), TimeSpan.Zero);

    /// <summary>The middle of the <paramref name="n"/>-th character of a Latin line at the fixed pitch.</summary>
    private static double At(int n) => (n + 0.5) * CharWidth;

    /// <summary>A lookup at a point: the word there, corrected by the reading, and the word there afterwards.</summary>
    private static (OcrPage Page, string? Word) Reread(OcrPage page, double x, double y, string reading, ITermMatcher? cjk = null)
    {
        var hit = new HitTester().Hit(page, x, y, cjk)!;
        var fixedPage = VisionReading.Correct(page, hit, reading);
        return (fixedPage, new HitTester().Hit(fixedPage, x, y, cjk)?.Word);
    }

    [Fact]
    public void Misread_word_is_replaced_and_found_under_the_point()
    {
        var (page, word) = Reread(Page(LatinLine("Your experence has been saved", 100)), At(8), 110, "Your experience has been saved");

        Assert.Equal("experience", word);
        Assert.Equal("Your experience has been saved", page.Lines[0].Text);
    }

    [Fact]
    public void Only_the_word_under_the_point_changes()
    {
        // The model's reading is trusted for the doubtful word alone: elsewhere it may be the one that is wrong.
        var (page, _) = Reread(Page(LatinLine("Your experence has ben saved", 100)), At(8), 110, "Your experience has been saved");

        Assert.Equal("Your experience has ben saved", page.Lines[0].Text);
    }

    [Fact]
    public void Word_broken_by_the_cursor_is_joined()
    {
        var (page, word) = Reread(Page(LatinLine("people may exper ence cramps", 100)), At(13), 110, "people may experience cramps");

        Assert.Equal("experience", word);
        Assert.Equal("people may experience cramps", page.Lines[0].Text);
    }

    [Fact]
    public void Letters_the_model_adds_after_the_word_belong_to_it()
    {
        var (_, word) = Reread(Page(LatinLine("change this optio at any time", 100)), At(14), 110, "change this option at any time");
        Assert.Equal("option", word);
    }

    [Fact]
    public void Glued_words_are_split_and_each_keeps_its_own_place()
    {
        // The game's cursor over "LOAD GAME": the recognizer read one word.
        var (page, left) = Reread(Page(LatinLine("LOARGAME", 100)), At(1), 110, "LOAD GAME");

        Assert.Equal("LOAD", left);
        Assert.Equal("LOAD GAME", page.Lines[0].Text);
        Assert.Equal("GAME", new HitTester().Hit(page, At(6), 110)!.Word);
    }

    [Fact]
    public void The_word_takes_the_model_line_that_matches_its_line()
    {
        var page = Page(LatinLine("CoN-IG", 100), LatinLine("optioryat", 140), LatinLine("reverted", 180));
        var (fixedPage, word) = Reread(page, At(2), 150, "reverted\nCONFIG\noption at\n");

        Assert.Equal("option", word);
        Assert.Equal(["CoN-IG", "option at", "reverted"], fixedPage.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Of_two_equally_close_lines_the_one_sharing_more_letters_wins()
    {
        var (page, _) = Reread(Page(LatinLine("Special Sensation OilII", 100)), At(19), 110,
            "Special Sensation Oil I Special Sensation Oil: Moderate\nSpecial Sensation Oil II Special Sensation Oil: Sweaty");
        Assert.Equal("Special Sensation Oil II", page.Lines[0].Text);
    }

    [Fact]
    public void A_letter_dropped_at_the_end_of_the_line_comes_back_in_its_room()
    {
        // "Special Sensation Oil I": the recognizer left out the lone "I", its line box still reaching past "Oil".
        var read = LatinLine("Special Sensation Oil", 100);
        var page = Page(read with { Box = read.Box with { Right = read.Box.Right + 8 } });
        var hit = new HitTester().Hit(page, At(19), 110)!;

        Assert.True(VisionReading.Doubtful(hit, known: true, page));
        var fixedPage = VisionReading.Correct(page, hit, "Special Sensation Oil I Special Sensation Oil: Moderate");

        Assert.Equal("Special Sensation Oil I", fixedPage.Lines[0].Text);
        Assert.Equal("Oil", new HitTester().Hit(fixedPage, At(19), 110)!.Word);
        Assert.Equal("I", new HitTester().Hit(fixedPage, 214, 110)!.Word);
    }

    [Fact]
    public void A_line_box_ending_at_its_last_word_is_not_doubtful()
    {
        var page = Page(LatinLine("Special Sensation Oil", 100));
        Assert.False(VisionReading.Doubtful(new HitTester().Hit(page, At(19), 110)!, known: true, page));
    }

    [Fact]
    public void Reading_unlike_the_line_changes_nothing()
    {
        var page = Page(LatinLine("Morgana smiles", 100));
        var hit = new HitTester().Hit(page, At(2), 110)!;
        Assert.Same(page, VisionReading.Correct(page, hit, "HP 100/100\nSP 45/60"));
        Assert.Same(page, VisionReading.Correct(page, hit, ""));
    }

    [Fact]
    public void Japanese_word_is_fixed_letter_by_letter()
    {
        // ケ read for ゲ: トカゲ (lizard).
        var (page, word) = Reread(Page(CjkLine("トカケ人間", 100)), 30, 110, "トカゲ人間", new FixedWord(0, 3));

        Assert.Equal("トカゲ", word);
        var line = page.Lines[0];
        Assert.Equal("トカゲ人間", line.Text);
        Assert.Equal(5, line.Words.Count);
        Assert.Equal(CjkLine("トカケ人間", 100).Words[2].Box, line.Words[2].Box);
    }

    [Fact]
    public void Code_fences_and_blank_lines_are_not_text() =>
        Assert.Equal(["SAVE", "LOAD"], VisionReading.Lines("```\nSAVE\r\n\n  LOAD  \n```"));

    [Fact]
    public void A_word_is_doubtful_when_no_dictionary_knows_it_or_the_recognizer_was_unsure()
    {
        var sure = new WordHit("word", default, "word", "word", 0, Script.Latin, 0.97f);
        Assert.False(VisionReading.Doubtful(sure, known: true));
        Assert.False(VisionReading.Doubtful(sure, known: null)); // nothing to check against
        Assert.True(VisionReading.Doubtful(sure, known: false));
        Assert.True(VisionReading.Doubtful(sure with { Score = 0.5f }, known: true));
    }

    [Fact]
    public void Region_is_the_measured_piece_around_the_point() =>
        Assert.Equal(new PixelRect(550, 380, 1450, 600), VisionReading.Region(1000, 500));

    [Theory]
    [InlineData("ニューゲーム", "ニューゲーム")]
    [InlineData("  ニューゲーム \n\n オプション ", "ニューゲーム\nオプション")]
    [InlineData("no text", null)]
    [InlineData("Nothing.", null)]
    [InlineData("(no text)", null)]
    [InlineData("", null)]
    [InlineData("123", null)] // no letters: nothing to translate
    public void A_zones_reading_drops_the_models_no_text_answers(string reading, string? expected) =>
        Assert.Equal(expected, VisionReading.ZoneText(reading));

    [Fact]
    public void A_button_of_rubbish_is_read_whole_a_dialogue_with_one_doubt_word_by_word()
    {
        var button = new PixelRect(0, 0, 200, 60);
        Assert.True(VisionReading.ReadWholeZone(Page(CjkLine("想书", 0)), doubtfulWords: 1, button));
        Assert.True(VisionReading.ReadWholeZone(Page(CjkLine("想书", 0)), doubtfulWords: 0, button)); // 40 px of 200: a piece
        Assert.False(VisionReading.ReadWholeZone(Page(CjkLine("回想モード", 0)), doubtfulWords: 0, new PixelRect(0, 0, 150, 30)));
        // A real line in a zone drawn wide: the recognizer's (the model would bring in the lines around it).
        Assert.False(VisionReading.ReadWholeZone(Page(CjkLine("それでは、刑事", 0)), doubtfulWords: 0, new PixelRect(0, 0, 900, 120)));
        Assert.True(VisionReading.ReadWholeZone(Page(), doubtfulWords: 0, button));
        var dialogue = Page(LatinLine("I kept Wching you all", 0), LatinLine("night long and you", 30), LatinLine("never noticed me", 60));
        Assert.False(VisionReading.ReadWholeZone(dialogue, doubtfulWords: 1, new PixelRect(0, 0, 220, 90))); // three lines: word by word
        Assert.False(VisionReading.ReadWholeZone(Page(LatinLine("I kept Wching you", 0)), doubtfulWords: 1, new PixelRect(0, 0, 180, 30)));
    }

    [Fact]
    public void A_zone_is_sent_with_some_screen_around_it()
    {
        var piece = VisionReading.ZonePiece(new PixelRect(128, 298, 328, 360)); // 0.6 of the height: 37.2 px
        Assert.Equal(90.8, piece.Left, 6);
        Assert.Equal(397.2, piece.Bottom, 6);
        Assert.Equal(new PixelRect(84, 84, 216, 116), VisionReading.ZonePiece(new PixelRect(100, 100, 200, 100))); // at least 16
    }

    /// <summary>A Japanese word boundary fixed by the test: the word is [start, start + length).</summary>
    private sealed class FixedWord(int start, int length) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (start, length);
    }
}
