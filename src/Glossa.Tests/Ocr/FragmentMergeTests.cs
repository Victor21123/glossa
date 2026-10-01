using Glossa.Core.Ocr;

namespace Glossa.Tests.Ocr;

public class FragmentMergeTests
{
    private static OcrLine Piece(string text, double left, double top, double width, double height = 30) =>
        new(text, new PixelRect(left, top, left + width, top + height),
            [new OcrWord(text, new PixelRect(left, top, left + width, top + height), 1)], 1);

    [Fact]
    public void Pieces_of_one_line_join_left_to_right()
    {
        // The detector cut "Hello there, stranger" into pieces at wide letter gaps; they come in any order.
        var merged = OcrEngine.MergeFragments([
            Piece("stranger", 300, 100, 120),
            Piece("Hello", 100, 101, 80),
            Piece("there,", 190, 99, 90)]);

        var line = Assert.Single(merged);
        Assert.Equal("Hello there, stranger", line.Text);
        Assert.Equal(100, line.Box.Left);
        Assert.Equal(420, line.Box.Right);
        Assert.Equal(3, line.Words.Count);
    }

    [Fact]
    public void Cjk_pieces_split_at_punctuation_still_join()
    {
        // 1.2 heights apart: the gap the detector leaves at a Japanese comma, and the pieces join without a space.
        var merged = OcrEngine.MergeFragments([
            Piece("ホラーが苦手な方や", 100, 100, 270),
            Piece("、探査が難しく", 406, 100, 210)]);

        var line = Assert.Single(merged);
        Assert.Equal("ホラーが苦手な方や、探査が難しく", line.Text);
    }

    [Fact]
    public void Lines_one_under_another_never_join()
    {
        var merged = OcrEngine.MergeFragments([Piece("File", 100, 100, 60), Piece("Edit", 100, 134, 60)]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Same_row_needs_a_shared_baseline_and_a_close_neighbour()
    {
        var a = new PixelRect(100, 100, 200, 130);
        Assert.True(OcrEngine.SameRow(a, new PixelRect(230, 102, 300, 132)));
        Assert.False(OcrEngine.SameRow(a, new PixelRect(230, 128, 300, 158))); // a row lower
        Assert.False(OcrEngine.SameRow(a, new PixelRect(400, 100, 500, 130))); // 10 heights away
    }
}
