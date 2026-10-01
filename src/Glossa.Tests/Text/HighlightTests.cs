using Glossa.Core.Text;

namespace Glossa.Tests.Text;

public class HighlightTests
{
    private const string Line = "I'd reconsider the offer.";

    private static HighlightPart[] Whole(string text) => [new HighlightPart(text, false)];

    [Fact]
    public void Highlight_splits_a_line_around_the_word()
    {
        Assert.Equal(
            [new HighlightPart("I'd ", false), new HighlightPart("reconsider", true), new HighlightPart(" the offer.", false)],
            Highlight.Split(Line, 4, 10));
    }

    [Fact]
    public void Highlight_leaves_no_empty_part_when_the_word_is_at_either_end()
    {
        Assert.Equal([new HighlightPart("I'd", true), new HighlightPart(" reconsider the offer.", false)], Highlight.Split(Line, 0, 3));
        Assert.Equal([new HighlightPart("I'd reconsider the ", false), new HighlightPart("offer.", true)], Highlight.Split(Line, 19, 6));
        Assert.Equal([new HighlightPart(Line, true)], Highlight.Split(Line, 0, Line.Length));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(30, 3)]
    [InlineData(26, 1)]
    [InlineData(20, 10)]
    [InlineData(4, 0)]
    [InlineData(4, -2)]
    public void Highlight_falls_back_to_the_whole_line_on_bad_offsets(int offset, int length)
    {
        Assert.Equal(Whole(Line), Highlight.Split(Line, offset, length));
    }

    [Fact]
    public void Highlight_does_not_overflow_on_a_huge_length()
    {
        Assert.Equal(Whole(Line), Highlight.Split(Line, 4, int.MaxValue));
        Assert.Equal(Whole(Line), Highlight.Split(Line, int.MaxValue, 3));
    }

    [Fact]
    public void Highlight_returns_nothing_for_an_empty_or_missing_line()
    {
        Assert.Empty(Highlight.Split("", 0, 0));
        Assert.Empty(Highlight.Split(null, 0, 3));
    }

    [Fact]
    public void Highlight_splits_a_japanese_line_without_spaces()
    {
        Assert.Equal(
            [new HighlightPart("スライムたちが ", false), new HighlightPart("合体", true), new HighlightPart("していく！", false)],
            Highlight.Split("スライムたちが 合体していく！", 8, 2));
    }

    [Fact]
    public void Highlight_counts_offsets_in_utf16_units_after_an_emoji()
    {
        // The emoji is two UTF-16 units, so "word" starts at 3, not 2.
        Assert.Equal(
            [new HighlightPart("😀 ", false), new HighlightPart("word", true), new HighlightPart("!", false)],
            Highlight.Split("😀 word!", 3, 4));
    }

    [Fact]
    public void Highlight_marks_a_word_made_of_astral_plane_characters()
    {
        Assert.Equal(
            [new HighlightPart("a ", false), new HighlightPart("𠮷野", true), new HighlightPart(" b", false)],
            Highlight.Split("a 𠮷野 b", 2, 3));
    }

    [Fact]
    public void Highlight_falls_back_when_a_cut_is_inside_a_surrogate_pair()
    {
        Assert.Equal(Whole("a 😀 b"), Highlight.Split("a 😀 b", 3, 2)); // starts on the low half
        Assert.Equal(Whole("a 😀 b"), Highlight.Split("a 😀 b", 0, 3)); // ends between the halves
    }

    [Fact]
    public void Highlight_falls_back_when_a_cut_is_before_a_combining_mark()
    {
        const string line = "café au lait"; // e + combining acute
        Assert.Equal(Whole(line), Highlight.Split(line, 0, 4)); // would leave the accent behind
        Assert.Equal(Whole(line), Highlight.Split(line, 4, 2)); // would start on the accent
        Assert.Equal(
            [new HighlightPart("café", true), new HighlightPart(" au lait", false)],
            Highlight.Split(line, 0, 5));
    }
}
