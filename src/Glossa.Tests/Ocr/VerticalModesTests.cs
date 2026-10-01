using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Ocr;

/// <summary>
/// Vertical Japanese in every mode (slice 3): the region that grows when a column touches its edge, columns found by
/// their quadrilateral (deskew), a column shredded into glyphs, and the model with sight staying out of columns.
/// </summary>
public class VerticalModesTests
{
    private static readonly PixelRect Frame = new(0, 0, 1920, 1080);

    private static OcrLine Column(double right, double top, double bottom, double width = 46)
    {
        var n = (int)((bottom - top) / 50);
        var words = Enumerable.Range(0, n).Select(i => new OcrWord("あ", new PixelRect(right - width, top + i * 50, right, top + (i + 1) * 50), 0.95f)).ToList();
        return new OcrLine(new string('あ', n), new PixelRect(right - width, top, right, bottom), words, 0.95f, Vertical: true);
    }

    private static OcrLine Row(string text, double left, double top, double width, double height = 40) =>
        new(text, new PixelRect(left, top, left + width, top + height), [new OcrWord(text, new PixelRect(left, top, left + width, top + height), 0.95f)], 0.95f);

    private static OcrPage Page(PixelRect region, params OcrLine[] lines) => new(lines, region, TimeSpan.Zero);

    // ---------------------------------------------------------------- region growth

    [Fact]
    public void Around_is_the_lookup_region_of_the_cursor()
    {
        var roi = LookupRegion.Around(1000, 500);

        Assert.Equal(new PixelRect(100, 240, 1900, 720), roi);
    }

    [Fact]
    public void NeedsTaller_is_true_only_for_a_vertical_line_touching_the_roi_edge()
    {
        var roi = LookupRegion.Around(1000, 500); // 100..1900 x 240..720
        var touchingBottom = Page(roi, Column(1010, 400, 720));
        var touchingTop = Page(roi, Column(1010, 240, 600));
        var inside = Page(roi, Column(1010, 300, 650));

        Assert.True(LookupRegion.NeedsTaller(touchingBottom, roi, Frame, 1000));
        Assert.True(LookupRegion.NeedsTaller(touchingTop, roi, Frame, 1000));
        Assert.False(LookupRegion.NeedsTaller(inside, roi, Frame, 1000));
        Assert.True(LookupRegion.NeedsTaller(Page(roi, Column(1010, 400, 717)), roi, Frame, 1000)); // 3 px short still touches
        Assert.False(LookupRegion.NeedsTaller(Page(roi, Column(1010, 400, 700)), roi, Frame, 1000));
    }

    [Fact]
    public void NeedsTaller_looks_only_at_columns_near_the_cursor()
    {
        var roi = LookupRegion.Around(1000, 500);
        var far = Page(roi, Column(1500, 400, 720)); // a column 450 px away: not the one pointed at
        var near = Page(roi, Column(1060, 400, 720)); // its left edge 46 px... within two column widths of x

        Assert.False(LookupRegion.NeedsTaller(far, roi, Frame, 1000));
        Assert.True(LookupRegion.NeedsTaller(near, roi, Frame, 1000));
    }

    [Fact]
    public void NeedsTaller_is_false_for_horizontal_text_at_the_same_edge()
    {
        var roi = LookupRegion.Around(1000, 500);
        var page = Page(roi, Row("今日はいい天気ですね", 900, 680, 500), Row("Hello there general", 900, 240, 300));

        Assert.False(LookupRegion.NeedsTaller(page, roi, Frame, 1000));
    }

    [Fact]
    public void NeedsTaller_ignores_a_tall_box_the_vertical_read_left_alone()
    {
        var roi = LookupRegion.Around(1000, 500);
        var box = new PixelRect(990, 400, 1036, 720);
        var tall = new OcrLine("ツ晕、M判", box, [new OcrWord("ツ晕、M判", box, 0.9f)], 0.9f);
        var latin = new OcrLine("llllll", box, [new OcrWord("llllll", box, 0.9f)], 0.9f);

        Assert.False(LookupRegion.NeedsTaller(Page(roi, tall), roi, Frame, 1000)); // only real vertical lines grow the window
        Assert.False(LookupRegion.NeedsTaller(Page(roi, latin), roi, Frame, 1000));
    }

    [Fact]
    public void NeedsTaller_is_false_when_the_roi_edge_is_the_frame_edge()
    {
        var roi = LookupRegion.Around(1000, 100); // reaches above the frame
        var page = Page(new PixelRect(100, 0, 1900, 320), Column(1010, 0, 200));

        Assert.False(LookupRegion.NeedsTaller(page, roi, Frame, 1000)); // the top is the frame's and the column ends inside
        var bottomTouch = Page(new PixelRect(100, 0, 1900, 320), Column(1010, 100, 320));
        Assert.True(LookupRegion.NeedsTaller(bottomTouch, roi, Frame, 1000));
        var wholeFrame = new PixelRect(0, 0, 1920, 1080);
        Assert.False(LookupRegion.NeedsTaller(Page(wholeFrame, Column(1010, 0, 1080)), wholeFrame, Frame, 1000));
    }

