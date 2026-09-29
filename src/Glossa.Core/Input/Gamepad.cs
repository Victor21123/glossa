namespace Glossa.Core.Input;

/// <summary>
/// Gamepad buttons with Xbox names, whatever the pad: XInput reports these directly, PlayStation and Switch pads are
/// mapped by position (the bottom face button is A). Buttons of an unknown pad keep their numbers (Button1…).
/// The left stick counts as four extra «buttons», used to move between words but never part of a combination.
/// </summary>
[Flags]
public enum PadButtons : ulong
{
    None = 0,
    DPadUp = 1UL << 0, DPadDown = 1UL << 1, DPadLeft = 1UL << 2, DPadRight = 1UL << 3,
    Start = 1UL << 4, Back = 1UL << 5, L3 = 1UL << 6, R3 = 1UL << 7, LB = 1UL << 8, RB = 1UL << 9,
    A = 1UL << 12, B = 1UL << 13, X = 1UL << 14, Y = 1UL << 15, LT = 1UL << 16, RT = 1UL << 17,
    StickUp = 1UL << 18, StickDown = 1UL << 19, StickLeft = 1UL << 20, StickRight = 1UL << 21,
    Button1 = 1UL << 32,
}

public static class Pad
{
    public const PadButtons Stick = PadButtons.StickUp | PadButtons.StickDown | PadButtons.StickLeft | PadButtons.StickRight;
    public const PadButtons Up = PadButtons.DPadUp | PadButtons.StickUp;
    public const PadButtons Down = PadButtons.DPadDown | PadButtons.StickDown;
    public const PadButtons Left = PadButtons.DPadLeft | PadButtons.StickLeft;
    public const PadButtons Right = PadButtons.DPadRight | PadButtons.StickRight;

    /// <summary>Generic buttons of an unknown pad: Button1 … Button16.</summary>
    public static PadButtons Button(int number) => number is >= 1 and <= 16 ? (PadButtons)((ulong)PadButtons.Button1 << (number - 1)) : PadButtons.None;

    private static readonly (PadButtons Button, string Name)[] Names =
    [
        (PadButtons.LB, "LB"), (PadButtons.RB, "RB"), (PadButtons.LT, "LT"), (PadButtons.RT, "RT"),
        (PadButtons.A, "A"), (PadButtons.B, "B"), (PadButtons.X, "X"), (PadButtons.Y, "Y"),
        (PadButtons.Back, "View"), (PadButtons.Start, "Menu"), (PadButtons.L3, "L3"), (PadButtons.R3, "R3"),
        (PadButtons.DPadUp, "Up"), (PadButtons.DPadDown, "Down"), (PadButtons.DPadLeft, "Left"), (PadButtons.DPadRight, "Right"),
    ];

    /// <summary>"LB+RB": the stored form of a combination, shoulders first.</summary>
    public static string Format(PadButtons buttons)
    {
        var parts = Names.Where(n => buttons.HasFlag(n.Button)).Select(n => n.Name).ToList();
        for (var i = 1; i <= 16; i++)
            if (buttons.HasFlag(Button(i))) parts.Add("B" + i);
        return string.Join("+", parts);
    }

