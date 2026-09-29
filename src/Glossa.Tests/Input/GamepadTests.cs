using Glossa.Core.Input;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Input;

public class GamepadTests
{
    [Fact]
    public void Combination_round_trips_and_reads_as_key_caps()
    {
        var combo = PadButtons.RB | PadButtons.LB;
        Assert.Equal("LB+RB", Pad.Format(combo));
        Assert.Equal(combo, Pad.Parse("lb + RB"));
        Assert.Equal(Pad.Button(5) | Pad.Button(6), Pad.Parse("B5+B6"));
        Assert.Equal(["Кнопка 5", "Кнопка 6"], Pad.Captions(Pad.Parse("B5+B6")));
        Assert.Equal(PadButtons.None, Pad.Parse("LB+Jump"));
        Assert.Equal(PadButtons.None, Pad.Parse(""));
    }

    [Fact]
    public void Combination_fires_once_per_press()
    {
        var w = new ComboWatcher { Combo = PadButtons.LB | PadButtons.RB };
        Assert.False(w.Update(PadButtons.LB));
        Assert.True(w.Update(PadButtons.LB | PadButtons.RB));
        Assert.False(w.Update(PadButtons.LB | PadButtons.RB | PadButtons.A)); // still held
        Assert.False(w.Update(PadButtons.RB));
        Assert.True(w.Update(PadButtons.LB | PadButtons.RB));
    }

    [Fact]
    public void Recording_takes_the_buttons_held_together_and_refuses_one()
    {
        var r = new ComboRecorder();
        Assert.Null(r.Update(PadButtons.LB, out _));
        Assert.Null(r.Update(PadButtons.LB | PadButtons.RB | PadButtons.StickLeft, out _));
        Assert.Null(r.Update(PadButtons.RB, out _));
        Assert.Equal(PadButtons.LB | PadButtons.RB, r.Update(PadButtons.None, out var error));
        Assert.Null(error);

        Assert.Null(r.Update(PadButtons.A, out _));
        Assert.Null(r.Update(PadButtons.None, out error));
        Assert.Contains("одна кнопка", error);
    }

    [Fact]
    public void Sony_and_nintendo_buttons_map_by_position()
    {
        Assert.Equal(PadButtons.A, Pad.FromHid(0x054C, 2));   // Cross
        Assert.Equal(PadButtons.B, Pad.FromHid(0x054C, 3));   // Circle
        Assert.Equal(PadButtons.LB, Pad.FromHid(0x054C, 5));  // L1
        Assert.Equal(PadButtons.None, Pad.FromHid(0x054C, 13)); // PS button
        Assert.Equal(PadButtons.A, Pad.FromHid(0x057E, 1));   // Switch «B» sits where Xbox «A» does
        Assert.Equal(Pad.Button(7), Pad.FromHid(0x1234, 7));
        Assert.Equal(PadButtons.DPadDown | PadButtons.DPadLeft, Pad.Hat(5));
        Assert.Equal(PadButtons.None, Pad.Hat(8));
        Assert.Equal(PadButtons.StickUp | PadButtons.StickRight, Pad.StickDirections(0.7, 0.9));
        Assert.Equal(PadButtons.None, Pad.StickDirections(0.3, -0.2));
    }

    [Fact]
    public void Held_direction_repeats_after_a_pause()
    {
        var r = new PadRepeat();
        Assert.Equal(Pad.Right, r.Step(PadButtons.DPadRight, 0));
        Assert.Equal(PadButtons.None, r.Step(PadButtons.DPadRight, 200));
        Assert.Equal(Pad.Right, r.Step(PadButtons.StickRight, 360)); // the stick counts as the same direction
        Assert.Equal(PadButtons.None, r.Step(PadButtons.DPadRight, 400));
        Assert.Equal(Pad.Right, r.Step(PadButtons.DPadRight, 455));
        Assert.Equal(PadButtons.None, r.Step(PadButtons.None, 500));
        Assert.Equal(Pad.Down, r.Step(PadButtons.DPadDown, 510));
    }