    [Fact]
    public void Taller_window_is_clamped_to_the_frame()
    {
        var middle = LookupRegion.Taller(1000, 500, Frame);
        var corner = LookupRegion.Taller(30, 20, Frame);
        var bottomRight = LookupRegion.Taller(1900, 1070, Frame);

        Assert.Equal(900, middle.Width);
        Assert.Equal(1080, middle.Height);
        Assert.Equal(1000, middle.CenterX);
        Assert.True(Frame.Contains(corner.Left, corner.Top) && Frame.Contains(corner.Right, corner.Bottom));
        Assert.Equal(0, corner.Left); // shifted, not centred and cut
        Assert.Equal(900, corner.Width);
        Assert.Equal(1920, bottomRight.Right);
        Assert.Equal(1020, bottomRight.Left);
        Assert.Equal(1080, bottomRight.Bottom);
        Assert.Equal(1080, bottomRight.Height);
    }

    [Fact]
    public void Taller_window_is_not_taller_than_the_tallest_sensible_on_a_big_screen()
    {
        var tall = new PixelRect(0, 0, 2560, 1440);

        var roi = LookupRegion.Taller(1280, 900, tall);

        Assert.Equal(LookupRegion.TallerHeight, roi.Height);
        Assert.True(roi.Top <= 900 && roi.Bottom >= 900);
        Assert.True(tall.Contains(roi.Left, roi.Top) && tall.Contains(roi.Right, roi.Bottom));
    }

    [Fact]
    public void Taller_window_of_a_small_frame_is_the_frame()
    {
        var small = new PixelRect(0, 0, 800, 600);

        Assert.Equal(small, LookupRegion.Taller(400, 300, small));
    }

    [Fact]
    public void Zones_Taller_extends_the_piece_up_and_down_inside_the_frame()
    {
        var big = new PixelRect(0, 0, 2560, 2000);
        var piece = Zones.Around(new PixelRect(700, 900, 1100, 1100)); // 480 high

        var taller = Zones.Taller(piece, big);

        // 480 and a Reach more each way is 1440: the cap of the tall window applies, around the piece's middle
        Assert.Equal(LookupRegion.TallerHeight, taller.Height);
        Assert.Equal(piece.CenterY, taller.Top + taller.Height / 2);
        Assert.Equal(piece.Left, taller.Left);
        Assert.Equal(piece.Right, taller.Right);
        var atTop = Zones.Taller(new PixelRect(700, 20, 1100, 500), big);
        Assert.Equal(0, atTop.Top);
        Assert.Equal(500 + Zones.Reach, atTop.Bottom); // under the cap: Reach more below
        var short_ = Zones.Taller(piece, new PixelRect(0, 0, 2560, 1000)); // the frame's bottom is the limit
        Assert.Equal((piece.Top - Zones.Reach, 1000d), (short_.Top, short_.Bottom));
    }

    [Fact]
    public void Zones_Taller_is_not_taller_than_the_tallest_sensible()
    {
        var big = new PixelRect(0, 0, 2560, 2000);
        var piece = new PixelRect(700, 600, 1100, 1300); // 700 high: 480 more each way would be 1660

        var taller = Zones.Taller(piece, big);

        Assert.Equal(LookupRegion.TallerHeight, taller.Height);
        Assert.True(taller.Top <= piece.CenterY && taller.Bottom >= piece.CenterY);
        Assert.Equal(piece.Left, taller.Left);
        var huge = new PixelRect(700, 100, 1100, 1500); // already taller than that: it stays as it is
        Assert.Equal(huge, Zones.Taller(huge, big));
    }

    [Fact]
    public void Region_growth_works_in_a_frame_with_a_negative_origin()
    {
        var left = new PixelRect(-1920, 0, 0, 1080); // a monitor to the left of the main one
        var roi = LookupRegion.Around(-1000, 500);
        var page = Page(roi, Column(-990, 400, 720));

        Assert.True(LookupRegion.NeedsTaller(page, roi, left, -1000));
        var tall = LookupRegion.Taller(-1000, 500, left);
        Assert.True(left.Contains(tall.Left, tall.Top) && left.Contains(tall.Right, tall.Bottom));
        Assert.Equal(-1450, tall.Left);
        var corner = LookupRegion.Taller(-30, 20, left);
        Assert.Equal(0, corner.Right);
        Assert.Equal(0, corner.Top);
    }

    [Fact]
    public void Region_growth_in_a_frame_smaller_than_the_tall_window_is_the_frame_near_an_edge()
    {
        var small = new PixelRect(0, 0, 800, 600);

        var tall = LookupRegion.Taller(790, 590, small);

        Assert.Equal(small, tall);
        Assert.False(LookupRegion.NeedsTaller(Page(small, Column(790, 0, 600)), small, small, 790)); // the region is the whole frame
    }

    // ---------------------------------------------------------------- columns by their quadrilateral

    private static (double X, double Y)[] Lean(double cx, double cy, double w, double h, double degrees)
    {
        var a = degrees * Math.PI / 180;
        (double X, double Y) P(double dx, double dy) => (cx + dx * Math.Cos(a) - dy * Math.Sin(a), cy + dx * Math.Sin(a) + dy * Math.Cos(a));
        return [P(-w / 2, -h / 2), P(w / 2, -h / 2), P(w / 2, h / 2), P(-w / 2, h / 2)];
    }

