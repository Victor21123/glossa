using Glossa.Core.Ocr;

namespace Glossa.Core.Games;

/// <summary>
/// A game in exclusive full screen and Glossa's card (research 2026-09-30, measured with a test Direct3D 11 program):
/// Windows reports such a game (state 3, a Direct3D program holds the screen) both when it really owns the display and
/// when "full screen optimizations" keep it a borderless window underneath. Only in the first case does a topmost card
/// over it take the game out of full screen: 0.25 s after the card shows the state is 2 and no window is in front. A card
/// on another monitor leaves it be.
/// </summary>
public static class FullScreen
{
    /// <summary>SHQueryUserNotificationState: a full-screen Direct3D program holds the screen.</summary>
    public const int Direct3DFullScreen = 3;

    /// <summary>How long after the card shows the game is looked at again (it was out of full screen within 0.25 s).</summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The card took the game out of full screen: it held the screen when the card was shown, and now Windows no longer
    /// reports a full-screen program and the game is not in front.
    /// </summary>
    public static bool KnockedOut(bool heldScreen, int stateNow, bool gameInFront) =>
        heldScreen && stateNow != Direct3DFullScreen && !gameInFront;

    /// <summary>The monitor to show the card on instead of the game's: the biggest other one; null with one monitor.</summary>
    public static PixelRect? OtherMonitor(PixelRect gameMonitor, IEnumerable<PixelRect> monitors) =>
        monitors.Where(m => !m.Contains(gameMonitor.CenterX, gameMonitor.CenterY))
            .OrderByDescending(m => m.Width * m.Height)
            .Select(m => (PixelRect?)m)
            .FirstOrDefault();

    /// <summary>A box on one monitor put at the same relative place on another (the word's spot, where the card goes).</summary>
    public static PixelRect Carry(PixelRect box, PixelRect from, PixelRect to)
    {
        double sx = to.Width / Math.Max(1, from.Width), sy = to.Height / Math.Max(1, from.Height);
        return new PixelRect(to.Left + (box.Left - from.Left) * sx, to.Top + (box.Top - from.Top) * sy,
            to.Left + (box.Right - from.Left) * sx, to.Top + (box.Bottom - from.Top) * sy);
    }
}