    private static OcrLine Latin(string text, double top, double left = 100)
    {
        var words = new List<OcrWord>();
        var x = left;
        foreach (var w in text.Split(' '))
        {
            words.Add(new OcrWord(w, new PixelRect(x, top, x + w.Length * 10, top + 20), 1));
            x += w.Length * 10 + 10;
        }
        return new OcrLine(text, new PixelRect(left, top, x - 10, top + 20), words, 1);
    }

    private static OcrLine Cjk(string text, double top)
    {
        var words = text.Select((c, i) => new OcrWord(c.ToString(), new PixelRect(100 + i * 20, top, 120 + i * 20, top + 20), 1)).ToList();
        return new OcrLine(text, new PixelRect(100, top, 100 + text.Length * 20, top + 20), words, 1);
    }

    /// <summary>Two-character words from the start of the line: 今日 / は、 / 天気.</summary>
    private sealed class Pairs : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index) => (index - index % 2, Math.Min(2, text.Length - (index - index % 2)));
    }

    [Fact]
    public void Still_frame_starts_at_the_biggest_block_and_steps_word_by_word()
    {
        var page = new OcrPage(
        [
            Latin("HP 120", 10, 2000),
            Latin("You should reconsider", 800),
            Latin("your position, mortal.", 822),
            Latin("Menu", 1300),
        ], new PixelRect(0, 0, 2560, 1440), TimeSpan.Zero);
        var nav = FrameWords.Build(page, null);
        Assert.Equal("You", nav.Current!.Text);
        Assert.True(nav.Move(PadButtons.DPadRight));
        Assert.True(nav.Move(PadButtons.StickRight));
        Assert.Equal("reconsider", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.DPadRight)); // on to the next line
        Assert.Equal("your", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.DPadLeft));
        Assert.Equal("reconsider", nav.Current.Text);
        Assert.True(nav.Move(PadButtons.DPadDown));
        Assert.Equal(822, nav.Current.Box.Top);
        Assert.True(nav.Move(PadButtons.DPadUp));
        Assert.True(nav.Move(PadButtons.DPadUp)); // the HUD line far to the right is still reachable
        Assert.Equal("HP", nav.Current.Text);
        Assert.False(nav.Move(PadButtons.DPadUp));
    }

    [Fact]
    public void Still_frame_steps_over_japanese_words_not_characters()
    {
        var page = new OcrPage([Cjk("今日は、天気", 500)], new PixelRect(0, 0, 1920, 1080), TimeSpan.Zero);
        var nav = FrameWords.Build(page, new Pairs());
        Assert.Equal("今日", nav.Current!.Text);
        Assert.Equal(new PixelRect(100, 500, 140, 520), nav.Current.Box);
        Assert.True(nav.Move(PadButtons.DPadRight));
        Assert.Equal("は", nav.Current.Text); // the matched «は、» stops before the comma, which is skipped
        Assert.True(nav.Move(PadButtons.DPadRight));
        Assert.Equal("天気", nav.Current.Text);
        Assert.False(nav.Move(PadButtons.DPadRight));
    }

    [Fact]
    public void Still_frame_starts_at_the_lowest_real_text_not_at_a_hud_label()
    {
        var page = new OcrPage(
        [
            Latin("An old line of the log that is long enough to count", 100),
            Latin("and goes on for a second line of the same old story", 122),
            Latin("Be seeing you, officer.", 900),
            Latin("Day 3", 1350),
        ], new PixelRect(0, 0, 2560, 1440), TimeSpan.Zero);
        Assert.Equal("Be", FrameWords.Build(page, null).Current!.Text);
    }

    [Fact]
    public void Empty_frame_has_nothing_to_choose()
    {
        var nav = FrameWords.Build(OcrPage.Empty(new PixelRect(0, 0, 10, 10)), null);
        Assert.Null(nav.Current);
        Assert.False(nav.Move(PadButtons.DPadRight));
    }
}
