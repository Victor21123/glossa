using Glossa.Core.Ocr;

namespace Glossa.Core.Games;

public enum WindowMode
{
    Unknown,
    /// <summary>A window with a frame: the card shows over it.</summary>
    Windowed,
    /// <summary>A frameless window over the whole monitor: the card shows over it.</summary>
    Borderless,
    /// <summary>Exclusive full screen: the game owns the display and the card may stay hidden behind it.</summary>
    Exclusive,
}

public static class WindowModes
{
    /// <summary>
    /// How a game's window sits on its monitor. <paramref name="exclusive"/> is Windows' own verdict that a Direct3D
    /// program holds the screen exclusively (SHQueryUserNotificationState).
    /// </summary>
    public static WindowMode Classify(PixelRect window, PixelRect monitor, bool hasCaption, bool hasSizingFrame, bool exclusive)
    {
        if (exclusive) return WindowMode.Exclusive;
        if (window.Width <= 0 || window.Height <= 0) return WindowMode.Unknown;
        const int slack = 2;
        var covers = window.Left <= monitor.Left + slack && window.Top <= monitor.Top + slack
                     && window.Right >= monitor.Right - slack && window.Bottom >= monitor.Bottom - slack;
        return covers && !hasCaption && !hasSizingFrame ? WindowMode.Borderless : WindowMode.Windowed;
    }

    public static string Code(WindowMode mode) => mode switch
    {
        WindowMode.Windowed => "windowed",
        WindowMode.Borderless => "borderless",
        WindowMode.Exclusive => "exclusive",
        _ => "unknown",
    };

    public static WindowMode Parse(string? code) => code switch
    {
        "windowed" => WindowMode.Windowed,
        "borderless" => WindowMode.Borderless,
        "exclusive" => WindowMode.Exclusive,
        _ => WindowMode.Unknown,
    };
}
