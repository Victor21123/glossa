namespace Glossa.Core.Games;

/// <summary>
/// Finds a kernel or user-mode anti-cheat beside a game, by the names of files and folders next to its program and by
/// the names of running processes. Nothing inside the game is opened or read: only the directory listing.
/// Such a game gets the «защищённый» profile — no pause of its process, no restyling of its window.
/// </summary>
public static class AntiCheat
{
    /// <summary>File or folder names (lower case) that ship with each anti-cheat.</summary>
    private static readonly (string Name, string[] Markers)[] Files =
    [
        ("Easy Anti-Cheat", ["easyanticheat", "easyanticheat_x64.dll", "easyanticheat_x86.dll", "easyanticheat_eos.sys", "start_protected_game.exe"]),
        ("BattlEye", ["battleye", "beclient_x64.dll", "beclient.dll", "beservice_x64.exe"]),
        ("Riot Vanguard", ["riot vanguard", "vgk.sys"]),
        ("ACE (Tencent)", ["anticheatexpert", "ace-base64.dll", "ace-base.dll", "sguard64.exe", "ace-guard64.dll"]),
        ("HoYoProtect", ["mhypbase.dll", "mhyprot2.sys", "mhyprot3.sys", "hoyokprotect.sys"]),
        ("NetEase NEAC", ["neacsafe64.sys", "neacclient.exe", "neac"]),
        ("nProtect GameGuard", ["gameguard", "gamemon.des", "gamemon64.des"]),
        ("XIGNCODE3", ["xigncode", "x3.xem", "xhunter1.sys"]),
        ("EA Javelin", ["eaanticheat", "eaanticheat.gameservicelauncher.exe"]),
        ("EQU8", ["equ8"]),
        ("PunkBuster", ["pbsvc.exe", "pbcl.dll"]),
    ];

    /// <summary>Program names of games whose anti-cheat runs as its own service rather than beside the game.</summary>
    private static readonly (string Name, string[] Games, string[] Services)[] Services =
    [
        ("Riot Vanguard", ["valorant-win64-shipping.exe", "valorant.exe", "league of legends.exe"], ["vgc.exe"]),
        ("FACEIT", ["cs2.exe", "csgo.exe"], ["faceitservice.exe", "faceit.exe"]),
    ];

    /// <summary>Folders under which each game has its own folder: the scan stops before them, never lists them.</summary>
    private static readonly HashSet<string> LibraryRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "steamapps", "SteamLibrary", "Steam", "Program Files", "Program Files (x86)", "Games", "Epic Games",
        "GOG Games", "GOG Galaxy", "XboxGames", "WindowsApps", "Ubisoft Game Launcher", "EA Games", "Origin Games", "Riot Games",
    };

    /// <summary>The anti-cheat named by the listed files or running services, or null.</summary>
    /// <param name="names">File and folder names beside the game (<see cref="NamesBeside"/>).</param>
    /// <param name="exeFileName">The game's program, e.g. «VALORANT-Win64-Shipping.exe».</param>
    /// <param name="running">Names of running processes («vgc.exe»).</param>
    public static string? Detect(IEnumerable<string> names, string exeFileName, IEnumerable<string> running)
    {
        var set = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
        foreach (var (name, markers) in Files)
            if (markers.Any(set.Contains)) return name;
        // BattlEye launchers are named after the game: «Game_BE.exe».
        if (set.Any(n => n.EndsWith("_be.exe", StringComparison.Ordinal))) return "BattlEye";

        var exe = exeFileName.ToLowerInvariant();
        var processes = new HashSet<string>(running.Select(p => p.ToLowerInvariant()));
        foreach (var (name, games, services) in Services)
            if (games.Contains(exe) && services.Any(processes.Contains)) return name;
        return null;
    }

    /// <summary>
    /// Names of files and folders in the game's program folder and up to three folders above it (an Unreal game's program
    /// sits in Binaries\Win64, its anti-cheat folder at the game's root), stopping below a library root such as
    /// «steamapps\common» or «Program Files» so another game's or a shared service's files are never counted.
    /// </summary>
    public static IReadOnlyList<string> NamesBeside(string exePath)
    {
        var names = new List<string>();
        if (!Path.IsPathRooted(exePath)) return names;
        var dir = Path.GetDirectoryName(exePath);
        for (var level = 0; level < 4 && dir is not null; level++)
        {
            var info = new DirectoryInfo(dir);
            if (info.Parent is null || LibraryRoots.Contains(info.Name)) break; // a drive root or a library
            try
            {
                foreach (var entry in info.EnumerateFileSystemInfos()) names.Add(entry.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                break;
            }
            if (LibraryRoots.Contains(info.Parent.Name)) break; // the game's own root folder was the last one
            dir = info.Parent.FullName;
        }
        return names;
    }
}
