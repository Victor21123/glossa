using Glossa.Core.Games;
using Glossa.Core.Library;
using Glossa.Core.Ocr;

namespace Glossa.Tests.Games;

public class GameProfileTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void First_lookup_creates_the_profile_once_named_after_the_window()
    {
        var profiles = new List<GameProfile>();
        var (p, created) = GameProfiles.Ensure(profiles, @"E:\SteamLibrary\steamapps\common\P5R\P5R.exe", "  Persona 5   Royal ", Now);
        Assert.True(created);
        Assert.Equal("Persona 5 Royal", p.Name);
        Assert.Equal(GameProfiles.Default, p.DuringLookup);

        var (again, changed) = GameProfiles.Ensure(profiles, @"E:\SteamLibrary\steamapps\common\P5R\P5R.exe", "P5R", Now);
        Assert.False(changed);
        Assert.Same(p, again);
        Assert.Single(profiles);
    }

    [Fact]
    public void A_moved_game_keeps_its_profile_and_gains_the_new_path()
    {
        var profiles = new List<GameProfile>();
        var (p, _) = GameProfiles.Ensure(profiles, @"D:\Games\Hades II\Hades2.exe", "Hades II", Now);
        var (moved, changed) = GameProfiles.Ensure(profiles, @"E:\Hades II\Hades2.exe", "Hades II", Now);
        Assert.Same(p, moved);
        Assert.True(changed);
        Assert.Equal(2, p.Programs.Count);
    }

    [Fact]
    public void Untitled_window_takes_the_program_name()
    {
        Assert.Equal("DQXIS", GameProfiles.NameFrom("", @"C:\Games\DQXIS.exe"));
        Assert.Equal(60, GameProfiles.NameFrom(new string('x', 100), "a.exe").Length);
    }

    [Fact]
    public void Dictionary_shows_the_profile_name_for_a_known_program()
    {
        var profiles = new List<GameProfile> { new() { Name = "Persona 5 Royal", Programs = [@"E:\P5R\P5R.exe"] } };
        Assert.Equal("Persona 5 Royal", GameProfiles.DisplayName(profiles, "P5R.exe", "P5R"));
        Assert.Equal("Hades II", GameProfiles.DisplayName(profiles, "Hades2.exe", "Hades II"));
        Assert.Equal("Hades2.exe", GameProfiles.DisplayName(profiles, "Hades2.exe", " "));
        Assert.Null(GameProfiles.DisplayName(profiles, null, null));
    }

    [Fact]
    public void Profile_choices_beat_the_general_ones_and_default_keeps_them()
    {
        var p = new GameProfile { Language = "ja", DuringLookup = GameProfiles.Default, Ai = "lowvram", Programs = [@"D:\Games\P5R.exe"] };
        var c = GameProfiles.Resolve("auto", "frame", p, @"D:\Games\P5R.exe");
        Assert.Equal(new GameChoices("ja", "frame", "lowvram"), c);
        Assert.Equal(new GameChoices("auto", "none", GameProfiles.Default), GameProfiles.Resolve("auto", "none", null, @"D:\x.exe"));
    }

    [Fact]
    public void Pause_is_refused_with_an_anti_cheat_and_for_windows_programs()
    {
        var protectedGame = new GameProfile { DuringLookup = "pause", AntiCheat = "Easy Anti-Cheat" };
        var c = GameProfiles.Resolve("auto", "none", protectedGame, @"D:\Games\Elden Ring\eldenring.exe");
        Assert.Equal("frame", c.DuringLookup);
        Assert.Contains("Easy Anti-Cheat", c.PauseRefused);

        Assert.Equal("frame", GameProfiles.Resolve("auto", "pause", null, @"C:\Windows\explorer.exe").DuringLookup);
        Assert.Equal("frame", GameProfiles.Resolve("auto", "pause", null, @"D:\Programs\Glossa\Glossa.exe").DuringLookup);
        Assert.Equal("pause", GameProfiles.Resolve("auto", "pause", null, @"D:\Games\P5R\P5R.exe").DuringLookup);
    }

    [Fact]
    public void Smart_collection_finds_a_renamed_game()
    {
        var word = new SavedWord { Language = "ja", Word = "俺様", AppExe = "P5R.exe", WindowTitle = "P5R" };
        var filter = new SmartFilter { Game = "Persona 5 Royal" };
        Assert.False(filter.Matches(word));
        Assert.True(filter.Matches(word, (exe, title) => exe == "P5R.exe" ? "Persona 5 Royal" : title));
    }

    [Theory]
    [InlineData("EasyAntiCheat", "Easy Anti-Cheat")]
    [InlineData("BEClient_x64.dll", "BattlEye")]
    [InlineData("TslGame_BE.exe", "BattlEye")]
    [InlineData("mhypbase.dll", "HoYoProtect")]
    [InlineData("ACE-Base64.dll", "ACE (Tencent)")]
    [InlineData("readme.txt", null)]
    public void Anti_cheat_is_named_by_the_files_beside_the_game(string file, string? expected)
    {
        Assert.Equal(expected, AntiCheat.Detect(["Game.exe", "Content", file], "Game.exe", []));
    }

    [Fact]
    public void Service_anti_cheat_counts_only_for_its_games_while_it_runs()
    {
        Assert.Equal("Riot Vanguard", AntiCheat.Detect([], "VALORANT-Win64-Shipping.exe", ["vgc.exe", "explorer.exe"]));
        Assert.Null(AntiCheat.Detect([], "VALORANT-Win64-Shipping.exe", ["explorer.exe"]));
        Assert.Null(AntiCheat.Detect([], "P5R.exe", ["vgc.exe"]));
    }

    [Fact]
    public void Folder_scan_climbs_to_the_game_root_but_not_into_the_library()
    {
        var root = Path.Combine(Path.GetTempPath(), "glossa-test-ac-" + Guid.NewGuid().ToString("N"));
        try
        {
            var common = Path.Combine(root, "steamapps", "common");
            var game = Path.Combine(common, "Game");
            var bin = Path.Combine(game, "Game", "Binaries", "Win64");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(Path.Combine(game, "EasyAntiCheat"));
            Directory.CreateDirectory(Path.Combine(common, "Other", "BattlEye")); // a neighbour's anti-cheat
            File.WriteAllText(Path.Combine(bin, "Game-Win64-Shipping.exe"), "");

            var names = AntiCheat.NamesBeside(Path.Combine(bin, "Game-Win64-Shipping.exe"));
            Assert.Contains("EasyAntiCheat", names);
            Assert.DoesNotContain("Other", names);
            Assert.Equal("Easy Anti-Cheat", AntiCheat.Detect(names, "Game-Win64-Shipping.exe", []));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Window_mode_follows_the_frame_and_the_monitor()
    {
        var monitor = new PixelRect(0, 0, 2560, 1440);
        Assert.Equal(WindowMode.Borderless, WindowModes.Classify(new PixelRect(0, 0, 2560, 1440), monitor, false, false, false));
        Assert.Equal(WindowMode.Windowed, WindowModes.Classify(new PixelRect(0, 0, 2560, 1440), monitor, true, true, false)); // maximized
        Assert.Equal(WindowMode.Windowed, WindowModes.Classify(new PixelRect(320, 180, 2240, 1260), monitor, false, false, false));
        Assert.Equal(WindowMode.Exclusive, WindowModes.Classify(new PixelRect(0, 0, 2560, 1440), monitor, false, false, true));
        Assert.Equal(WindowMode.Borderless, WindowModes.Parse(WindowModes.Code(WindowMode.Borderless)));
    }

    private sealed class FakeSuspender : IProcessSuspender
    {
        public List<string> Calls { get; } = [];
        public bool Refuse { get; set; }

        public bool Suspend(int pid, out string? error)
        {
            error = Refuse ? "нет прав" : null;
            if (!Refuse) Calls.Add("suspend " + pid);
            return !Refuse;
        }

        public void Resume(int pid) => Calls.Add("resume " + pid);
    }

    [Fact]
    public void Every_pause_is_matched_by_exactly_one_resume()
    {
        var s = new FakeSuspender();
        var guard = new PauseGuard(s, TimeSpan.FromMinutes(5));
        Assert.Equal("ok", guard.Handle("pause 42", Now));
        Assert.Equal("ok", guard.Handle("pause 42", Now)); // the same game again: not suspended twice
        Assert.Equal("ok", guard.Handle("pause 7", Now));  // another game: the first wakes first
        guard.Resume();
        guard.Resume();
        Assert.Equal(["suspend 42", "resume 42", "suspend 7", "resume 7"], s.Calls);
        Assert.Null(guard.Paused);
    }

    [Fact]
    public void A_forgotten_pause_ends_by_the_time_limit()
    {
        var s = new FakeSuspender();
        var guard = new PauseGuard(s, TimeSpan.FromMinutes(5));
        guard.Pause(42, Now);
        Assert.False(guard.Tick(Now.AddMinutes(4)));
        Assert.True(guard.Tick(Now.AddMinutes(5)));
        Assert.False(guard.Tick(Now.AddMinutes(6)));
        Assert.Equal(["suspend 42", "resume 42"], s.Calls);
    }

    [Fact]
    public void Refused_pause_reports_why_and_leaves_nothing_to_resume()
    {
        var guard = new PauseGuard(new FakeSuspender { Refuse = true }, TimeSpan.FromMinutes(5));
        Assert.Equal("error нет прав", guard.Handle("pause 42", Now));
        Assert.Null(guard.Paused);
        Assert.StartsWith("error", guard.Handle("pause x", Now));
        Assert.Equal("ok", guard.Handle("ping", Now));
    }
}
