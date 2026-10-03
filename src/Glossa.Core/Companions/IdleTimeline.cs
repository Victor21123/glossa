namespace Glossa.Core.Companions;

/// <summary>One frame of the idle and how long it stays; 0 ms - it stays until the mood changes.</summary>
public sealed record IdleStep(string Frame, int Ms);

/// <summary>
/// The companion's idle, planned here and only played by the window: an even breath, 0.7 s each way, the rest on the
/// mood's face and the breath-in on that face lifted a row (the approved idle of 2026-09-30); a calm rest may hold a
/// blink (350 + 150 + 200 ms, keeping the rhythm). Only the calm face blinks: the moods have no closed-eye frames yet.
/// The sleep holds still.
/// </summary>
public static class IdleTimeline
{
    public const int BreathMs = 700;

    public static string FaceOf(Mood mood) => mood switch
    {
        Mood.Happy => CompanionFrames.Happy,
        Mood.Sad => CompanionFrames.Sad,
        Mood.Tired => CompanionFrames.Tired,
        Mood.Asleep => CompanionFrames.Sleep,
        _ => CompanionFrames.Base,
    };

    /// <summary>One breath in a mood: the rest, then the breath-in.</summary>
    public static IReadOnlyList<IdleStep> Breath(Mood mood, bool blink)
    {
        if (mood == Mood.Asleep)
            return [new IdleStep(CompanionFrames.Sleep, 0)];
        var face = FaceOf(mood);
        var inhale = new IdleStep(CompanionFrames.BreathOf(face), BreathMs);
        if (blink && mood == Mood.Calm)
            return [new IdleStep(face, 350), new IdleStep(CompanionFrames.Blink, 150), new IdleStep(face, 200), inhale];
        return [new IdleStep(face, BreathMs), inhale];
    }

    /// <summary>Whether this rest holds a blink: one in three after a rest without one, never two in a row.</summary>
    public static bool Blinks(Random rng, bool lastBlinked) => !lastBlinked && rng.Next(3) == 0;
}
