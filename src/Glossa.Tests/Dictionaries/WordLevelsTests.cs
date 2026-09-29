using Glossa.Core.Lookup;

namespace Glossa.Tests.Dictionaries;

public class WordLevelsTests
{
    [Theory]
    [InlineData("ja", "JLPT N3", "JLPT", "N3")]   // from the lists
    [InlineData("ja", "N1", "JLPT", "N1")]        // from the AI
    [InlineData("zh", "HSK 4", "HSK", "4")]
    [InlineData("zh", "HSK 7–9", "HSK", "7-9")]
    [InlineData("en", "b2", "CEFR", "B2")]
    public void A_level_is_read_on_its_languages_list(string language, string level, string scale, string value) =>
        Assert.Equal((scale, value), WordLevels.Split(language, level));

    [Theory]
    [InlineData("ja", "A1")]   // the AI gave a CEFR level to です (2026-09-29): not shown as one
    [InlineData("en", "N3")]
    [InlineData("zh", "B2")]
    [InlineData("en", "")]
    [InlineData("en", null)]
    public void A_level_off_the_languages_list_is_none(string language, string? level) =>
        Assert.Null(WordLevels.Split(language, level));

    [Fact]
    public void Stored_and_short_forms()
    {
        Assert.Equal("JLPT N3", WordLevels.Normalize("ja", "N3"));
        Assert.Equal("HSK 4", WordLevels.Normalize("zh", "4"));
        Assert.Equal("B2", WordLevels.Normalize("en", "B2"));
        Assert.Null(WordLevels.Normalize("ja", "A1"));
        Assert.Equal("N3", WordLevels.Short("ja", "JLPT N3"));
        Assert.Equal("HSK 4", WordLevels.Short("zh", "HSK 4"));
    }
}
