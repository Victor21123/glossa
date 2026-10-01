using Glossa.Core.Input;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Text;

/// <summary>
/// Vertical Japanese (tategaki): columns read top to bottom, the columns right to left. The geometry follows
/// ja_vert_vn.png: columns 46 px wide, one character per 50 px, ~30 px between columns, centres at x 1500 / 1422 / 1344.
/// </summary>
public class VerticalTextTests
{
    private const double Right0 = 1523, Right1 = 1445, Right2 = 1367, Top = 190;

    /// <summary>A vertical column: one unit per character stepping down from <paramref name="top"/>, its right edge at <paramref name="right"/>.</summary>
    private static OcrLine VertLine(string text, double right, double top, double pitch = 50, double width = 46)
    {
        var words = text.Select((c, i) =>
            new OcrWord(c.ToString(), new PixelRect(right - width, top + i * pitch, right, top + (i + 1) * pitch), 0.99f)).ToList();
        return new OcrLine(text, new PixelRect(right - width, top, right, top + text.Length * pitch), words, 0.99f, Vertical: true);
    }

    private static OcrLine Horizontal(string text, double left, double top, double height)
    {
        var words = text.Select((c, i) =>
            new OcrWord(c.ToString(), new PixelRect(left + i * height, top, left + (i + 1) * height, top + height), 0.99f)).ToList();
        return new OcrLine(text, new PixelRect(left, top, left + text.Length * height, top + height), words, 0.99f);
    }

    private static OcrLine Plate(string text, double left, double top)
    {
        var words = new List<OcrWord>();
        var x = left;
        foreach (var w in text.Split(' '))
        {
            words.Add(new OcrWord(w, new PixelRect(x, top, x + w.Length * 10, top + 20), 0.99f));
            x += (w.Length + 1) * 10;
        }
        return new OcrLine(text, new PixelRect(left, top, x - 10, top + 20), words, 0.99f);
    }

    private static OcrPage Page(params OcrLine[] lines) => new(lines, new PixelRect(0, 0, 1920, 1088), TimeSpan.Zero);

    private const string Col0 = "今日はいい天気ですね。", Col1 = "散歩に行きませんか？", Col2 = "はい、喜んで――。";

    private static OcrLine[] ThreeColumns() =>
        [VertLine(Col0, Right0, Top), VertLine(Col1, Right1, Top), VertLine(Col2, Right2, Top)];