    [Fact]
    public void An_upright_quad_gives_its_rectangle()
    {
        var q = ColumnQuad.Fit([Lean(123, 300, 46, 500, 0)])!.Value;

        Assert.Equal(500, q.Length, 3);
        Assert.Equal(46, q.Width, 3);
        Assert.Equal(100, q.TopLeft.X, 3);
        Assert.Equal(50, q.TopLeft.Y, 3);
        Assert.Equal(146, q.BottomRight.X, 3);
        Assert.Equal(550, q.BottomRight.Y, 3);
    }

    [Fact]
    public void A_leaning_quad_keeps_its_own_axis_whatever_the_point_order()
    {
        // a column leaning 5 degrees: its top is to the right of its bottom
        var quad = Lean(500, 400, 46, 600, 5);
        var shuffled = new[] { quad[2], quad[0], quad[3], quad[1] };

        var q = ColumnQuad.Fit([shuffled])!.Value;

        Assert.Equal(600, q.Length, 2);
        Assert.Equal(46, q.Width, 2);
        Assert.Equal(5, q.TiltDegrees, 2);
        Assert.True(q.TopLeft.Y < q.BottomLeft.Y);
        Assert.True(q.TopLeft.X < q.TopRight.X);
        Assert.Equal(quad[0].X, q.TopLeft.X, 2);
        Assert.Equal(quad[0].Y, q.TopLeft.Y, 2);
    }

    [Fact]
    public void Pieces_of_one_leaning_column_make_one_long_quad()
    {
        // one axis through (500, 400): the upper piece covers 300..100 px above its middle, the lower 90 px above to 210 below
        (double X, double Y) Along(double s) => (500 - s * Math.Sin(5 * Math.PI / 180), 400 + s * Math.Cos(5 * Math.PI / 180));
        var (uc, lc) = (Along(-200), Along(60));
        var upper = Lean(uc.X, uc.Y, 46, 200, 5);
        var lower = Lean(lc.X, lc.Y, 46, 300, 5);
        var q = ColumnQuad.Fit([upper, lower])!.Value;

        Assert.Equal(510, q.Length, 1);
        Assert.Equal(46, q.Width, 1);
        Assert.Equal(5, q.TiltDegrees, 1);
    }

    [Fact]
    public void The_margin_grows_the_quad_along_and_across()
    {
        var plain = ColumnQuad.Fit([Lean(123, 300, 46, 500, 4)])!.Value;
        var margin = ColumnQuad.Fit([Lean(123, 300, 46, 500, 4)])!.Value.Grow(4);

        Assert.Equal(plain.Length + 8, margin.Length, 2);
        Assert.Equal(plain.Width + 8, margin.Width, 2);
    }

    [Fact]
    public void An_absurd_lean_is_read_upright()
    {
        var q = ColumnQuad.Fit([Lean(300, 300, 46, 400, 40)])!.Value;

        Assert.Equal(0, q.TiltDegrees, 6);
    }

    [Fact]
    public void A_slice_of_a_leaning_quad_follows_the_lean()
    {
        var q = ColumnQuad.Fit([Lean(500, 400, 46, 600, 5)])!.Value;

        var top = q.Slice(0, 100);
        var bottom = q.Slice(500, 600);

        Assert.True(top.Height > 95 && top.Height < 105);
        Assert.True(bottom.CenterY > top.CenterY + 450);
        // 5 degrees over 500 px of column: the centres drift 500 * sin(5 deg) = 43.6 px sideways
        Assert.Equal(500 * Math.Sin(5 * Math.PI / 180), Math.Abs(bottom.CenterX - top.CenterX), 3);
        Assert.Equal(q.Slice(0, 600).Height, q.Slice(0, 100).Union(q.Slice(500, 600)).Height, 1);
    }

    [Fact]
    public void A_rectangle_quad_is_the_box_with_a_margin_inside_the_bitmap()
    {
        var q = ColumnQuad.FromRect(new PixelRect(100, 50, 146, 550)).Grow(4).Clamp(300, 560);

        Assert.Equal(96, q.TopLeft.X);
        Assert.Equal(46, q.TopLeft.Y);
        Assert.Equal(150, q.BottomRight.X);
        Assert.Equal(554, q.BottomRight.Y);
        var edge = ColumnQuad.FromRect(new PixelRect(0, 0, 46, 560)).Grow(4).Clamp(300, 560);
        Assert.Equal(0, edge.TopLeft.X);
        Assert.Equal(560, edge.BottomRight.Y);
    }

    // ---------------------------------------------------------------- a column shredded into glyphs

    private static OcrLine Glyph(string c, double left, double top, double size = 46, float score = 0.9f) =>
        new(c, new PixelRect(left, top, left + size, top + size), [new OcrWord(c, new PixelRect(left, top, left + size, top + size), score)], score);

    [Fact]
    public void Glyphs_the_library_could_not_read_stacked_in_a_column_are_a_shredded_column()
    {
        // the fourth column of the book frame at native size: boxes read "?", "X", "0", nothing
        Assert.True(VerticalColumns.LooksShredded([Glyph("?", 1496, 161, 33, 0.84f), Glyph("X", 1490, 215, 46, 0.26f), Glyph("", 1495, 270, 40, 0f), Glyph("0", 1498, 320, 30, 0.48f)]));
        Assert.True(VerticalColumns.LooksShredded([Glyph("吾", 1650, 100, 46, 0.4f), Glyph("輩", 1652, 150), Glyph("は", 1649, 198), Glyph("猫", 1651, 250)])); // one poor read is enough
    }

