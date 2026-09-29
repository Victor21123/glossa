using System.Globalization;
using System.Windows.Media;

namespace Glossa.App.Theme;

/// <summary>
/// OKLCH to sRGB. The approved mockups define every colour in OKLCH ("0.17 0.01 255", "0.74 0.14 55 / 0.3"),
/// so the app keeps the same numbers and converts them here instead of carrying a second, hand-made hex palette.
/// </summary>
public static class Oklch
{
    /// <summary>Parses "L C H" or "L C H / alpha" (L and alpha 0–1, H in degrees).</summary>
    public static Color Parse(string value)
    {
        var parts = value.Split('/', 2);
        var lch = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (lch.Length != 3) throw new FormatException($"Not an OKLCH colour: '{value}'");
        var alpha = parts.Length == 2 ? Number(parts[1]) : 1;
        return ToColor(Number(lch[0]), Number(lch[1]), Number(lch[2]), alpha);
    }

    public static Color ToColor(double l, double c, double h, double alpha = 1)
    {
        var hue = h * Math.PI / 180;
        var a = c * Math.Cos(hue);
        var b = c * Math.Sin(hue);

        var l1 = l + 0.3963377774 * a + 0.2158037573 * b;
        var m1 = l - 0.1055613458 * a - 0.0638541728 * b;
        var s1 = l - 0.0894841775 * a - 1.2914855480 * b;
        var lc = l1 * l1 * l1;
        var mc = m1 * m1 * m1;
        var sc = s1 * s1 * s1;

        var red = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        var green = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        var blue = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;
        return Color.FromArgb(Channel(alpha), Encode(red), Encode(green), Encode(blue));
    }

    private static byte Encode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return Channel(srgb);
    }

    private static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static double Number(string s) => double.Parse(s.Trim(), CultureInfo.InvariantCulture);
}
