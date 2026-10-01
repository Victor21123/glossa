using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class HitTesterTests
{
    private const double CharWidth = 10;
    private const double LineHeight = 20;

    /// <summary>Builds a line of space-separated words laid out left to right at a fixed pitch.</summary>
    private static OcrLine LatinLine(string text, double top, double left = 0)
    {
        var words = new List<OcrWord>();
        var x = left;
        foreach (var w in text.Split(' '))
        {
            words.Add(new OcrWord(w, new PixelRect(x, top, x + w.Length * CharWidth, top + LineHeight), 0.99f));
            x += (w.Length + 1) * CharWidth;
        }
        return new OcrLine(text, new PixelRect(left, top, x - CharWidth, top + LineHeight), words, 0.99f);
    }

    /// <summary>CJK lines come back from OCR as one unit per character.</summary>
    private static OcrLine CjkLine(string text, double top, double height = LineHeight)
    {
        var words = text.Select((c, i) =>
            new OcrWord(c.ToString(), new PixelRect(i * height, top, (i + 1) * height, top + height), 0.99f)).ToList();
        return new OcrLine(text, new PixelRect(0, top, text.Length * height, top + height), words, 0.99f);
    }

    private static OcrPage Page(params OcrLine[] lines) => new(lines, new PixelRect(0, 0, 2000, 2000), TimeSpan.Zero);

    [Fact]
    public void Picks_word_under_cursor_and_strips_punctuation()
    {
        var page = Page(LatinLine("A new neighbor...? Hello, stranger.", 100));
        // "neighbor...?" starts at char 6 → x = 60..180
        var hit = new HitTester().Hit(page, 100, 110);

        Assert.NotNull(hit);
        Assert.Equal("neighbor", hit.Word);
        Assert.Equal(Script.Latin, hit.Script);
    }

    [Fact]
    public void Hit_carries_the_recognizers_confidence_in_the_word()
    {
        var line = LatinLine("Your experence", 100);
        var unsure = line with { Words = [line.Words[0], line.Words[1] with { Score = 0.6f }] };
        Assert.Equal(0.6f, new HitTester().Hit(Page(unsure), 80, 110)!.Score);

        // A Japanese word is as sure as its least sure character.
        var cjk = CjkLine("攻撃力", 100);
        var chars = cjk with { Words = [cjk.Words[0], cjk.Words[1] with { Score = 0.5f }, cjk.Words[2]] };
        Assert.Equal(0.5f, new HitTester().Hit(Page(chars), 30, 110, new StubMatcher(0, 3))!.Score);
    }

    [Fact]
    public void Short_sentence_widens_context_to_paragraph()
    {
        var page = Page(LatinLine("A new neighbor...? Hello, stranger.", 100));
        var hit = new HitTester().Hit(page, 100, 110)!;

        Assert.Equal("A new neighbor...? Hello, stranger.", hit.Context);
        Assert.Equal("neighbor", hit.Context.Substring(hit.ContextOffset, hit.Word.Length));
    }

    [Fact]
    public void Context_spans_lines_of_one_paragraph_and_rejoins_hyphenation()
    {
        var page = Page(
            LatinLine("The detective could hardly believe how unbeliev-", 100),
            LatinLine("able the whole situation had become that night.", 124),
            LatinLine("Inventory", 400));

        var hit = new HitTester().Hit(page, 25, 130)!; // "able"

        Assert.Equal("able", hit.Word);
        Assert.Contains("unbelievable the whole situation", hit.Context);
        Assert.DoesNotContain("Inventory", hit.Context);
    }

    /// <summary>A Japanese line whose box is <paramref name="height"/> high, <paramref name="pitch"/> wide per character.</summary>
    private static OcrLine JaLine(string text, double left, double top, double pitch, double height)
    {
        var words = text.Select((c, i) =>
            new OcrWord(c.ToString(), new PixelRect(left + i * pitch, top, left + (i + 1) * pitch, top + height), 0.99f)).ToList();
        return new OcrLine(text, new PixelRect(left, top, left + text.Length * pitch, top + height), words, 0.99f);
    }

    [Fact]
    public void A_column_beside_the_text_does_not_cut_its_paragraph()
    {
        // Two panels side by side (Mercurius, 2026-09-29): sorted top to bottom, the left panel's lines fall between
        // the right panel's, and the context used to stop at the first of them.
        var page = Page(
            JaLine("観察力に優れ、", 1373, 686, 18, 30),
            JaLine("ホラーが苦手な方や、", 997, 688, 18, 26),
            JaLine("ホラー慣れている方に", 1373, 710, 19, 26),
            JaLine("探査とパズルが難しく", 997, 711, 19, 24),
            JaLine("感じる方におすすめ", 997, 733, 19, 24),
            JaLine("おすすめ", 1374, 733, 20, 24));

        var hit = new HitTester().Hit(page, 1500, 723, new StubMatcher(7, 2))!; // いる

        Assert.Equal("観察力に優れ、ホラー慣れている方におすすめ", hit.Context);
    }

    [Fact]
    public void Japanese_lines_join_by_the_width_of_their_characters_not_the_height_of_their_boxes()
    {
        var page = Page(
            JaLine("【宇宙迷子モード】", 1397, 649, 17, 29), // the title: the same font, taller box
            JaLine("観察力に優れ、", 1373, 686, 18, 30),
            JaLine("おすすめ", 1374, 710, 20, 24),        // lower box, same font
            JaLine("※このゲームの基本モードです", 1373, 738, 13, 23)); // a note in a smaller font

        var body = new HitTester().Hit(page, 1400, 720, new StubMatcher(0, 4))!;
        var note = new HitTester().Hit(page, 1400, 750, new StubMatcher(12, 2))!;

        Assert.Equal("【宇宙迷子モード】観察力に優れ、おすすめ", body.Context);
        Assert.Equal("※このゲームの基本モードです", note.Context);
    }

    [Fact]
    public void Returns_null_far_from_any_text()
    {
        var page = Page(LatinLine("Hello there", 100));
        Assert.Null(new HitTester().Hit(page, 1500, 1500));
    }

    [Fact]
    public void Cjk_word_uses_term_matcher_span()
    {
        var page = Page(CjkLine("我不知道自己是在哪里出生的。", 100));
        // cursor over 出 (index 10); stub matcher says the word is 出生 (10..12)
        var hit = new HitTester().Hit(page, 10 * LineHeight + 5, 110, new StubMatcher(10, 2))!;

        Assert.Equal("出生", hit.Word);
        Assert.Equal(Script.Han, hit.Script);
        Assert.Equal(new PixelRect(200, 100, 240, 120), hit.Box);
        Assert.Equal("我不知道自己是在哪里出生的。", hit.Context);
    }

    [Fact]
    public void Furigana_line_is_ignored()
    {
        var furigana = CjkLine("うすぐらい", 90, height: 8);
        var body = CjkLine("薄暗い所で泣いていた。", 100);
        var hit = new HitTester().Hit(Page(furigana, body), 5, 96)!; // points between the two lines

        Assert.Equal("薄", hit.Word);
        Assert.DoesNotContain("うすぐらい", hit.Context);
    }

    [Fact]
    public void A_corrected_word_is_spelled_so_in_its_sentence_and_line()
    {
        var misread = new WordHit("Wching", default, "Wching you. Wching them.", "I kept... Wching you. Wching them.", 10, Script.Latin, 0.4f);

        var fixedHit = misread.Respelled("Watching");

        Assert.Equal("Watching", fixedHit.Word);
        Assert.Equal("I kept... Watching you. Wching them.", fixedHit.Context); // only the one under the cursor
        Assert.Equal(10, fixedHit.ContextOffset);
        Assert.Equal("Watching you. Wching them.", fixedHit.Line);
        Assert.Equal(1f, fixedHit.Score);

        // The second one under the cursor, and an offset that missed the word by a letter.
        var second = (misread with { ContextOffset = 24 }).Respelled("Watching");
        Assert.Equal("I kept... Wching you. Watching them.", second.Context);
        Assert.Equal("Wching you. Watching them.", second.Line);
        Assert.Equal("I kept... Wching you. Watching them.", (misread with { ContextOffset = 23 }).Respelled("Watching").Context);
    }

    // Rows 24 px apart (a 20 px box and a 4 px gap): close enough that the paragraph rule alone would glue them.
    private static OcrLine[] WordList(double left, double top, params string[] words) =>
        words.Select((w, i) => LatinLine(w, top + i * 24, left)).ToArray();

    [Fact]
    public void A_column_of_single_words_is_a_list_not_a_sentence()
    {
        var page = Page(WordList(100, 100, "Word", "might", "mistake", "wheel", "magnifying"));

        var hit = new HitTester().Hit(page, 125, 182); // "wheel"

        Assert.NotNull(hit);
        Assert.Equal("wheel", hit.Word);
        Assert.Equal("wheel", hit.Line);
        Assert.Equal("wheel", hit.Context);
    }

    [Fact]
    public void A_paragraph_under_a_list_stays_one_sentence()
    {
        var page = Page([
            .. WordList(100, 100, "Word", "might", "mistake", "wheel", "magnifying"),
            LatinLine("The old harbor master looked at the broken wheel", 240, 100),
            LatinLine("and said it was a mistake to sail tonight.", 264, 100)]);

        var hit = new HitTester().Hit(page, 165, 274); // "said"

        Assert.NotNull(hit);
        Assert.Equal("said", hit.Word);
        Assert.Equal("The old harbor master looked at the broken wheel and said it was a mistake to sail tonight.", hit.Context);
    }

    [Fact]
    public void Prose_right_above_a_list_does_not_take_its_rows()
    {
        var page = Page([
            LatinLine("She said it was a mistake to sail tonight", 76, 100),
            .. WordList(100, 100, "Word", "might", "mistake", "wheel", "magnifying")]);

        var hit = new HitTester().Hit(page, 150, 86); // "it"

        Assert.NotNull(hit);
        Assert.Equal("She said it was a mistake to sail tonight", hit.Context); // no rows glued on
    }

    [Fact]
    public void A_narrow_bubble_of_short_lines_stays_one_sentence()
    {
        var page = Page(
            LatinLine("I don't", 100, 100),
            LatinLine("know what", 124, 100),
            LatinLine("to do.", 148, 100));

        var hit = new HitTester().Hit(page, 140, 134); // "what"

        Assert.NotNull(hit);
        Assert.Equal("I don't know what to do.", hit.Context);
    }

    [Fact]
    public void A_narrow_bubble_of_two_word_lines_without_punctuation_stays_one_sentence()
    {
        // Four lines of two words that fill the bubble: no punctuation, but not ragged either, so not a list.
        var page = Page(
            LatinLine("I really", 100, 100),
            LatinLine("don't know", 124, 100),
            LatinLine("what you", 148, 100),
            LatinLine("mean here", 172, 100));

        var hit = new HitTester().Hit(page, 130, 158); // "what"

        Assert.NotNull(hit);
        Assert.Equal("I really don't know what you mean here", hit.Context);
    }

    /// <summary>A Latin line of one font (the advance per character) with the box height the recognizer happened to give.</summary>
    private static OcrLine FontLine(string text, double top, double height, double advance, double left = 100)
    {
        var words = new List<OcrWord>();
        var x = left;
        foreach (var w in text.Split(' '))
        {
            words.Add(new OcrWord(w, new PixelRect(x, top, x + w.Length * advance, top + height), 0.99f));
            x += (w.Length + 1) * advance;
        }
        return new OcrLine(text, new PixelRect(left, top, x - advance, top + height), words, 0.99f);
    }

    [Fact]
    public void Lines_of_one_paragraph_join_even_when_only_one_has_descenders()
    {
        // 34 against 45 px: the same font, but the second line has descenders and the box grew (ratio 1.32 by height).
        var page = Page(
            FontLine("The old harbor master looked at the broken wheel", 100, 34, 14),
            FontLine("and said it was a mistake to sail tonight.", 140, 45, 14));

        var hit = new HitTester().Hit(page, 400, 160); // "mistake"

        Assert.NotNull(hit);
        Assert.Equal("The old harbor master looked at the broken wheel and said it was a mistake to sail tonight.", hit.Context);
    }

    [Fact]
    public void A_heading_in_a_bigger_font_does_not_join_the_paragraph_below()
    {
        var page = Page(
            FontLine("Chapter One The Old Harbor Master", 100, 62, 21), // 1.4x the font: 62 px with a descender, 34 without
            FontLine("The old harbor master looked at the broken wheel", 172, 34, 14),
            FontLine("and said it was a mistake to sail tonight.", 212, 42, 14));

        var hit = new HitTester().Hit(page, 400, 230); // "mistake"

        Assert.NotNull(hit);
        Assert.Equal("The old harbor master looked at the broken wheel and said it was a mistake to sail tonight.", hit.Context);
    }

    [Fact]
    public void A_small_caption_does_not_join_the_paragraph_above()
    {
        var page = Page(
            FontLine("The old harbor master looked at the broken wheel", 100, 34, 14),
            FontLine("and said it was a mistake to sail tonight.", 140, 34, 14),
            FontLine("Photo taken at the old harbor in the spring", 180, 24, 9.5));

        var hit = new HitTester().Hit(page, 400, 120); // "broken"

        Assert.NotNull(hit);
        Assert.DoesNotContain("Photo", hit.Context);
    }

    // Rows centered on x = 145, as a speech bubble sets them.
    private static OcrLine[] Centered(double top, params string[] rows) =>
        rows.Select((r, i) => LatinLine(r, top + i * 24, 145 - (r.Length * CharWidth) / 2)).ToArray();

    [Fact]
    public void A_narrow_bubble_ending_with_a_full_stop_stays_one_sentence()
    {
        var left = Page(
            LatinLine("I am", 100, 100), LatinLine("not going", 124, 100), LatinLine("to the", 148, 100), LatinLine("market.", 172, 100));
        var centered = Page(Centered(100, "I am", "not going", "to the", "market."));

        foreach (var page in new[] { left, centered })
        {
            var hit = new HitTester().Hit(page, 165, 134); // "going"
            Assert.NotNull(hit);
            Assert.Equal("going", hit.Word);
            Assert.Equal("I am not going to the market.", hit.Context);
        }
    }

    [Fact]
    public void Three_two_word_rows_with_one_ragged_row_stay_one_sentence()
    {
        var page = Page(LatinLine("we should", 100, 100), LatinLine("go now", 124, 100), LatinLine("before it", 148, 100));

        var hit = new HitTester().Hit(page, 110, 134); // "go"

        Assert.NotNull(hit);
        Assert.Equal("we should go now before it", hit.Context);
    }

    [Fact]
    public void Prose_flowing_into_short_rows_stays_one_sentence()
    {
        // A sentence that goes on in short rows (lowercase, nothing ended the line above) is not a list.
        var page = Page(
            LatinLine("I think that we should", 100, 100),
            LatinLine("go", 124, 100), LatinLine("to the", 148, 100), LatinLine("old", 172, 100), LatinLine("market", 196, 100));

        var hit = new HitTester().Hit(page, 110, 182); // "old"

        Assert.NotNull(hit);
        Assert.Equal("I think that we should go to the old market", hit.Context);
    }

    [Fact]
    public void Numbers_and_abbreviations_inside_rows_do_not_break_a_list()
    {
        var page = Page(WordList(100, 100, "Rice", "Mr. Smith", "1,200", "Potion 1.5", "Salt"));

        foreach (var (y, row) in new[] { (134.0, "Mr. Smith"), (158.0, "1,200"), (182.0, "Potion 1.5") })
        {
            var hit = new HitTester().Hit(page, 105, y);
            Assert.NotNull(hit);
            Assert.Equal(row, hit.Line);
            Assert.Equal(row, hit.Context);
        }
    }

    [Fact]
    public void An_equal_width_list_of_single_words_is_a_list()
    {
        var page = Page(WordList(100, 100, "Rice", "Milk", "Eggs", "Salt"));

        var hit = new HitTester().Hit(page, 110, 158); // "Eggs"

        Assert.NotNull(hit);
        Assert.Equal("Eggs", hit.Context);
    }

    [Fact]
    public void A_column_of_numbers_is_a_list()
    {
        var page = Page(WordList(100, 100, "10", "20", "30", "40"));

        Assert.Equal("30", new HitTester().Hit(page, 105, 158)!.Context);
    }

    [Fact]
    public void A_russian_list_is_a_list_and_a_russian_narrow_bubble_stays_one_sentence()
    {
        var list = Page(WordList(100, 100, "Хлеб", "Молоко", "Яйца", "Сыр"));
        Assert.Equal("Яйца", new HitTester().Hit(list, 110, 158)!.Context);

        var bubble = Page(
            LatinLine("Я не", 100, 100), LatinLine("собираюсь", 124, 100), LatinLine("идти на", 148, 100), LatinLine("рынок.", 172, 100));
        Assert.Equal("Я не собираюсь идти на рынок.", new HitTester().Hit(bubble, 130, 134)!.Context);
    }

    [Fact]
    public void A_list_under_a_heading()
    {
        var page = Page([LatinLine("Shopping list for today", 76, 100), .. WordList(100, 100, "Rice", "Milk", "Eggs", "Salt")]);

        Assert.Equal("Milk", new HitTester().Hit(page, 105, 134)!.Context);
        Assert.Equal("Shopping list for today", new HitTester().Hit(page, 105, 86)!.Context);
    }

    [Fact]
    public void A_trailing_hyphen_continuation_is_not_cut_from_its_line()
    {
        var page = Page([
            .. WordList(100, 100, "Rice", "Milk", "Eggs"),
            LatinLine("unbe-", 180, 100), LatinLine("lievable", 204, 100), LatinLine("stuff", 228, 100)]);

        var hit = new HitTester().Hit(page, 105, 214); // "lievable"

        Assert.NotNull(hit);
        Assert.Equal("unbelievable stuff", hit.Context);
    }

    [Fact]
    public void Rows_at_the_width_threshold_are_judged_by_three_quarters_of_the_widest()
    {
        // Widest row 12 characters: a row of 9 is exactly three quarters (fills the line), one of 8 is not.
        var wrapped = Page(
            LatinLine("ab cdefgh", 100, 100), LatinLine("ab cdefgh", 124, 100), LatinLine("abcd efghijk", 148, 100), LatinLine("ab cdefgh", 172, 100));
        Assert.Equal("ab cdefgh ab cdefgh abcd efghijk ab cdefgh", new HitTester().Hit(wrapped, 105, 134)!.Context);

        var ragged = Page(
            LatinLine("ab cdefg", 100, 100), LatinLine("ab cdefg", 124, 100), LatinLine("abcd efghijk", 148, 100), LatinLine("ab cdefgh", 172, 100));
        Assert.Equal("ab cdefg", new HitTester().Hit(ragged, 105, 134)!.Context);
    }

    [Fact]
    public void A_short_last_line_joins_whether_or_not_it_has_descenders()
    {
        // The last line of a paragraph is short, so no mean advance can tell its font: only its letters can. "tonight."
        // has a g (45 px), the line above has no descender at all (34 px).
        var page = Page(
            FontLine("The old harbor master looked at the broken wheel", 100, 34, 14),
            FontLine("tonight.", 140, 45, 14));

        var hit = new HitTester().Hit(page, 400, 115); // "broken"

        Assert.NotNull(hit);
        Assert.Equal("The old harbor master looked at the broken wheel tonight.", hit.Context);
    }

    [Fact]
    public void An_all_caps_line_and_a_lowercase_line_of_one_font_join()
    {
        // Capitals are wider per character (17 against 13 px) and have no descenders (34 against 45 px): by raw height
        // and by advance alone these are two fonts, by their letters one.
        var page = Page(
            FontLine("THE OLD HARBOR MASTER LOOKED", 100, 34, 17),
            FontLine("and said it was a mistake to sail tonight.", 140, 45, 13));

        var hit = new HitTester().Hit(page, 300, 160); // "mistake"

        Assert.NotNull(hit);
        Assert.StartsWith("THE OLD HARBOR MASTER LOOKED and said", hit.Context);
    }

    [Fact]
    public void A_cyrillic_pair_joins_when_only_one_line_has_descenders()
    {
        // The first line has none of д р у ф щ ц (34 px), the second has several (45 px).
        var page = Page(
            FontLine("Он сел на тот стол и все его меха ели", 100, 34, 14),
            FontLine("Пора плыть.", 140, 45, 14));

        var hit = new HitTester().Hit(page, 250, 115); // "стол"

        Assert.NotNull(hit);
        Assert.Equal("Он сел на тот стол и все его меха ели Пора плыть.", hit.Context);
    }

    [Theory]
    [InlineData(40, true)]  // 1.18 of 34, the same letters
    [InlineData(42, false)] // 1.24
    public void Lines_of_the_same_letters_join_below_a_size_ratio_of_1_2(double secondHeight, bool joins)
    {
        var page = Page(
            FontLine("Hello there", 100, 34, 10),
            FontLine("Hello where", 140, secondHeight, 10));

        var hit = new HitTester().Hit(page, 130, 115);

        Assert.NotNull(hit);
        Assert.Equal(joins, hit.Context.Contains("where"));
    }

    private sealed class StubMatcher(int start, int length) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (start, length);
    }
}