    [Fact]
    public void A_menu_of_single_kanji_read_with_confidence_is_not_a_shredded_column()
    {
        var menu = new[] { Glyph("戻", 100, 100), Glyph("設", 100, 160), Glyph("終", 100, 220), Glyph("続", 100, 280), Glyph("新", 100, 340) };

        Assert.False(VerticalColumns.LooksShredded(menu));
        Assert.False(VerticalColumns.LooksShredded([.. menu.Select(l => l with { Text = "7" })])); // a column of confident digits
    }

    [Fact]
    public void Three_glyphs_are_not_enough_and_four_boxes_off_the_line_are_not_a_column()
    {
        Assert.False(VerticalColumns.LooksShredded([Glyph("?", 1650, 100, 46, 0.2f), Glyph("X", 1652, 150, 46, 0.2f), Glyph("0", 1649, 198, 46, 0.2f)]));
        // centres wander by half a glyph from box to box: a list of different entries, not one column
        Assert.False(VerticalColumns.LooksShredded([Glyph("?", 100, 100, 46, 0.2f), Glyph("X", 125, 150, 46, 0.2f), Glyph("0", 100, 198, 46, 0.2f), Glyph("?", 125, 250, 46, 0.2f)]));
    }

    [Fact]
    public void Glyphs_in_a_row_or_a_staircase_or_far_apart_are_not_a_column()
    {
        Assert.False(VerticalColumns.LooksShredded([Glyph("?", 100, 100, 46, 0.2f), Glyph("X", 150, 100, 46, 0.2f), Glyph("?", 200, 100, 46, 0.2f), Glyph("X", 250, 100, 46, 0.2f)]));
        Assert.False(VerticalColumns.LooksShredded([Glyph("?", 100, 100, 46, 0.2f), Glyph("X", 150, 150, 46, 0.2f), Glyph("?", 200, 200, 46, 0.2f), Glyph("X", 250, 250, 46, 0.2f)]));
        Assert.False(VerticalColumns.LooksShredded([Glyph("?", 100, 100, 46, 0.2f), Glyph("X", 100, 400, 46, 0.2f), Glyph("?", 100, 700, 46, 0.2f), Glyph("X", 100, 1000, 46, 0.2f)]));
    }

    [Fact]
    public void Wide_lines_never_count_as_a_shredded_column()
    {
        Assert.False(VerticalColumns.LooksShredded([Row("今日はいい天気ですね", 100, 100, 500), Row("散歩に行きませんか", 100, 150, 500), Row("はい喜んで", 100, 200, 500)]));
    }

    [Fact]
    public void Vertical_lines_found_already_do_not_count_as_shredded_pieces()
    {
        var done = new[] { Column(1700, 100, 400), Column(1650, 100, 400), Column(1600, 100, 400) };

        Assert.False(VerticalColumns.LooksShredded(done));
    }

    // ---------------------------------------------------------------- the model with sight stays out of columns

    private static WordHit ColumnHit(float score = 0.9f) =>
        new("天気", new PixelRect(1000, 300, 1046, 400), "今日はいい天気ですね。", "今日はいい天気ですね。", 4, Script.Han, score, Vertical: true);

    [Fact]
    public void A_vertical_hit_is_never_doubtful_for_the_horizontal_vision_pass()
    {
        var page = Page(Frame, Column(1046, 200, 700));

        Assert.False(VisionReading.Doubtful(ColumnHit(), known: false, page));
        Assert.False(VisionReading.Doubtful(ColumnHit(0.3f), known: true, page));
        Assert.True(VisionReading.Doubtful(ColumnHit() with { Vertical = false }, known: false, page));
    }

    [Fact]
    public void The_unread_tail_of_a_column_is_not_looked_for_sideways()
    {
        var line = new OcrLine("今日は", new PixelRect(1000, 300, 1046, 600), [new OcrWord("今日は", new PixelRect(1000, 300, 1020, 450), 0.9f)], 0.9f, Vertical: true);
        var hit = ColumnHit() with { Box = new PixelRect(1000, 300, 1020, 450), Line = "今日は" };

        Assert.False(VisionReading.UnreadAfter(Page(Frame, line), hit));
    }

    [Fact]
    public void A_zone_with_a_column_is_not_read_whole_by_the_model()
    {
        var zone = new PixelRect(980, 300, 1100, 700);
        var page = Page(Frame, new OcrLine("はい", new PixelRect(1000, 300, 1046, 400), [new OcrWord("はい", new PixelRect(1000, 300, 1046, 400), 0.9f)], 0.9f, Vertical: true));

        Assert.False(VisionReading.ReadWholeZone(page, 0, zone));
    }

    [Fact]
    public void Only_the_units_of_horizontal_lines_are_checked_for_a_second_reading()
    {
        var page = Page(Frame, Column(1046, 200, 700), Row("Hello", 100, 100, 100));

        var units = VisionReading.UnitsToCheck(page).ToList();

        Assert.Equal(["Hello"], units.Select(u => u.Text));
    }

