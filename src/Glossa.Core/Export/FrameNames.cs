using System.Globalization;

namespace Glossa.Core.Export;

/// <summary>
/// File names for exported scene frames: "game_word_date.jpg". The game and the word come from the screen and the
/// user's library, so every part is made safe for a Windows folder and can never reach outside it.
/// </summary>
public static class FrameNames
{
    /// <summary>Longest game or word part, in characters: the whole name stays well under the path limits.</summary>
    public const int MaxPart = 40;

    /// <summary>The game part when the sentence has no game.</summary>
    public const string NoGame = "Без игры";
    /// <summary>The word part when there is no usable word text.</summary>
    public const string NoWord = "слово";

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>"Game_word_2026-10-01_14-05-09.jpg"; <paramref name="local"/> is the time as the user saw it.</summary>
    public static string File(string? game, string? word, DateTime local) =>
        string.Create(CultureInfo.InvariantCulture, $"{Part(game, NoGame)}_{Part(word, NoWord)}_{local:yyyy-MM-dd_HH-mm-ss}.jpg");

    /// <summary>One piece of a file name: no separators or characters Windows forbids, no device names, cut short.</summary>
    public static string Part(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => Unsafe(c, invalid) ? '_' : c).ToArray();
        var part = Trim(new string(chars));
        if (part.Length > MaxPart)
        {
            var cut = char.IsHighSurrogate(part[MaxPart - 1]) ? MaxPart - 1 : MaxPart; // keep an emoji whole
            part = Trim(part[..cut]);
        }
        if (part.Length == 0 || part.All(c => c == '_')) return fallback; // nothing but replaced characters says nothing
        var stem = part.Split('.')[0].TrimEnd();
        return Devices.Contains(stem) ? "_" + part : part;
    }

    /// <summary>
    /// Characters Windows forbids, plus the invisible ones that disguise a name: direction overrides (U+202E shows
    /// "gpj.exe" as "exe.jpg"), zero-width and isolate marks, line and paragraph separators, private use, unassigned.
    /// </summary>
    private static bool Unsafe(char c, char[] invalid)
    {
        if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || Array.IndexOf(invalid, c) >= 0) return true;
        return char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    /// <summary>Dots and spaces at the ends: Windows drops trailing ones, and a leading ".." is not a name.</summary>
    private static string Trim(string s) => s.Trim(' ', '.');

    /// <summary>The name itself, or "name (2).jpg", "name (3).jpg"... until <paramref name="taken"/> says it is free.</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken(candidate)) return candidate;
        }
    }
}
