using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class ChineseDictionaryTests
{
    private const string Cedict = @"D:\GlossaData\dict\cedict_ts.u8";

    [Theory]
    [InlineData("chu1 sheng1", "chūshēng")]
    [InlineData("zhi1 dao5", "zhīdao")]
    [InlineData("lu:4", "lǜ")]
    [InlineData("gou3", "gǒu")]
    [InlineData("xiu1", "xiū")]
    [InlineData("Bei3 jing1", "Běijīng")]
    public void Numbered_pinyin_becomes_tone_marks(string numbered, string expected) =>
        Assert.Equal(expected, ChineseDictionary.ToToneMarks(numbered));

    [Fact]
    public void Parses_cedict_line()
    {
        var e = ChineseDictionary.Parse("傳統 传统 [chuan2 tong3] /tradition/traditional/")!;
        Assert.Equal("傳統", e.Traditional);
        Assert.Equal("传统", e.Simplified);
        Assert.Equal(["tradition", "traditional"], e.Glosses);
    }

    [Fact]
    public void Segments_around_cursor_with_real_dictionary()
    {
        if (!File.Exists(Cedict)) return;
        var zh = new ChineseDictionary(Cedict);
        const string text = "我不知道自己是在哪里出生的。";

        string WordAt(char c)
        {
            var (s, l) = zh.Match(text, text.IndexOf(c));
            return text.Substring(s, l);
        }

        Assert.Equal("出生", WordAt('出'));
        Assert.Equal("出生", WordAt('生'));
        Assert.Equal("哪里", WordAt('哪'));
        Assert.Equal("自己", WordAt('己'));
        Assert.Equal("chūshēng", zh.PinyinOf("出生"));
        Assert.Contains("to be born", zh.SensesOf("出生"));
    }
}