    [Fact]
    public void A_stylized_scrap_next_to_a_narrow_column_still_gets_the_eyes()
    {
        var zone = new PixelRect(0, 0, 400, 100);
        var scrap = new OcrLine("想书", new PixelRect(10, 20, 90, 60), [new OcrWord("想书", new PixelRect(10, 20, 90, 60), 0.5f)], 0.5f);
        var column = new OcrLine("は", new PixelRect(350, 10, 380, 90), [new OcrWord("は", new PixelRect(350, 10, 380, 90), 0.9f)], 0.9f, Vertical: true);

        Assert.True(VisionReading.ReadWholeZone(Page(zone, scrap, column), doubtfulWords: 1, zone)); // the scrap is judged alone
        Assert.False(VisionReading.ReadWholeZone(Page(zone, column), doubtfulWords: 0, zone)); // only a column: not the eyes'
        var longColumn = column with { Text = "今日はいい天気ですね", Words = [new OcrWord("今日はいい天気ですね", column.Box, 0.9f)] };
        Assert.False(VisionReading.ReadWholeZone(Page(zone, scrap, longColumn), doubtfulWords: 1, zone)); // the column carries the text
    }

    [Fact]
    public void NearVertical_is_the_band_of_the_column_not_the_whole_screen_beside_it()
    {
        var page = Page(Frame, Column(1046, 200, 700)); // 1000..1046, 200..700, one width = 46

        Assert.True(VisionReading.NearVertical(page, 1100, 400)); // beside it, within 1.5 widths
        Assert.True(VisionReading.NearVertical(page, 1020, 710)); // just past its end
        Assert.False(VisionReading.NearVertical(page, 1100, 800)); // a label far below the column's end
        Assert.False(VisionReading.NearVertical(page, 1100, 100)); // far above
        Assert.False(VisionReading.NearVertical(page, 1200, 400)); // beyond 1.5 widths to the side
    }

    // ---------------------------------------------------------------- plates that do not cover their neighbours

    private static TextBlock Vertical(double left, double right) => new("今日", new PixelRect(left, 190, right, 740), 1, Vertical: true);

    [Fact]
    public void A_plate_widens_away_from_a_neighbour_block()
    {
        var mine = Vertical(1309, 1378);
        var neighbour = new PixelRect(1388, 190, 1532, 700);

        var plate = TextBlocks.PlateBox(mine, Frame, [neighbour]);

        Assert.Equal(240, plate.Width);
        Assert.Equal(1378, plate.Right); // grows to the left, where nothing is
        Assert.Equal((190d, 550d), (plate.Top, plate.Height));
        // and the neighbour, with the first plate placed, grows to the right
        var other = TextBlocks.PlateBox(Vertical(1388, 1532) with { Lines = 2 }, Frame, [plate, mine.Box]);
        Assert.Equal(1388, other.Left);
        Assert.True(other.Width >= 240);
    }

    [Fact]
    public void A_plate_with_nothing_near_stays_centred_as_before()
    {
        var mine = Vertical(1477, 1523);

        Assert.Equal(TextBlocks.PlateBox(mine, Frame), TextBlocks.PlateBox(mine, Frame, []));
        Assert.Equal(TextBlocks.PlateBox(mine, Frame), TextBlocks.PlateBox(mine, Frame, [new PixelRect(100, 100, 400, 140)])); // far away
    }

    [Fact]
    public void A_plate_between_two_neighbours_takes_the_smaller_overlap()
    {
        var mine = Vertical(1000, 1046);
        var left = new PixelRect(900, 190, 990, 740); // 10 px from my box
        var right = new PixelRect(1200, 190, 1300, 740); // 154 px from it

        var plate = TextBlocks.PlateBox(mine, Frame, [left, right]);

        Assert.Equal(1000, plate.Left); // grows right: 40 px over the right neighbour, against 87 centred and 90 to the left
        Assert.Equal(240, plate.Width);
    }

    [Fact]
    public void Horizontal_blocks_keep_their_box_whatever_is_near()
    {
        var row = new TextBlock("Hello there", new PixelRect(100, 100, 300, 130), 1);

        Assert.Equal(row.Box, TextBlocks.PlateBox(row, Frame, [new PixelRect(90, 90, 400, 200)]));
    }

    // ---------------------------------------------------------------- a column the first look cut short

    private static OcrLine CutColumn() => Column(1533, 321, 997, 53);

    [Fact]
    public void A_taller_box_of_the_same_column_outgrows_a_line_cut_short()
    {
        // book frame, 4th column: the first look got 321..997 of 130..1005
        Assert.True(VerticalColumns.Outgrows(new PixelRect(1475, 130, 1538, 1005), CutColumn()));
    }

    [Fact]
    public void A_box_that_is_a_neighbour_a_blob_or_barely_taller_does_not_outgrow_a_line()
    {
        var line = CutColumn();

        Assert.False(VerticalColumns.Outgrows(new PixelRect(1400, 130, 1460, 1005), line)); // the column to the left
        Assert.False(VerticalColumns.Outgrows(new PixelRect(1400, 130, 1538, 1005), line)); // two columns merged: a blob
        Assert.True(VerticalColumns.Outgrows(new PixelRect(1472, 130, 1543, 1008), line)); // the detector's own margin: 71 against 53 wide
        Assert.False(VerticalColumns.Outgrows(new PixelRect(1475, 300, 1538, 1005), line)); // a few pixels taller
        Assert.False(VerticalColumns.Outgrows(new PixelRect(1475, 130, 1538, 1005), Row("横書きの行です", 1400, 600, 300))); // not a column
    }

