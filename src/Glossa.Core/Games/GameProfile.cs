namespace Glossa.Core.Games;

/// <summary>
/// Настройки → Игры и профили: one program the user looked words up in. It appears by itself at the first lookup there,
/// and its choices beat the general ones («Как в общих» keeps the general one).
/// </summary>
public sealed class GameProfile
{
    /// <summary>As the dictionary and collections show the game.</summary>
    public string Name { get; set; } = "";

    /// <summary>The window title at the first lookup («заголовок окна был «P5R»»).</summary>
    public string FirstTitle { get; set; } = "";

    /// <summary>Full paths of its programs (a launcher, several exe); matched by path, then by file name.</summary>
    public List<string> Programs { get; set; } = [];

    /// <summary>default (as in Языки), en, ja, zh or ru.</summary>
    public string Language { get; set; } = GameProfiles.Default;

    /// <summary>default, none (leave the game alone), frame (a still of the screen) or pause (the game's process sleeps).</summary>
    public string DuringLookup { get; set; } = GameProfiles.Default;

    /// <summary>default, lowvram (the model mostly in RAM) or off (dictionaries only).</summary>
    public string Ai { get; set; } = GameProfiles.Default;

    /// <summary>Stretch the game's window over the monitor without a frame, so the card shows over it.</summary>
    public bool Borderless { get; set; }

    /// <summary>The anti-cheat found beside the game («Easy Anti-Cheat»), or null; checked again when the profile is shown.</summary>
    public string? AntiCheat { get; set; }

    /// <summary>How the game's window looked at the last lookup: windowed, borderless or exclusive.</summary>
    public string? WindowMode { get; set; }

    public DateTime CreatedUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsProtected => AntiCheat is not null;
}

/// <summary>What a lookup in one program actually does, general settings and the program's profile together.</summary>
/// <param name="Language">auto, en, ja, zh or ru.</param>
/// <param name="DuringLookup">none, frame or pause.</param>
/// <param name="Ai">default, lowvram or off.</param>
/// <param name="PauseRefused">Why «Пауза игры» was asked for but a still frame is used instead, or null.</param>
public sealed record GameChoices(string Language, string DuringLookup, string Ai, string? PauseRefused = null);

public static class GameProfiles
{
    public const string Default = "default";
    private const int MaxNameLength = 60;

    /// <summary>Programs that are never paused: the shell and Glossa itself would freeze the desktop or the card.</summary>
    private static readonly HashSet<string> NeverPaused = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "glossa.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "sihost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe", "ApplicationFrameHost.exe", "taskmgr.exe",
    };

    /// <summary>The profile of a program: its exact path first, then any profile listing a program of that file name.</summary>
    public static GameProfile? Find(IReadOnlyList<GameProfile> profiles, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var byPath = profiles.FirstOrDefault(p => p.Programs.Any(x => string.Equals(x, exePath, StringComparison.OrdinalIgnoreCase)));
        if (byPath is not null) return byPath;
        var file = Path.GetFileName(exePath);
        return profiles.FirstOrDefault(p => p.Programs.Any(x => string.Equals(Path.GetFileName(x), file, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The program's profile, created at its first lookup (named after the window title). A copy of the game moved
    /// to another folder keeps its profile and gains the new path. <c>Changed</c> is true when anything was written.
    /// </summary>
    public static (GameProfile Profile, bool Changed) Ensure(List<GameProfile> profiles, string exePath, string title, DateTime nowUtc)
    {
        if (Find(profiles, exePath) is { } found)
        {
            if (Path.IsPathRooted(exePath) && !found.Programs.Any(x => string.Equals(x, exePath, StringComparison.OrdinalIgnoreCase)))
            {
                // Same file name, other folder: a moved game, or only the name was known before.
                var bare = found.Programs.FindIndex(x => !Path.IsPathRooted(x) && string.Equals(x, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase));
                if (bare >= 0) found.Programs[bare] = exePath;
                else found.Programs.Add(exePath);
                return (found, true);
            }
            return (found, false);
        }
        var profile = new GameProfile
        {
            Name = NameFrom(title, exePath),
            FirstTitle = title.Trim(),
            Programs = [exePath],
            CreatedUtc = nowUtc,
        };
        profiles.Add(profile);
        return (profile, true);
    }

    /// <summary>The window title as the game's name, or the program's file name without «.exe».</summary>
    public static string NameFrom(string title, string exePath)
    {
        var name = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exePath);
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd();
    }

    /// <summary>
    /// How the dictionary names the game of a saved sentence: the profile's name when its program is known, else the
    /// window title or the program's file name (words added by hand carry only a title).
    /// </summary>
    public static string? DisplayName(IReadOnlyList<GameProfile> profiles, string? appExe, string? title)
    {
        if (!string.IsNullOrWhiteSpace(appExe) && Find(profiles, appExe.Trim()) is { Name.Length: > 0 } p) return p.Name;
        return Blank(title) ?? Blank(appExe);

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// The general choices with the profile's on top. A pause is refused for games with an anti-cheat (it may take the
    /// frozen process for tampering) and for the desktop's own programs; a still frame is used instead.
    /// </summary>
    public static GameChoices Resolve(string generalLanguage, string generalDuring, GameProfile? profile, string exePath)
    {
        var language = profile is { Language: var l } && l != Default ? l : generalLanguage;
        var during = profile is { DuringLookup: var d } && d != Default ? d : generalDuring;
        var ai = profile?.Ai ?? Default;
        string? refused = null;
        if (during == "pause")
        {
            refused = profile?.AntiCheat is { } ac ? $"в игре античит ({ac}) — пауза недоступна"
                : !CanPause(exePath) ? "это программа Windows — её не останавливаем"
                : null;
            if (refused is not null) during = "frame";
        }
        return new GameChoices(language, during, ai, refused);
    }

    /// <summary>False for Windows' own programs and Glossa: pausing them would freeze the desktop.</summary>
    public static bool CanPause(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        if (NeverPaused.Contains(Path.GetFileName(exePath))) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !(Path.IsPathRooted(exePath) && windows.Length > 0
                 && exePath.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