    /// <summary>Parses "LB+RB" (and "B5+B6" of an unknown pad); None when empty or not understood.</summary>
    public static PadButtons Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PadButtons.None;
        var result = PadButtons.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var named = Names.FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (named.Name is not null) result |= named.Button;
            else if (part.Length > 1 && (part[0] is 'B' or 'b') && int.TryParse(part[1..], out var n) && Button(n) is var b and not PadButtons.None) result |= b;
            else return PadButtons.None;
        }
        return result;
    }

    /// <summary>The buttons of a combination as key caps show them: ["LB", "RB"], ["Кнопка 5"].</summary>
    public static IReadOnlyList<string> Captions(PadButtons buttons) =>
        Format(buttons).Split('+', StringSplitOptions.RemoveEmptyEntries).Select(p => p.StartsWith('B') && p.Length > 1 && char.IsDigit(p[1]) ? "Кнопка " + p[1..] : p).ToList();

    public static int Count(PadButtons buttons) => System.Numerics.BitOperations.PopCount((ulong)(buttons & ~Stick));

    /// <summary>A stick position (-1…1 on each axis, up positive) as direction «buttons», with a dead zone.</summary>
    public static PadButtons StickDirections(double x, double y, double deadZone = 0.5)
    {
        var result = PadButtons.None;
        if (x <= -deadZone) result |= PadButtons.StickLeft;
        if (x >= deadZone) result |= PadButtons.StickRight;
        if (y >= deadZone) result |= PadButtons.StickUp;
        if (y <= -deadZone) result |= PadButtons.StickDown;
        return result;
    }

    /// <summary>A HID hat switch (0 up, clockwise in eighths; anything else is centred) as the D-pad.</summary>
    public static PadButtons Hat(int value) => value switch
    {
        0 => PadButtons.DPadUp,
        1 => PadButtons.DPadUp | PadButtons.DPadRight,
        2 => PadButtons.DPadRight,
        3 => PadButtons.DPadDown | PadButtons.DPadRight,
        4 => PadButtons.DPadDown,
        5 => PadButtons.DPadDown | PadButtons.DPadLeft,
        6 => PadButtons.DPadLeft,
        7 => PadButtons.DPadUp | PadButtons.DPadLeft,
        _ => PadButtons.None,
    };

    /// <summary>
    /// A HID pad's numbered button by its maker: Sony (DualShock 4, DualSense) and Nintendo (Switch Pro) by the position
    /// of the button, so «A» is always the bottom face button; other pads keep the number.
    /// </summary>
    public static PadButtons FromHid(int vendorId, int button) => vendorId switch
    {
        0x054C => button switch
        {
            1 => PadButtons.X, 2 => PadButtons.A, 3 => PadButtons.B, 4 => PadButtons.Y,
            5 => PadButtons.LB, 6 => PadButtons.RB, 7 => PadButtons.LT, 8 => PadButtons.RT,
            9 => PadButtons.Back, 10 => PadButtons.Start, 11 => PadButtons.L3, 12 => PadButtons.R3,
            _ => PadButtons.None, // PS button, touchpad, mute
        },
        0x057E => button switch
        {
            1 => PadButtons.A, 2 => PadButtons.B, 3 => PadButtons.X, 4 => PadButtons.Y,
            5 => PadButtons.LB, 6 => PadButtons.RB, 7 => PadButtons.LT, 8 => PadButtons.RT,
            9 => PadButtons.Back, 10 => PadButtons.Start, 11 => PadButtons.L3, 12 => PadButtons.R3,
            _ => PadButtons.None, // Home, Capture
        },
        _ => Button(button),
    };
}

/// <summary>Fires once when every button of the combination is down, and again only after it was let go.</summary>
public sealed class ComboWatcher
{
    private bool _held;

    public PadButtons Combo { get; set; }

    public bool Update(PadButtons state)
    {
        var down = Combo != PadButtons.None && (state & Combo) == Combo;
        var fired = down && !_held;
        _held = down;
        return fired;
    }
}

/// <summary>
/// «Записать»: the buttons held together, taken when all are let go. One button alone is refused — a game uses each of
/// its buttons, so a single one would open Glossa in the middle of play.
/// </summary>
public sealed class ComboRecorder
{
    private PadButtons _seen;

    /// <summary>The combination once recorded, else null; <paramref name="error"/> says why a press was not taken.</summary>
    public PadButtons? Update(PadButtons state, out string? error)
    {
        error = null;
        var buttons = state & ~Pad.Stick;
        _seen |= buttons;
        if (buttons != PadButtons.None || _seen == PadButtons.None) return null;
        var taken = _seen;
        _seen = PadButtons.None;
        if (Pad.Count(taken) < 2)
        {
            error = $"{string.Join(" + ", Pad.Captions(taken))} - одна кнопка; нужно сочетание из двух, например LB + RB.";
            return null;
        }
        return taken;
    }
}

/// <summary>Key repeat for held directions: a step at once, then after 350 ms every 90 ms.</summary>
public sealed class PadRepeat
{
    private PadButtons _held;
    private long _nextAt;

    /// <summary>The directions to step now (at most one of up/down/left/right).</summary>
    public PadButtons Step(PadButtons state, long nowMs)
    {
        var dir = state & (Pad.Up | Pad.Down | Pad.Left | Pad.Right);
        var one = (dir & Pad.Up) != 0 ? Pad.Up : (dir & Pad.Down) != 0 ? Pad.Down : (dir & Pad.Left) != 0 ? Pad.Left : (dir & Pad.Right) != 0 ? Pad.Right : PadButtons.None;
        if (one == PadButtons.None)
        {
            _held = PadButtons.None;
            return PadButtons.None;
        }
        if (one != _held)
        {
            _held = one;
            _nextAt = nowMs + 350;
            return one;
        }
        if (nowMs < _nextAt) return PadButtons.None;
        _nextAt = nowMs + 90;
        return one;
    }
}
