using System.Diagnostics;
using System.Windows;
using Glossa.Core.Config;
using Glossa.Core.Games;
using Glossa.Core.Logging;

namespace Glossa.App.Games;

/// <summary>
/// Настройки → Игры и профили at run time: finds or creates the profile of the program a lookup starts in, says what
/// the lookup does there, and keeps the anti-cheat verdict fresh. Profiles are part of settings.json.
/// </summary>
public sealed class GameRegistry(Func<AppSettings> settings, Action<AppSettings> save, ILog log)
{
    private readonly HashSet<GameProfile> _warnedExclusive = [];

    /// <summary>A profile was created, changed or removed (raised on the UI thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<GameProfile> Profiles => settings().Games;

    /// <summary>
    /// At a lookup: the program's profile (created at its first lookup, with its anti-cheat checked before anything is
    /// paused) and what the lookup does in it. Glossa's own windows get no profile and are never frozen.
    /// </summary>
    public (GameProfile? Profile, GameChoices Choices) Touch(GameWindow w)
    {
        var s = settings();
        if (w.IsGlossa || w.ExePath.Length == 0)
            return (null, GameProfiles.Resolve(s.ScreenLanguage, "none", null, w.ExePath));

        var existed = GameProfiles.Find(s.Games, w.ExePath) is not null;
        var (profile, changed) = GameProfiles.Ensure(s.Games, w.ExePath, w.Title, DateTime.UtcNow);
        if (!existed)
        {
            profile.AntiCheat = Detect(profile);
            log.Info($"game profile «{profile.Name}» for {w.ExePath}" + (profile.AntiCheat is { } ac ? $", anti-cheat: {ac}" : ""));
        }
        if (w.Mode != WindowMode.Unknown && profile.WindowMode != WindowModes.Code(w.Mode))
        {
            profile.WindowMode = WindowModes.Code(w.Mode);
            changed = true;
        }
        if (changed) Save();
        return (profile, GameProfiles.Resolve(s.ScreenLanguage, s.DuringLookup, profile, w.ExePath));
    }

    /// <summary>True once per profile per run: the game holds the screen exclusively and the card may stay hidden.</summary>
    public bool WarnExclusiveOnce(GameProfile profile) => _warnedExclusive.Add(profile);

    /// <summary>The anti-cheat of a profile checked again in the background (the page shows it fresh).</summary>
    public void Recheck(GameProfile profile) => Task.Run(() =>
    {
        var found = Detect(profile);
        if (found == profile.AntiCheat) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            profile.AntiCheat = found;
            Save();
        });
    });

    public void Remove(GameProfile profile)
    {
        settings().Games.Remove(profile);
        Save();
    }

    /// <summary>Writes settings.json and tells the pages (a renamed game renames it in the dictionary too).</summary>
    public void Save()
    {
        save(settings());
        Changed?.Invoke();
    }

    /// <summary>The game of a saved sentence as the dictionary names it.</summary>
    public string? DisplayName(string? exe, string? title) => GameProfiles.DisplayName(settings().Games, exe, title);

    private string? Detect(GameProfile profile)
    {
        // The game is gone from its folder (uninstalled, a drive unplugged): keep what was found while it was there.
        if (!profile.Programs.Any(File.Exists)) return profile.AntiCheat;
        try
        {
            var running = Process.GetProcesses();
            List<string> names;
            try
            {
                names = running.Select(p => p.ProcessName + ".exe").ToList();
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
            foreach (var program in profile.Programs)
                if (AntiCheat.Detect(AntiCheat.NamesBeside(program), Path.GetFileName(program), names) is { } found) return found;
            return null;
        }
        catch (Exception ex)
        {
            log.Error("anti-cheat check", ex);
            return profile.AntiCheat;
        }
    }
}