    private sealed class StubMatcher(int start, int length) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (start, length);
    }

    [Fact]
    public void Hit_picks_the_character_by_y_in_a_vertical_line()
    {
        var page = Page(VertLine(Col0, Right0, Top));

        var hit = new HitTester().Hit(page, 1500, Top + 5 * 50 + 10); // the sixth character, 天

        Assert.NotNull(hit);
        Assert.Equal("天", hit.Word);
        Assert.True(hit.Vertical);
    }

    [Fact]
    public void Hit_returns_a_word_box_spanning_the_characters_vertically()
    {
        var page = Page(VertLine(Col0, Right0, Top));

        var hit = new HitTester().Hit(page, 1500, Top + 5 * 50 + 10, new StubMatcher(5, 2)); // 天気

        Assert.NotNull(hit);
        Assert.Equal("天気", hit.Word);
        Assert.Equal(new PixelRect(Right0 - 46, Top + 250, Right0, Top + 350), hit.Box);
    }

    [Fact]
    public void Nearest_line_tolerance_uses_the_column_width_for_vertical_lines()
    {
        var page = Page(VertLine(Col0, Right0, Top)); // 46 px wide, 550 px tall

        Assert.NotNull(new HitTester().Hit(page, Right0 - 46 - 30, 400)); // 30 px beside the column: pointing at it
        Assert.Null(new HitTester().Hit(page, Right0 - 46 - 60, 400));    // 60 px away: not (the height would have allowed 400)
        Assert.Null(new HitTester().Hit(page, Right0 + 60, 400));
    }

    [Fact]
    public void Ruby_column_right_of_the_main_column_is_dropped()
    {
        // Furigana stands right of its kanji: a narrow column of kana, as tall as the characters it reads.
        var main = VertLine("漢字を読む", Right0, Top);
        var ruby = VertLine("かんじ", Right0 + 20, Top, pitch: 20, width: 18);

        var kept = HitTester.DropFurigana([main, ruby]);

        Assert.Same(main, Assert.Single(kept));
        var hit = new HitTester().Hit(Page(main, ruby), Right0 + 12, Top + 30); // on the ruby: the main column is meant
        Assert.NotNull(hit);
        Assert.Equal("漢", hit.Word);
        Assert.DoesNotContain("かんじ", hit.Context);
    }

    [Fact]
    public void Ruby_row_above_horizontal_text_is_still_dropped_beside_vertical_columns()
    {
        var furigana = Horizontal("うすぐらい", 100, 90, 8);
        var body = Horizontal("薄暗い所で泣いていた。", 100, 100, 20);

        var kept = HitTester.DropFurigana([VertLine(Col0, Right0, Top), furigana, body]);

        Assert.DoesNotContain(furigana, kept);
        Assert.Contains(body, kept);
    }

    [Fact]
    public void Columns_form_one_paragraph_ordered_right_to_left()
    {
        var lines = ThreeColumns().ToList();

        foreach (var anchor in lines) // from any column the same paragraph comes out
        {
            var paragraph = HitTester.ParagraphOf(lines, anchor);
            Assert.Equal([Col0, Col1, Col2], paragraph.Select(l => l.Text));
        }
    }

    [Fact]
    public void Context_of_a_vertical_paragraph_is_joined_without_spaces()
    {
        var page = Page(VertLine(Col1, Right1, Top), VertLine(Col2, Right2, Top), VertLine(Col0, Right0, Top));

        var hit = new HitTester().Hit(page, 1422, Top + 4 * 50 + 10); // き in the middle column

        Assert.NotNull(hit);
        Assert.Equal("き", hit.Word);
        Assert.Equal(Col1, hit.Line);
        Assert.Equal(Col0 + Col1 + Col2, hit.Context);
        Assert.Equal(Col0.Length + 4, hit.ContextOffset); // a short sentence widens to the whole paragraph
    }

    [Fact]
    public void A_distant_or_larger_column_does_not_join_the_paragraph()
    {
        var anchor = VertLine(Col0, Right0, Top);
        var far = VertLine(Col1, Right1 - 100, Top);               // 132 px gap: another panel
        var bigger = VertLine(Col1, Right1 + 0, Top, pitch: 70, width: 70); // a 70 px font: a title, not the same text
        var smaller = VertLine(Col1, Right1, Top, pitch: 28, width: 26);    // a note in a smaller font

        Assert.Single(HitTester.ParagraphOf([anchor, far], anchor));
        Assert.Single(HitTester.ParagraphOf([anchor, bigger], anchor));
        Assert.Single(HitTester.ParagraphOf([anchor, smaller], anchor));
    }

    [Fact]
    public void A_column_with_no_height_in_common_does_not_join()
    {
        var anchor = VertLine(Col0, Right0, Top);          // y 190..740
        var below = VertLine(Col1, Right1, Top + 800);     // starts under it

        Assert.Single(HitTester.ParagraphOf([anchor, below], anchor));
    }

    [Fact]
    public void Horizontal_and_vertical_lines_never_share_a_paragraph()
    {
        // A two-character column and a horizontal line right under it, of the same font size and sharing its width:
        // the horizontal rule alone would glue them.
        var column = VertLine("はい", Right0, Top, pitch: 50, width: 46);        // 46 wide, 100 tall
        var row = Horizontal("はい", Right0 - 46, Top + 104, 23);               // 46 wide, 23 tall, 4 px below

        var lines = new List<OcrLine> { column, row };

        Assert.Single(HitTester.ParagraphOf(lines, row));
        Assert.Single(HitTester.ParagraphOf(lines, column));
    }

    [Fact]
    public void A_horizontal_name_plate_next_to_vertical_text_stays_separate()
    {
        var plate = Plate("Mika", Right2 - 46 - 20 - 40, Top + 10); // left of the last column, at its height
        var page = Page([.. ThreeColumns(), plate]);

        var blocks = TextBlocks.Of(page);

        Assert.Equal(2, blocks.Count);
        var dialogue = Assert.Single(blocks, b => b.Vertical);
        Assert.Equal(Col0 + Col1 + Col2, dialogue.Text);
        Assert.Equal("Mika", Assert.Single(blocks, b => !b.Vertical).Text);
        var hit = new HitTester().Hit(page, 1344, 300);
        Assert.NotNull(hit);
        Assert.DoesNotContain("Mika", hit.Context);
    }

    [Fact]
    public void Within_keeps_the_words_of_a_vertical_column_cut_by_the_zone()
    {
        var column = VertLine(Col0, Right0, Top); // y 190..740, the middle 465
        var page = Page(column);

        // The zone starts below the column's middle: a horizontal line would be dropped by its CenterY.
        var cut = page.Within(new PixelRect(1400, 500, 1600, 900));

        var kept = Assert.Single(cut.Lines);
        Assert.True(kept.Vertical);
        Assert.Equal(Col0[6..], kept.Text); // the words whose middle is at or below 500
        Assert.Equal(Top + 6 * 50, kept.Box.Top);
        Assert.Equal(Top + Col0.Length * 50, kept.Box.Bottom);
        Assert.Same(column, page.Within(new PixelRect(1400, 0, 1600, 1000)).Lines[0]); // nothing cut: the same line
    }

    [Fact]
    public void Dialogue_prefers_the_lowest_real_vertical_block()
    {
        // The same rule as horizontal text: the lowest block with real text (20 letters), short labels never count.
        var page = Page([
            .. ThreeColumns(),                                              // 24 letters, bottom 740
            VertLine("メニュー", 300, 900),                                  // a label, lower, but short
            VertLine("遠くの空に雲が流れていく", 1800, 60),                    // real text (23 letters with the next one), a block of its own
            VertLine("静かな午後のことでした", 1752, 60)]);                   // its second column; the block ends at 660, above the dialogue
        var blocks = TextBlocks.Of(page);

        var dialogue = TextBlocks.Dialogue(blocks);

        Assert.NotNull(dialogue);
        Assert.True(dialogue.Vertical);
        Assert.Equal(Col0 + Col1 + Col2, dialogue.Text);
    }

    [Fact]
    public void PlateBox_widens_a_vertical_block_and_stays_inside_the_frame()
    {
        var frame = new PixelRect(0, 0, 1920, 1088);
        var narrow = new TextBlock(Col0, new PixelRect(1477, 190, 1523, 740), 1, Vertical: true);
        var atEdge = new TextBlock(Col0, new PixelRect(1860, 190, 1906, 740), 1, Vertical: true);
        var wide = new TextBlock(Col0 + Col1 + Col2, new PixelRect(1321, 190, 1523, 740), 3, Vertical: true);
        var row = new TextBlock("Hello there", new PixelRect(100, 100, 300, 130), 1);

        var plate = TextBlocks.PlateBox(narrow, frame);
        Assert.Equal(240, plate.Width);
        Assert.Equal(narrow.Box.CenterX, plate.CenterX);
        Assert.Equal((190d, 550d), (plate.Top, plate.Height));

        var edge = TextBlocks.PlateBox(atEdge, frame);
        Assert.True(edge.Width >= 240 && edge.Left >= 0 && edge.Right <= 1920);
        Assert.Equal(atEdge.Box.Top, edge.Top);

        Assert.True(TextBlocks.PlateBox(wide, frame).Width >= 240);
        Assert.Equal(row.Box, TextBlocks.PlateBox(row, frame));
        Assert.Equal(new PixelRect(0, 190, 100, 740), TextBlocks.PlateBox(
            narrow with { Box = new PixelRect(30, 190, 76, 740) }, new PixelRect(0, 0, 100, 1088))); // a frame narrower than the plate
    }

    [Fact]
    public void Live_watcher_keys_a_vertical_dialogue_in_reading_order()
    {
        var watcher = new LiveTextWatcher();
        var blocks = TextBlocks.Of(Page(ThreeColumns()));

        var first = watcher.NewLine(blocks, _ => "ru");

        Assert.NotNull(first);
        Assert.Equal(Col0 + Col1 + Col2, first.Text); // right column first
        Assert.Equal(TextBlocks.Key(Col0 + Col1 + Col2), TextBlocks.Key(first.Text));
        Assert.Null(watcher.NewLine(TextBlocks.Of(Page(ThreeColumns())), _ => "ru")); // the same lines read again
    }

    [Fact]
    public void Scrap_is_never_true_for_a_vertical_hit()
    {
        var scrap = new WordHit("毛", new PixelRect(0, 0, 46, 50), "回想毛", "回想毛", 2, Script.Han, 0.81f);

        Assert.True(VisionReading.Scrap(scrap));
        Assert.False(VisionReading.Scrap(scrap with { Vertical = true }));
    }

    [Fact]
    public void Near_vertical_is_true_only_close_to_a_vertical_line()
    {
        var page = Page(VertLine(Col0, Right0, Top), Plate("Mika", 100, 100));

        Assert.True(VisionReading.NearVertical(page, Right0 + 60, 400));  // within 1.5 widths
        Assert.False(VisionReading.NearVertical(page, Right0 + 80, 400));
        Assert.False(VisionReading.NearVertical(page, 120, 110));          // on a horizontal line
        Assert.False(VisionReading.NearVertical(Page(Plate("Mika", 100, 100)), 120, 110));
    }

    [Fact]
    public void Merging_fragments_never_joins_vertical_columns_as_a_row()
    {
        var merged = OcrEngine.MergeFragments([VertLine(Col0, Right0, Top), VertLine(Col1, Right1, Top)]);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, l => Assert.True(l.Vertical));
    }

    [Fact]
    public void Columns_of_different_widths_with_the_same_pitch_join()
    {
        // Measured 46 to 61 px for one font: a kanji column is wider than a kana one (1.33 by width), the pitch is the same.
        var wide = VertLine(Col0, Right0, Top, width: 61);
        var narrow = VertLine(Col1, Right1 - 8, Top, width: 46);
        var lines = new List<OcrLine> { wide, narrow };

        Assert.Equal([Col0, Col1], HitTester.ParagraphOf(lines, narrow).Select(l => l.Text));
    }

    [Fact]
    public void A_two_character_last_column_joins()
    {
        var lines = new List<OcrLine> { VertLine(Col0, Right0, Top), VertLine(Col1, Right1, Top), VertLine("はい", Right2, Top) };

        var paragraph = HitTester.ParagraphOf(lines, lines[0]);

        Assert.Equal([Col0, Col1, "はい"], paragraph.Select(l => l.Text));
    }

    [Fact]
    public void A_column_sharing_less_than_half_of_the_shorter_one_does_not_join()
    {
        var anchor = VertLine(Col0, Right0, Top);                   // y 190..740
        var barely = VertLine(Col1, Right1, Top + 490);             // y 680..1180: 60 of its 500 beside the anchor
        var half = VertLine(Col1, Right1, Top + 275);               // y 465..965: 275 of its 500 beside the anchor

        Assert.Single(HitTester.ParagraphOf([anchor, barely], anchor));
        Assert.Equal(2, HitTester.ParagraphOf([anchor, half], anchor).Count);
    }

    [Fact]
    public void Real_ragged_columns_of_ja_vert_vn_form_one_paragraph()
    {
        // Detector boxes of the frame: 61, 59 and 61 px wide, gaps 19 and 17, tops 191 / 194 / 194, pitch ~49-51.
        var c0 = VertLine(Col0, 1530, 191, 535.0 / 11, 61);
        var c1 = VertLine(Col1, 1450, 194, 504.0 / 10, 59);
        var c2 = VertLine(Col2, 1374, 194, 455.0 / 9, 61);
        var page = Page(c2, c0, c1);

        var hit = new HitTester().Hit(page, 1420, 300);

        Assert.NotNull(hit);
        Assert.Equal(Col1, hit.Line);
        Assert.Equal(Col0 + Col1 + Col2, hit.Context);
    }

    [Fact]
    public void A_point_in_the_gap_between_columns_goes_to_the_nearer_one_and_the_right_one_on_a_tie()
    {
        var right = VertLine(Col0, 1523, Top);          // x 1477..1523
        var left = VertLine(Col1, 1455, Top);           // x 1409..1455, a 22 px gap

        foreach (var page in new[] { Page(right, left), Page(left, right) })
        {
            Assert.Equal(Col0, new HitTester().Hit(page, 1466, 400)!.Line); // 11 px from both: the one read first
            Assert.Equal(Col1, new HitTester().Hit(page, 1461, 400)!.Line);
            Assert.Equal(Col0, new HitTester().Hit(page, 1471, 400)!.Line);
        }
    }

    [Fact]
    public void A_kana_only_dialogue_column_next_to_a_narrow_kana_column_is_kept()
    {
        var dialogue = VertLine("はいそうですね", Right0, Top);
        var aside = VertLine("ええ", Right0 + 30, Top, pitch: 24, width: 24); // narrow, kana, right of it, but no kanji to read

        Assert.Equal(2, HitTester.DropFurigana([dialogue, aside]).Count);
    }

    [Fact]
    public void A_narrow_dialogue_column_taller_than_the_title_beside_it_is_kept()
    {
        var title = VertLine("第一章", Right0, Top, pitch: 60, width: 60);                    // 60 wide, 180 tall
        var dialogue = VertLine("はいそうですよね", Right0 + 40, Top, pitch: 30, width: 30);   // 30 wide, 240 tall: ruby is never taller

        Assert.Equal(2, HitTester.DropFurigana([title, dialogue]).Count);
    }

    [Fact]
    public void Many_ruby_fragments_are_dropped_even_when_they_outnumber_the_main_columns()
    {
        var main = VertLine("漢字を読む方法です", Right0, Top);
        var other = VertLine("文字を読んでいる", Right1, Top);
        var rubies = new[]
        {
            VertLine("かんじ", Right0 + 21, Top, pitch: 24, width: 19),
            VertLine("よ", Right0 + 21, Top + 150, pitch: 24, width: 19),
            VertLine("ほうほう", Right0 + 21, Top + 200, pitch: 24, width: 19),
            VertLine("もじ", Right1 + 21, Top, pitch: 24, width: 19)
        };

        var kept = HitTester.DropFurigana([main, other, .. rubies]);

        Assert.Equal([main, other], kept);
    }

    [Fact]
    public void Left_on_the_last_column_stays_put_when_a_horizontal_line_follows()
    {
        // The plate sits below the columns and is read after them, but it is not a column to step into.
        var page = Page(VertLine(Cols[0], Right0, Top), VertLine(Cols[1], Right1, Top),
            Plate("Speaker name that is long enough", Right0 - 400, 900));
        var nav = FrameWords.Build(page, null);
        Assert.Equal("Speaker", nav.Current!.Text); // the lowest real text
        Assert.True(nav.Move(PadButtons.DPadUp));   // into a column
        Assert.True(nav.Move(PadButtons.DPadLeft)); // the left column
        var (line, index) = (nav.LineIndex, nav.Index);

        Assert.False(nav.Move(PadButtons.DPadLeft));

        Assert.Equal((line, index), (nav.LineIndex, nav.Index));
    }

    [Fact]
    public void Right_on_the_first_column_does_not_walk_down_it_after_a_horizontal_line()
    {
        var page = Page(
            Plate("Speaker name that is long enough to be text", Right0 - 400, 60),
            VertLine(Cols[0], Right0, Top), VertLine(Cols[1], Right1, Top), VertLine(Cols[2], Right2, Top));
        var nav = FrameWords.Build(page, null);
        for (var i = 0; i < 3; i++) nav.Move(PadButtons.DPadDown);
        var before = nav.Current;

        Assert.False(nav.Move(PadButtons.DPadRight));

        Assert.Same(before, nav.Current);
    }

    private static OcrPage TwoPanels() => Page(
        VertLine("遠くの空に雲が流れ", 500, Top), VertLine("静かな午後のことで", 422, Top),
        VertLine(Cols[0], Right0, Top), VertLine(Cols[1], Right1, Top));

    [Fact]
    public void Down_at_the_end_of_a_paragraph_does_not_cross_into_another_vertical_paragraph()
    {
        var nav = FrameWords.Build(TwoPanels(), null);
        for (var i = 0; i < 40; i++) nav.Move(PadButtons.DPadDown); // to the end of the first column and on to the second
        var end = nav.Current;

        Assert.False(nav.Move(PadButtons.DPadDown));
        Assert.Same(end, nav.Current);
        Assert.Equal(1, nav.LineIndex % 2); // still in the second column of its own panel
    }

    [Fact]
    public void Left_goes_to_the_nearest_column_on_that_side_even_in_another_paragraph()
    {
        var nav = FrameWords.Build(TwoPanels(), null);
        // The lowest real text is the right panel (bottom 740 against 690), so it starts on its first column.
        Assert.Equal("今", nav.Current!.Text);

        Assert.True(nav.Move(PadButtons.DPadLeft)); // the other column of its own panel
        Assert.Equal("散", nav.Current!.Text);
        Assert.True(nav.Move(PadButtons.DPadLeft)); // then the nearest column of the other panel
        Assert.Equal("遠", nav.Current.Text);
    }

    [Fact]
    public void Entering_a_column_from_a_row_picks_the_character_by_height()
    {
        // The plate is the lowest real text, so the highlight starts on it; Up enters the column nearest in height.
        var page = Page(
            VertLine(Cols[0], Right0, Top),
            Plate("Speaker name that is long enough", Right0 - 100, 900));
        var nav = FrameWords.Build(page, null);
        Assert.Equal("Speaker", nav.Current!.Text);

        Assert.True(nav.Move(PadButtons.DPadUp));

        Assert.Equal("す", nav.Current.Text); // the last character, the nearest to the plate's height
    }

    // Still-frame navigation: characters without punctuation, one step per character (no matcher).
    private static readonly string[] Cols = ["今日はいい天気です", "散歩に行きませんか", "はい喜んで"];

    private static OcrPage Columns(params OcrLine[] extra) =>
        Page([VertLine(Cols[1], Right1, Top + 15), VertLine(Cols[2], Right2, Top), VertLine(Cols[0], Right0, Top), .. extra]);

    [Fact]
    public void Down_moves_along_a_column()
    {
        var nav = FrameWords.Build(Columns(), null);
        Assert.Equal("今", nav.Current!.Text);

        Assert.True(nav.Move(PadButtons.DPadDown));
        Assert.Equal("日", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.StickDown));
        Assert.Equal("は", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.DPadUp));
        Assert.Equal("日", nav.Current.Text);
    }

    [Fact]
    public void Left_moves_to_the_next_column_at_the_nearest_height()
    {
        var nav = FrameWords.Build(Columns(), null);
        for (var i = 0; i < 3; i++) nav.Move(PadButtons.DPadDown); // い, the fourth character: middle at y 365
        Assert.Equal("い", nav.Current!.Text);

        Assert.True(nav.Move(PadButtons.DPadLeft));

        // The middle column starts 15 px lower: its characters have middles at 230, 280, 330, 380 ... so 380 (行) is nearest.
        Assert.Equal("行", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.DPadRight));
        Assert.Equal("い", nav.Current.Text); // back on the right column, at the middle nearest to 380
        Assert.Equal(0, nav.LineIndex);
    }

    [Fact]
    public void Down_at_the_column_end_goes_to_the_next_column()
    {
        var nav = FrameWords.Build(Columns(), null);
        for (var i = 0; i < Cols[0].Length - 1; i++) Assert.True(nav.Move(PadButtons.DPadDown));
        Assert.Equal("す", nav.Current!.Text);

        Assert.True(nav.Move(PadButtons.DPadDown));

        Assert.Equal("散", nav.Current.Text); // the first character of the column to the left
        Assert.True(nav.Move(PadButtons.DPadUp));
        Assert.Equal("す", nav.Current.Text); // and back to the end of the one it came from
    }

    [Fact]
    public void Build_orders_columns_right_to_left()
    {
        var nav = FrameWords.Build(Columns(), null);

        Assert.Equal(string.Concat(Cols), string.Concat(nav.All.Select(w => w.Text)));
        Assert.Equal(3, nav.LineCount);
    }
}
