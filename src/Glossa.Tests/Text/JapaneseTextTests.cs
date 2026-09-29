using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class JapaneseTextTests
{
    [Fact]
    public void Table_pairs_each_old_form_with_one_modern_form()
    {
        var (old, modern) = JapaneseText.Table;
        Assert.Equal(old.Length, modern.Length);
        Assert.Equal(old.Length, old.Distinct().Count());
        Assert.All(old.Zip(modern), p => Assert.NotEqual(p.First, p.Second));
        Assert.DoesNotContain(old, modern.Contains); // a fixed text is not fixed again
        Assert.All(old + modern, c => Assert.False(char.IsSurrogate(c)));
    }

    [Theory]
    [InlineData("攻擊力", "攻撃力")]      // what PP-OCRv6 medium read on a Japanese menu
    [InlineData("說明書", "説明書")]
    [InlineData("者", "者")]          // a compatibility kanji
    [InlineData("攻撃力", "攻撃力")]
    public void Old_and_traditional_forms_become_modern(string read, string want) =>
        Assert.Equal(want, JapaneseText.Normalize(read));

    [Theory]
    [InlineData("ス一パ一", "スーパー")]      // between katakana and at the end of a katakana word
    [InlineData("パ一ティ", "パーティ")]
    [InlineData("ス一パ一！", "スーパー！")]
    [InlineData("カード一枚", "カード一枚")]  // the number before a counter
    [InlineData("ケーキ一つ", "ケーキ一つ")]
    [InlineData("もう一つ", "もう一つ")]      // after hiragana it is the number
    [InlineData("ただ一つの", "ただ一つの")]
    [InlineData("その一", "その一")]
    [InlineData("一番", "一番")]
    public void Kanji_one_is_the_long_vowel_mark_only_inside_a_katakana_word(string read, string want) =>
        Assert.Equal(want, JapaneseText.Normalize(read));

    [Fact]
    public void Elsewhere_the_mark_is_restored_when_the_dictionary_has_the_word()
    {
        static bool Dictionary(string w) => w is "ユーザー" or "すごー";
        Assert.Equal("ユーザー名", JapaneseText.Normalize("ユ一ザ一名", Dictionary));
        Assert.Equal("すごーい", JapaneseText.Normalize("すご一い", Dictionary));
        Assert.Equal("カード一枚", JapaneseText.Normalize("カード一枚", Dictionary));
        Assert.Equal("もう一つ", JapaneseText.Normalize("もう一つ", Dictionary));
    }

    [Fact]
    public void Page_keeps_one_box_per_character()
    {
        var units = "攻擊力".Select((c, i) => new OcrWord(c.ToString(), new PixelRect(i * 20, 0, (i + 1) * 20, 20), 0.9f)).ToList();
        var page = new OcrPage([new OcrLine("攻擊力", new PixelRect(0, 0, 60, 20), units, 0.9f)], new PixelRect(0, 0, 100, 100), TimeSpan.Zero);

        var line = JapaneseText.Normalize(page).Lines[0];

        Assert.Equal("攻撃力", line.Text);
        Assert.Equal(["攻", "撃", "力"], line.Words.Select(w => w.Text));
        Assert.Equal(units.Select(w => w.Box), line.Words.Select(w => w.Box));
    }

    [Fact]
    public void Page_without_anything_to_fix_is_the_same_page()
    {
        var page = new OcrPage([new OcrLine("Hello", new PixelRect(0, 0, 50, 20), [], 0.9f)], new PixelRect(0, 0, 100, 100), TimeSpan.Zero);
        Assert.Same(page, JapaneseText.Normalize(page));
    }
}
