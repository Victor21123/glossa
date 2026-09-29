using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class JapaneseAnalyzerTests
{
    private const string DicDir = @"D:\GlossaData\dict\unidic-lite";

    [Fact]
    public void Gives_reading_and_dictionary_form()
    {
        if (!Directory.Exists(DicDir)) return; // dictionary is installed per machine

        using var ja = new JapaneseAnalyzer(DicDir);
        var tokens = ja.Tokenize("薄暗いじめじめした所でニャーニャー泣いていた");

        var dim = tokens.First(t => t.Surface == "薄暗い");
        Assert.Equal("うすぐらい", dim.Reading);
        Assert.Equal("薄暗い", dim.Lemma);

        var cry = tokens.First(t => t.Surface == "泣い");
        Assert.Equal("泣く", cry.Lemma);
        Assert.Equal("なく", cry.LemmaReading);
    }

    [Fact]
    public void Match_returns_token_span_around_index()
    {
        if (!Directory.Exists(DicDir)) return;

        using var ja = new JapaneseAnalyzer(DicDir);
        const string text = "何でも薄暗いじめじめした所で";
        var (start, length) = ja.Match(text, text.IndexOf('暗'));

        Assert.Equal("薄暗い", text.Substring(start, length));
    }

    [Fact]
    public void Katakana_converts_to_hiragana()
    {
        Assert.Equal("うすぐらい", JapaneseAnalyzer.ToHiragana("ウスグライ"));
        Assert.Equal("にゃー", JapaneseAnalyzer.ToHiragana("ニャー"));
    }
}
