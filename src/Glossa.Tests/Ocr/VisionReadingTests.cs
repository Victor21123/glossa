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

    /// <summary>The piece a point is read in, 600x180 around it, as in the lookup (the measured size).</summary>
    private static readonly PixelRect PointPiece = VisionReading.PointPiece(1000, 500);

    [Fact]
    public void The_point_is_read_in_the_measured_piece_around_it() =>
        Assert.Equal(new PixelRect(700, 410, 1300, 590), PointPiece);

    [Fact]
    public void The_models_lines_become_a_page_with_their_places_on_screen()
    {
        // Gemma 4 26B on the neon title menu (2026-09-30), boxes as [ymin, xmin, ymax, xmax] in thousandths of the piece.
        var page = VisionReading.LinesPage("""
            [
              {"text": "回想モード", "box_2d": [64, 354, 153, 598]},
              {"text": "オプション", "box_2d": [455, 411, 545, 611]}
            ]
            """, PointPiece)!;

        Assert.Equal(["回想モード", "オプション"], page.Lines.Select(l => l.Text));
        var option = page.Lines[1];
        Assert.Equal(new PixelRect(700 + 411 * 0.6, 410 + 455 * 0.18, 700 + 611 * 0.6, 410 + 545 * 0.18), option.Box);
        // One unit per kana, as the recognizer gives them: the lookup cuts words out of them as usual.
        Assert.Equal(5, option.Words.Count);
        Assert.Equal(option.Box.Left, option.Words[0].Box.Left, 6);
        Assert.Equal(option.Box.Right, option.Words[^1].Box.Right, 6);
        Assert.Equal("オプション", new HitTester().Hit(page, 1000, 500, new FixedWord(0, 5))!.Word);
    }

    [Fact]
    public void A_label_of_two_words_is_cut_by_its_letters()
    {
        var page = VisionReading.LinesPage("""[{"text": "LOAD GAME", "box_2d": [0, 0, 1000, 900]}]""", new PixelRect(0, 0, 1000, 100))!;

        Assert.Equal(["LOAD", "GAME"], page.Lines[0].Words.Select(w => w.Text));
        Assert.Equal(new PixelRect(0, 0, 400, 100), page.Lines[0].Words[0].Box);
        Assert.Equal("LOAD", new HitTester().Hit(page, 150, 50)!.Word);
        Assert.Equal("GAME", new HitTester().Hit(page, 700, 50)!.Word);
    }

    [Fact]
    public void A_sign_read_as_one_item_of_two_rows_is_two_lines()
    {
        var page = VisionReading.LinesPage("""[{"text": "LOAD\nGAME", "box_2d": [0, 0, 1000, 1000]}]""", new PixelRect(0, 0, 400, 200))!;

        Assert.Equal(["LOAD", "GAME"], page.Lines.Select(l => l.Text));
        Assert.Equal(new PixelRect(0, 100, 400, 200), page.Lines[1].Box);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("```json\n[]\n```")]
    [InlineData("[{\"text\": \"\", \"box_2d\": [0, 0, 10, 10]}]")]
    [InlineData("[{\"text\": \"CONFIG\", \"box_2d\": [0, 0, 10]}]")]
    [InlineData("[{\"text\": \"CONFIG\", \"box_2d\": [5, 5, 5, 5]}]")] // no room
    [InlineData("[{\"text\": 12, \"box_2d\": [\"a\", 0, 10, 10]}]")]
    public void An_answer_without_usable_lines_is_an_empty_page(string answer) =>
        Assert.Empty(VisionReading.LinesPage(answer, PointPiece)!.Lines);

    [Theory]
    [InlineData("nothing")]
    [InlineData("The picture shows a menu.")]
    [InlineData("[{\"text\": \"CONFIG\"")] // cut off before any whole line
    public void An_answer_that_is_not_the_lines_is_a_failed_reading(string answer) =>
        Assert.Null(VisionReading.LinesPage(answer, PointPiece));

    [Fact]
    public void An_answer_cut_off_by_the_token_limit_keeps_its_whole_lines()
    {
        // The last "]" is the one of the cut line's box: the list is closed after the last whole line.
        var page = VisionReading.LinesPage("""
            [{"text": "HP : 379", "box_2d": [188, 37, 353, 346]}, {"text": "MP : 106", "box_2d": [463, 437, 583, 747]}, {"text": "HP : 3
            """, PointPiece)!;

        Assert.Equal(["HP : 379", "MP : 106"], page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Boxes_past_the_piece_are_kept_inside_it()
    {
        var line = VisionReading.LinesPage("""[{"text": "CONFIG", "box_2d": [900, -40, 1200, 300]}]""", PointPiece)!.Lines[0];
        Assert.Equal(700, line.Box.Left);
        Assert.Equal(590, line.Box.Bottom);
    }

    [Fact]
    public void A_short_unsure_line_is_a_scrap_of_a_label()
    {
        WordHit Hit(string word, string line, float score) => new(word, default, line, line, 0, Script.Han, score);

        Assert.True(VisionReading.Scrap(Hit("毛", "回想毛", 0.81f)));   // 回想モード in neon outlines
        Assert.True(VisionReading.Scrap(Hit("オー", "オー卜", 0.76f))); // オート
        Assert.False(VisionReading.Scrap(Hit("毛", "回想毛", 0.95f)));
        Assert.False(VisionReading.Scrap(Hit("早送り", "早送り", 1f)));
        Assert.False(VisionReading.Scrap(Hit("策", "策产出了", 0.84f))); // four letters: a line of text
        Assert.True(VisionReading.Scrap(Hit("E", "ME : 106", 0.8f)));   // digits are not letters
    }

    [Fact]
    public void The_models_word_stands_in_for_nothing_or_a_scrap_in_its_line()
    {
        WordHit Hit(string word, double top, double height = 26) =>
            new(word, new PixelRect(100, top, 160, top + height), word, word, 0, Script.Han, 1f);
        var scrap = Hit("毛", 238);
        var nothing = Page();

        Assert.Equal("モード", VisionReading.Settle(null, Hit("モード", 236), nothing)!.Word);
        Assert.Null(VisionReading.Settle(null, null, nothing));
        Assert.Equal("モード", VisionReading.Settle(scrap, Hit("モード", 236, 30), nothing)!.Word);
        // The model's boxes a line off in a dense paragraph: the recognizer's word stays.
        Assert.Same(scrap, VisionReading.Settle(scrap, Hit("次", 205), nothing));
    }

    [Fact]
    public void A_scrap_is_no_text_only_when_the_model_read_nothing_in_its_row()
    {
        var scrap = new WordHit("毛", new PixelRect(100, 238, 160, 264), "回想毛", "回想毛", 2, Script.Han, 0.81f);

        // An icon or a drawing: the model read nothing there, or only lines elsewhere in the piece.
        Assert.Null(VisionReading.Settle(scrap, null, Page()));
        Assert.Null(VisionReading.Settle(scrap, null, Page(LatinLine("Settings", 100))));
        // A line in the same row whose box just misses the point: the recognizer's word stays (unconfirmed).
        Assert.Same(scrap, VisionReading.Settle(scrap, null, Page(LatinLine("OK", 240, left: 300))));
    }

    [Fact]
    public void Get_is_banned_from_the_reading_unless_the_text_is_english()
    {
        Assert.Null(VisionReading.Banned("en", Page()));
        Assert.Equal(["get"], VisionReading.Banned("ja", Page(LatinLine("LOAD GAME", 0))));
        Assert.Equal(["get"], VisionReading.Banned(null, Page())); // nothing recognized to tell by
        Assert.Equal(["get"], VisionReading.Banned(null, Page(CjkLine("回想毛", 0))));
        Assert.Null(VisionReading.Banned(null, Page(LatinLine("Ctrl LICENSE", 0))));
    }

    [Fact]
    public void The_lines_answer_is_held_to_its_json()
    {
        var schema = VisionReading.LinesSchema();
        Assert.Equal("array", (string?)schema["type"]);
        Assert.Equal(12, (int?)schema["maxItems"]);
        Assert.NotSame(schema, VisionReading.LinesSchema()); // each request gets its own copy
    }

    /// <summary>A Japanese word boundary fixed by the test: the word is [start, start + length).</summary>
    private sealed class FixedWord(int start, int length) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (start, length);
    }
}
