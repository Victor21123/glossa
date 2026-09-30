using Glossa.Core.Games;
using Glossa.Core.Ocr;

namespace Glossa.Tests.Games;

public class FullScreenTests
{
    private static readonly PixelRect Main = new(0, 0, 2560, 1440);
    private static readonly PixelRect Left = new(-1920, 0, 0, 1080);

    [Fact]
    public void The_card_knocked_the_game_out_only_when_it_held_the_screen_and_lost_it()
    {
        // Measured 2026-09-30: state 3 -> 2 and no window in front 0.25 s after the card showed.
        Assert.True(FullScreen.KnockedOut(heldScreen: true, stateNow: 2, gameInFront: false));
        // Full screen optimizations: the card is composed over the game, which keeps the screen and the focus.
        Assert.False(FullScreen.KnockedOut(heldScreen: true, stateNow: 3, gameInFront: true));
        // A borderless or windowed game was never holding the screen.
        Assert.False(FullScreen.KnockedOut(heldScreen: false, stateNow: 2, gameInFront: false));
        // The user switched to another program: the game is not in front, but it still reports full screen.
        Assert.False(FullScreen.KnockedOut(heldScreen: true, stateNow: 3, gameInFront: false));
    }

    [Fact]
    public void The_other_monitor_is_the_biggest_one_not_under_the_game()
    {
        var small = new PixelRect(2560, 0, 3840, 1024);
        Assert.Equal(Left, FullScreen.OtherMonitor(Main, [Main, small, Left]));
        Assert.Equal(Main, FullScreen.OtherMonitor(Left, [Main, Left]));
        Assert.Null(FullScreen.OtherMonitor(Main, [Main]));
    }

    [Fact]
    public void The_words_spot_is_carried_to_the_same_place_on_the_other_monitor()
    {
        var word = new PixelRect(1280, 720, 1408, 756); // the middle of the main monitor
        var carried = FullScreen.Carry(word, Main, Left);

        Assert.Equal(-960, carried.Left);
        Assert.Equal(540, carried.Top);
        Assert.Equal(-960 + 128 * 0.75, carried.Right, 6);
        Assert.Equal(540 + 36 * 0.75, carried.Bottom, 6);
    }
}
