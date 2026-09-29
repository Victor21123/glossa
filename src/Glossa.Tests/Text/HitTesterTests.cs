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

    private sealed class StubMatcher(int start, int length) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (start, length);
    }
}
