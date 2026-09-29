namespace Glossa.App.Theme;

public enum ThemeKind { Dark, Light, Disco }

/// <summary>
/// Colour tokens of the approved mockups (DESIGN.md; canvas «Glossa: дизайн»), in the OKLCH notation they were
/// designed in. Keys become resource keys: a brush under the key and its colour under key + "Color".
/// </summary>
public static class Palettes
{
    public static readonly string[] Accents = ["white", "jade", "amber", "sun", "lilac"];

    /// <summary>The main window and everything opened from it.</summary>
    public static IReadOnlyDictionary<string, string> Window(ThemeKind kind) => kind switch
    {
        ThemeKind.Disco => new Dictionary<string, string>
        {
            ["Page"] = "0.18 0.01 60", ["Side"] = "0.2 0.011 60", ["Surface"] = "0.215 0.012 60", ["Band"] = "0.245 0.013 60",
            ["Well"] = "0.27 0.013 60", ["Rule"] = "0.32 0.018 65", ["RuleSoft"] = "0.27 0.015 65",
            ["Ink"] = "0.94 0.015 85", ["InkSoft"] = "0.85 0.015 80", ["Muted"] = "0.71 0.02 75", ["Faint"] = "0.6 0.02 75",
            ["Accent"] = "0.74 0.14 55", ["AccentText"] = "0.78 0.13 60", ["MarkBg"] = "0.74 0.14 55 / 0.3", ["MarkInk"] = "0.94 0.015 85",
            ["Inverse"] = "0.88 0.035 80", ["InverseInk"] = "0.24 0.02 60",
            ["Danger"] = "0.72 0.16 30", ["DangerWash"] = "0.72 0.16 30 / 0.14", ["Good"] = "0.78 0.11 140", ["Warn"] = "0.78 0.13 60",
            ["Scrim"] = "0.1 0.01 60 / 0.6", ["Shadow"] = "0.06 0.01 60 / 0.6",
        },
        ThemeKind.Light => new Dictionary<string, string>
        {
            ["Page"] = "0.955 0.008 95", ["Side"] = "0.94 0.009 95", ["Surface"] = "0.975 0.006 95", ["Band"] = "0.95 0.008 95",
            ["Well"] = "0.905 0.01 95", ["Rule"] = "0.86 0.01 95", ["RuleSoft"] = "0.9 0.008 95",
            ["Ink"] = "0.23 0.02 255", ["InkSoft"] = "0.37 0.02 255", ["Muted"] = "0.5 0.015 255", ["Faint"] = "0.56 0.013 255",
            ["Accent"] = "0.3 0.02 255", ["AccentText"] = "0.3 0.02 255", ["MarkBg"] = "0.27 0.02 255", ["MarkInk"] = "0.97 0.005 95",
            ["Inverse"] = "0.23 0.02 255", ["InverseInk"] = "0.975 0.006 95",
            ["Danger"] = "0.52 0.17 28", ["DangerWash"] = "0.52 0.17 28 / 0.1", ["Good"] = "0.48 0.12 150", ["Warn"] = "0.5 0.12 60",
            ["Scrim"] = "0.3 0.02 255 / 0.35", ["Shadow"] = "0.3 0.02 255 / 0.28",
        },
        _ => new Dictionary<string, string>
        {
            ["Page"] = "0.17 0.01 255", ["Side"] = "0.185 0.011 255", ["Surface"] = "0.205 0.012 255", ["Band"] = "0.235 0.013 255",
            ["Well"] = "0.25 0.013 255", ["Rule"] = "0.3 0.013 255", ["RuleSoft"] = "0.26 0.012 255",
            ["Ink"] = "0.96 0.006 95", ["InkSoft"] = "0.86 0.008 95", ["Muted"] = "0.71 0.012 255", ["Faint"] = "0.62 0.012 255",
            ["Accent"] = "0.97 0.005 95", ["AccentText"] = "0.97 0.005 95", ["MarkBg"] = "0.94 0.006 95", ["MarkInk"] = "0.22 0.02 255",
            ["Inverse"] = "0.96 0.006 95", ["InverseInk"] = "0.17 0.01 255",
            ["Danger"] = "0.74 0.16 30", ["DangerWash"] = "0.74 0.16 30 / 0.15", ["Good"] = "0.78 0.12 150", ["Warn"] = "0.8 0.12 65",
            ["Scrim"] = "0.08 0.01 255 / 0.62", ["Shadow"] = "0.05 0.01 255 / 0.6",
        },
    };