    // ---------------------------------------------------------------- the size of the second look

    [Fact]
    public void The_second_look_keeps_the_scale_of_the_whole_screen_in_a_window()
    {
        Assert.Equal(960, VerticalColumns.PassSide(1920, 1920)); // a whole 1080p frame: as measured
        Assert.Equal(540, VerticalColumns.PassSide(1080, 1920)); // the tall window: half size, as the whole frame
        Assert.Equal(960, VerticalColumns.PassSide(2560, 2560)); // a 1440p frame: the same 960
        Assert.Equal(405, VerticalColumns.PassSide(1080, 2560)); // its window: the same scale (0.375)
        Assert.Equal(960, VerticalColumns.PassSide(1500, 0)); // the screen is not known: the whole-frame size
        Assert.Equal(256, VerticalColumns.PassSide(200, 4000)); // never a thumbnail
    }

    // ---------------------------------------------------------------- the first page and the taller one

    [Fact]
    public void Merge_keeps_a_wide_horizontal_line_whole_and_takes_the_column_from_the_tall_page()
    {
        var firstRoi = LookupRegion.Around(1000, 500); // 100..1900 x 240..720
        var tallRoi = LookupRegion.Taller(1000, 500, Frame); // 550..1450 x 0..1080
        var wide = Row("今日はいい天気ですねとても良い天気でしょう", 300, 600, 1400);
        var cutColumn = Column(1010, 400, 720);
        var glyph = new OcrLine("?", new PixelRect(970, 500, 1008, 540), [new OcrWord("?", new PixelRect(970, 500, 1008, 540), 0.3f)], 0.3f);
        var first = Page(firstRoi, wide, glyph, cutColumn);
        var cutWide = Row("今日はいい天気ですねとても良", 550, 600, 900); // the same line cut at the tall window's sides
        var fullColumn = Column(1010, 400, 900);
        var tall = Page(tallRoi, cutWide, fullColumn);

        var merged = LookupRegion.Merge(first, tall);

        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(1400, merged.Lines.Single(l => !l.Vertical).Box.Width); // the whole line, not its cut copy
        var column = merged.Lines.Single(l => l.Vertical);
        Assert.Equal(900, column.Box.Bottom); // the whole column, not the cut one, and not the glyph beside it
        Assert.Equal(firstRoi.Union(tallRoi), merged.Region);
    }

    [Fact]
    public void Merge_adds_what_only_the_tall_page_has_and_keeps_far_columns_of_the_first()
    {
        var firstRoi = LookupRegion.Around(1000, 500);
        var tallRoi = LookupRegion.Taller(1000, 500, Frame);
        var farColumn = Column(1800, 300, 700); // outside the tall window, cut or not
        var below = Row("次の行です", 700, 900, 300); // under the first window: only the tall page sees it
        var first = Page(firstRoi, farColumn, Column(1010, 400, 720));
        var tall = Page(tallRoi, below, Column(1010, 400, 900), Column(960, 400, 900));

        var merged = LookupRegion.Merge(first, tall);

        Assert.Contains(merged.Lines, l => !l.Vertical && l.Text == "次の行です");
        var columns = merged.Lines.Where(l => l.Vertical).ToList();
        Assert.Equal(3, columns.Count);
        Assert.Equal(columns.OrderByDescending(c => c.Box.Right).Select(c => c.Box.Right), columns.Select(c => c.Box.Right)); // right to left
    }

    [Fact]
    public void Merge_of_a_page_without_a_column_in_the_tall_one_is_the_first_page()
    {
        var roi = LookupRegion.Around(1000, 500);
        var row = Row("今日はいい天気ですね", 300, 600, 500);

        var merged = LookupRegion.Merge(Page(roi, row), Page(LookupRegion.Taller(1000, 500, Frame), row with { Box = row.Box }));

        Assert.Single(merged.Lines);
    }

    // ---------------------------------------------------------------- the quadrilateral: degenerate input and margins at an edge

    [Fact]
    public void Fit_gives_null_for_nothing_to_fit()
    {
        Assert.Null(ColumnQuad.Fit([]));
        Assert.Null(ColumnQuad.Fit([[(0, 0), (10, 10)]])); // two corners are not a quadrilateral
        Assert.Null(ColumnQuad.Fit([[(5, 5), (5, 5), (5, 5), (5, 5)]])); // one point
        Assert.Null(ColumnQuad.Fit([[(5, 0), (5, 0), (5, 100), (5, 100)]])); // a line: no width
    }

