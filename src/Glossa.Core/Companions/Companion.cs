namespace Glossa.Core.Companions;

/// <summary>
/// The frames of a companion, by name: <c>&lt;id&gt;_&lt;frame&gt;.png</c> in its folder of the pack. Strings, not an
/// enum: later animation loops of the legendaries add frames without a code change.
/// </summary>
public static class CompanionFrames
{
    public const string Base = "base";
    public const string Blink = "blink";
    public const string Breath = "breath";
    public const string Happy = "happy";
    public const string Sad = "sad";
    public const string Tired = "tired";
    public const string Sleep = "sleep";

    /// <summary>A mood's breath-in frame: the mood's face on the breath frame (made by the art tools, 2026-10-03).</summary>
    public static string BreathOf(string mood) => mood == Base ? Breath : mood + "_" + Breath;

    /// <summary>Every frame an approved companion has.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Base, Blink, Breath, Happy, Sad, Tired, Sleep, BreathOf(Happy), BreathOf(Sad), BreathOf(Tired)];
}

/// <summary>
/// A place on the companion that answers a click with lines of its own, "poke.&lt;name&gt;" (the user, 2026-10-03:
/// "забавные места для клика"): rectangles in the base frame's pixels.
/// </summary>
public sealed record CompanionZone(string Name, IReadOnlyList<(int X, int Y, int W, int H)> Boxes)
{
    public bool Holds(int x, int y) => Boxes.Any(b => x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H);
}

/// <summary>
/// One companion of the catalog: a game character in pixel art. <see cref="Gender"/> ("f"/"m") is the grammatical
/// gender for phrases about the character (decided 2026-09-29); <see cref="Width"/> and <see cref="Height"/> are the
/// sprite's size in its own pixels, the same for all its frames. The frames are PNG bytes from the encrypted pack
/// (<see cref="CompanionPack"/>), never files beside the exe.
/// </summary>
public sealed record Companion(string Id, Rarity Rarity, string Name, string Game, string Gender, int Width, int Height,
    IReadOnlyDictionary<string, byte[]> Frames,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Phrases = null, IReadOnlyList<CompanionZone>? Zones = null)
{
    public bool Has(string frame) => Frames.ContainsKey(frame);

    /// <summary>The zone at a pixel of the base frame (the first in the manifest's order that holds it), or null.</summary>
    public string? ZoneAt(int x, int y) => Zones?.FirstOrDefault(z => z.Holds(x, y))?.Name;

    /// <summary>A frame's PNG bytes; the base frame's for one the companion has not got.</summary>
    public byte[] Frame(string frame) => Frames.TryGetValue(frame, out var png) ? png : Frames[CompanionFrames.Base];
}