    /// <summary>
    /// The word card over the game. White is the default accent (Dragon Quest style: inverse highlight);
    /// «Диско» ignores the accent and keeps its own amber.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Card(ThemeKind kind, string accent)
    {
        if (kind == ThemeKind.Disco)
            return new Dictionary<string, string>
            {
                ["Surface"] = "0.215 0.012 60", ["Band"] = "0.245 0.013 60", ["Well"] = "0.26 0.012 60",
                ["Rule"] = "0.47 0.03 70", ["RuleSoft"] = "0.34 0.02 65",
                ["Ink"] = "0.94 0.015 85", ["InkSoft"] = "0.85 0.015 80", ["Muted"] = "0.71 0.02 75",
                ["Plate"] = "0.88 0.035 80", ["PlateInk"] = "0.24 0.02 60", ["Kbd"] = "0.27 0.012 60",
                ["Shadow"] = "0.08 0.01 60 / 0.55", ["Danger"] = "0.72 0.16 30", ["DangerWash"] = "0.72 0.16 30 / 0.14",
                ["Accent"] = "0.74 0.14 55", ["AccentText"] = "0.78 0.13 60", ["MarkBg"] = "0.74 0.14 55 / 0.3", ["MarkInk"] = "0.94 0.015 85",
            };

        var light = kind == ThemeKind.Light;
        var t = light
            ? new Dictionary<string, string>
            {
                ["Surface"] = "0.975 0.006 95", ["Band"] = "0.95 0.008 95", ["Well"] = "0.92 0.008 95",
                ["Rule"] = "0.86 0.01 95", ["RuleSoft"] = "0.9 0.008 95",
                ["Ink"] = "0.23 0.02 255", ["InkSoft"] = "0.37 0.02 255", ["Muted"] = "0.5 0.015 255",
                ["Plate"] = "0.27 0.02 255", ["PlateInk"] = "0.96 0.006 95", ["Kbd"] = "0.985 0.004 95",
                ["Shadow"] = "0.15 0.02 255 / 0.35", ["Danger"] = "0.52 0.17 28", ["DangerWash"] = "0.52 0.17 28 / 0.1",
            }
            : new Dictionary<string, string>
            {
                ["Surface"] = "0.205 0.012 255", ["Band"] = "0.235 0.013 255", ["Well"] = "0.24 0.012 255",
                ["Rule"] = "0.36 0.014 255", ["RuleSoft"] = "0.3 0.013 255",
                ["Ink"] = "0.96 0.006 95", ["InkSoft"] = "0.86 0.008 95", ["Muted"] = "0.71 0.012 255",
                ["Plate"] = "0.9 0.02 95", ["PlateInk"] = "0.22 0.02 255", ["Kbd"] = "0.26 0.013 255",
                ["Shadow"] = "0.08 0.01 255 / 0.55", ["Danger"] = "0.74 0.16 30", ["DangerWash"] = "0.74 0.16 30 / 0.15",
            };

        if (accent is not ("jade" or "amber" or "sun" or "lilac"))
        {
            t["Accent"] = t["AccentText"] = light ? "0.3 0.02 255" : "0.97 0.005 95";
            t["MarkBg"] = light ? "0.27 0.02 255" : "0.94 0.006 95";
            t["MarkInk"] = light ? "0.97 0.005 95" : "0.22 0.02 255";
            return t;
        }

        var hue = accent switch { "jade" => 168, "amber" => 62, "sun" => 95, _ => 300 };
        var sun = accent == "sun";
        t["Accent"] = light ? $"0.56 0.12 {hue}" : $"0.8 0.13 {hue}";
        t["AccentText"] = light ? $"0.46 0.11 {hue}" : $"0.82 0.12 {hue}";
        t["MarkBg"] = light ? $"0.9 0.08 {hue}" : sun ? "0.87 0.15 95" : $"0.52 0.1 {hue} / 0.62";
        t["MarkInk"] = !light && sun ? "0.24 0.03 95" : t["Ink"];
        return t;
    }
}