    [Fact]
    public void Fit_of_pieces_that_lean_differently_takes_the_mean_lean_and_holds_them_all()
    {
        var a = Lean(500, 300, 46, 300, 3);
        var b = Lean(500, 700, 46, 300, 7);

        var q = ColumnQuad.Fit([a, b])!.Value;

        Assert.InRange(q.TiltDegrees, 4.5, 5.5);
        Assert.True(q.Length > 650);
        // every corner of both pieces is inside the fitted rectangle (as seen along its axes)
        foreach (var (x, y) in a.Concat(b))
        {
            var (ux, uy) = ((q.BottomLeft.X - q.TopLeft.X) / q.Length, (q.BottomLeft.Y - q.TopLeft.Y) / q.Length);
            var (vx, vy) = ((q.TopRight.X - q.TopLeft.X) / q.Width, (q.TopRight.Y - q.TopLeft.Y) / q.Width);
            var (dx, dy) = (x - q.TopLeft.X, y - q.TopLeft.Y);
            Assert.InRange(dx * ux + dy * uy, -1e-6, q.Length + 1e-6);
            Assert.InRange(dx * vx + dy * vy, -1e-6, q.Width + 1e-6);
        }
    }

    [Fact]
    public void A_column_touching_the_bitmap_edge_gets_no_margin_there_and_stays_a_rectangle()
    {
        var q = ColumnQuad.Fit([Lean(100, 302, 46, 600, 5)])!.Value; // the top corners are at y 1 and 5: the top margin cannot be 4
        var (grown, top, bottom) = q.GrowWithin(4, 400, 1000);

        Assert.True(top < 4);
        Assert.Equal(4, bottom);
        Assert.True(grown.TopLeft.Y >= -1e-9 && grown.TopRight.Y >= -1e-9);
        // still a rectangle: opposite sides equal and parallel
        Assert.Equal(grown.TopRight.X - grown.TopLeft.X, grown.BottomRight.X - grown.BottomLeft.X, 6);
        Assert.Equal(grown.TopRight.Y - grown.TopLeft.Y, grown.BottomRight.Y - grown.BottomLeft.Y, 6);
        Assert.Equal(grown.BottomLeft.X - grown.TopLeft.X, grown.BottomRight.X - grown.TopRight.X, 6);
        // the margin reported is the real distance along the axis from the grown top to the column's top
        Assert.Equal(top, grown.Length - q.Length - bottom, 6);
        var free = ColumnQuad.Fit([Lean(100, 400, 46, 600, 5)])!.Value.GrowWithin(4, 4000, 4000);
        Assert.Equal((4d, 4d), (free.Top, free.Bottom));
    }

    // ---------------------------------------------------------------- the sampling of a column into the model input

    private static (byte[] Bgra, int Stride) Canvas(int w, int h, Action<SkiaSharp.SKCanvas> draw, byte gray = 128)
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque));
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(new SkiaSharp.SKColor(gray, gray, gray));
        draw(canvas);
        return (bmp.Bytes, bmp.RowBytes);
    }

    private static float[] Sample(byte[] bgra, int stride, int w, int h, ColumnQuad quad, out int rw, out int inputW)
    {
        rw = (int)Math.Ceiling(48 * quad.Length / quad.Width);
        inputW = Math.Max(320, rw);
        var dst = new float[3 * 48 * inputW];
        VerticalReader.Fill(dst, bgra, stride, w, h, quad, rw, inputW);
        return dst;
    }

    private static SkiaSharp.SKPaint Paint(byte v) => new() { Color = new SkiaSharp.SKColor(v, v, v), IsAntialias = true };

    [Fact]
    public void Fill_lays_the_column_on_its_side_top_to_the_left_and_right_edge_up()
    {
        // a white column 100..146 x 50..450 with a black band across its top and a black strip along its right edge
        var (bgra, stride) = Canvas(300, 500, c =>
        {
            using var white = Paint(255);
            using var black = Paint(0);
            c.DrawRect(100, 50, 46, 400, white);
            c.DrawRect(100, 50, 46, 40, black);
            c.DrawRect(140, 50, 6, 400, black);
        });
        var quad = ColumnQuad.FromRect(new PixelRect(100, 50, 146, 450));

        var dst = Sample(bgra, stride, 300, 500, quad, out var rw, out var inputW);

        float At(int y, int x) => dst[y * inputW + x]; // channel 0
        Assert.True(At(24, 10) < -0.9f, "the top of the column is at the left");
        Assert.True(At(24, rw - 10) > 0.9f, "the bottom of the column is at the right");
        Assert.True(At(1, rw / 2) < -0.9f, "the right edge of the column is the top row");
        Assert.True(At(46, rw / 2) > 0.9f, "the left edge of the column is the bottom row");
        if (inputW > rw) Assert.Equal(0f, At(24, inputW - 1)); // the padding to the right stays 0
    }

    [Fact]
    public void Fill_follows_a_column_leaning_five_degrees()
    {
        var (bgra, stride) = Canvas(400, 700, c =>
        {
            using var white = Paint(255);
            using var black = Paint(0);
            c.Save();
            c.RotateDegrees(5, 200, 350);
            c.DrawRect(177, 50, 46, 600, white);
            c.DrawRect(177, 50, 46, 60, black); // the top band
            c.Restore();
        });
        var corners = new[] { (177.0, 50.0), (223.0, 50.0), (223.0, 650.0), (177.0, 650.0) }.Select(pt =>
        {
            var a = 5 * Math.PI / 180;
            var (dx, dy) = (pt.Item1 - 200, pt.Item2 - 350);
            return (200 + dx * Math.Cos(a) - dy * Math.Sin(a), 350 + dx * Math.Sin(a) + dy * Math.Cos(a));
        }).ToArray();
        var quad = ColumnQuad.Fit([corners])!.Value;

        var dst = Sample(bgra, stride, 400, 700, quad, out var rw, out var inputW);

        float At(int y, int x) => dst[y * inputW + x];
        Assert.Equal(5, quad.TiltDegrees, 1);
        Assert.True(At(24, (int)(rw * 20.0 / 600)) < -0.9f, "the band at the top, whatever the lean");
        Assert.True(At(24, (int)(rw * 0.5)) > 0.9f, "white in the middle");
        Assert.True(At(4, (int)(rw * 0.9)) > 0.9f && At(44, (int)(rw * 0.9)) > 0.9f, "white right across the strip near the bottom: the lean is taken out");
    }

    [Fact]
    public void Fill_samples_at_pixel_centres_along_the_column()
    {
        // a gradient along the column: row y is 2 * y
        var (bgra, stride) = Canvas(40, 100, c =>
        {
            for (var y = 0; y < 100; y++)
            {
                using var line = Paint((byte)(2 * y));
                line.IsAntialias = false;
                c.DrawRect(10, y, 4, 1, line);
            }
        });
        var quad = ColumnQuad.FromRect(new PixelRect(10, 0, 14, 100));

        var dst = Sample(bgra, stride, 40, 100, quad, out var rw, out var inputW);

        // strip x is at a = (x + 0.5) / rw of the column, whose pixel centres are at y + 0.5: value = 2 * (a * 100 - 0.5)
        foreach (var x in new[] { rw / 4, rw / 2, rw * 3 / 4 })
        {
            var expected = 2 * ((x + 0.5) / rw * 100 - 0.5);
            Assert.Equal(expected / 255f * 2 - 1, dst[24 * inputW + x], 0.01);
        }
        Assert.True(dst[24 * inputW] < dst[24 * inputW + rw - 1]); // the top is the left
    }

    [Fact]
    public void Fill_clamps_at_the_bitmap_edge_instead_of_reading_outside()
    {
        var (bgra, stride) = Canvas(60, 200, _ => { }, gray: 200);
        var quad = ColumnQuad.FromRect(new PixelRect(-10, -10, 30, 150)).Grow(4); // sticks out on the left and the top

        var dst = Sample(bgra, stride, 60, 200, quad, out var rw, out var inputW);

        var expected = (200 / 255f - 0.5f) / 0.5f;
        for (var x = 0; x < rw; x += 37)
            for (var y = 0; y < 48; y += 7)
                Assert.Equal(expected, dst[y * inputW + x], 0.01); // the edge pixel repeated, nothing else
    }

    // ---------------------------------------------------------------- the second look's replacement of a cut column

    private sealed class ColumnReader(params (PixelRect Box, string Text)[] known)
    {
        public VerticalRead? Read(PixelRect box, IReadOnlyList<PixelRect> pieces)
        {
            foreach (var (b, text) in known)
                if (Math.Abs(b.Left - box.Left) < 1 && Math.Abs(b.Top - box.Top) < 1 && Math.Abs(b.Bottom - box.Bottom) < 1)
                    return new VerticalRead(text, text.Select((c, i) => new OcrWord(c.ToString(), new PixelRect(box.Left, box.Top + i * 50, box.Right, box.Top + (i + 1) * 50), 0.9f)).ToList(), 0.9f);
            return null;
        }
    }

    private static OcrLine Blank(PixelRect box) => new("", box, [], 0f);

    [Fact]
    public void A_longer_box_replaces_the_cut_column_and_the_columns_stay_right_to_left()
    {
        var right = Column(1600, 100, 600);
        var old = Column(1533, 321, 997, 53); // the first look's cut copy of the fourth column
        var left = Column(1400, 100, 600);
        var longer = new PixelRect(1472, 130, 1543, 1008);
        var reader = new ColumnReader((longer, "ニャーニャー泣いていた"));

        var result = VerticalColumns.ReplaceOutgrown([right, left, old], [(old, longer)], [Blank(longer)], reader.Read, 0.5f, null);

        var columns = result.Where(l => l.Vertical).ToList();
        Assert.Equal(3, columns.Count);
        Assert.DoesNotContain(columns, c => ReferenceEquals(c, old));
        Assert.Equal("ニャーニャー泣いていた", columns[1].Text); // between the right column and the left one
        Assert.Equal(columns.OrderByDescending(c => c.Box.Right).Select(c => c.Box.Right), columns.Select(c => c.Box.Right));
    }

    [Fact]
    public void When_the_longer_box_does_not_read_the_old_line_comes_back_in_its_place_among_the_columns()
    {
        var right = Column(1600, 100, 600);
        var old = Column(1533, 321, 997, 53);
        var left = Column(1400, 100, 600);
        var longer = new PixelRect(1472, 130, 1543, 1008);
        var row = Row("横書き", 100, 700, 200);

        var result = VerticalColumns.ReplaceOutgrown([row, right, left, old], [(old, longer)], [Blank(longer)], new ColumnReader().Read, 0.5f, null);

        Assert.Same(row, result[0]); // rows first
        var columns = result.Where(l => l.Vertical).ToList();
        Assert.Equal(3, columns.Count);
        Assert.Same(old, columns[1]); // not at the end, where it was put back
        Assert.Equal(columns.OrderByDescending(c => c.Box.Right).Select(c => c.Box.Right), columns.Select(c => c.Box.Right));
    }
}
