using Glossa.Core.Companions;
using Glossa.Core.Library;
using Microsoft.Data.Sqlite;

namespace Glossa.Tests.Companions;

public sealed class KeeperTests : IDisposable
{
    private readonly TestFolders _folders = new();
    private readonly string _db;
    private readonly string _art;

    public KeeperTests()
    {
        _db = Path.Combine(_folders.New(), "library.db");
        _art = _folders.New();
        CatalogTests.Character(_art, "1-legendary", "ueno", 189);
        CatalogTests.Character(_art, "2-epic", "alice", 256);
        CatalogTests.Character(_art, "3-rare", "joker", 253);
        CatalogTests.Character(_art, "4-common", "miku", 255);
        CatalogTests.Character(_art, "4-common", "cloud", 255);
    }

    public void Dispose() => _folders.Dispose();

    private CompanionCatalog Catalog() => CompanionCatalog.Load([_art]);

    private static Func<double> Sequence(params double[] values)
    {
        var i = 0;
        return () => values[i++ % values.Length];
    }

    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    internal static readonly byte[] SealKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    internal static readonly byte[] FateKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    /// <summary>A guard of a test PC; the fate off unless asked, so a test's draws decide the roll.</summary>
    internal static CompanionGuard Guard(Func<double> random, string machine = "pc-1", bool fate = false) =>
        new(SealKey, FateKey, machine, random, fate);

    /// <summary>A bond as the keeper writes it: sealed on the guard's PC.</summary>
    internal static void Sealed(LibraryStore store, CompanionGuard guard, string id, DateTime utc) =>
        store.Adopt(id, utc, null, guard.BondSeal(id, utc), guard.MachineTag);

    [Fact]
    public void A_roll_picks_the_rarity_by_its_share_then_a_character_in_it()
    {
        var catalog = Catalog();
        // Common takes [0, 0.60), rare [0.60, 0.85), epic [0.85, 0.97), legendary [0.97, 1).
        Assert.Equal(("miku", Rarity.Common), Pick(catalog, 0.10, 0.9));
        Assert.Equal(("cloud", Rarity.Common), Pick(catalog, 0.59, 0.0));
        Assert.Equal(("joker", Rarity.Rare), Pick(catalog, 0.60, 0.5));
        Assert.Equal(("alice", Rarity.Epic), Pick(catalog, 0.96, 0.5));
        Assert.Equal(("ueno", Rarity.Legendary), Pick(catalog, 0.999, 0.99));
    }

    private static (string, Rarity) Pick(CompanionCatalog catalog, double rarity, double pick)
    {
        var r = CompanionRoller.Roll(catalog, Sequence(rarity, pick))!;
        return (r.Companion.Id, r.Rarity);
    }

    [Fact]
    public void Many_rolls_land_near_the_shares()
    {
        var catalog = Catalog();
        var rng = new Random(11);
        var counts = Enum.GetValues<Rarity>().ToDictionary(r => r, _ => 0);
        for (var i = 0; i < 100_000; i++)
            counts[CompanionRoller.Roll(catalog, rng.NextDouble)!.Rarity]++;
        foreach (var (rarity, share) in Rarities.Chances)
            Assert.InRange(counts[rarity] / 100_000.0, share - 0.01, share + 0.01);
    }

    [Fact]
    public void A_rarity_without_characters_gives_its_share_to_the_others()
    {
        var art = _folders.New();
        CatalogTests.Character(art, "4-common", "miku");
        CatalogTests.Character(art, "3-rare", "joker");
        var catalog = CompanionCatalog.Load([art]);
        // Only common (0.60) and rare (0.25): common is 0.60 / 0.85 of the roll.
        Assert.Equal(Rarity.Common, CompanionRoller.Roll(catalog, Sequence(0.70, 0.0))!.Rarity);
        Assert.Equal(Rarity.Rare, CompanionRoller.Roll(catalog, Sequence(0.71, 0.0))!.Rarity);
        Assert.Null(CompanionRoller.Roll(CompanionCatalog.Load([_folders.New()]), Sequence(0.5)));
    }

    [Fact]
    public void The_first_visit_rolls_once_and_keeps_the_companion()
    {
        using var store = new LibraryStore(_db);
        var keeper = new CompanionKeeper(store, Catalog(), Guard(Sequence(0.10, 0.0)), new MoodRules());

        var bond = keeper.Current(Now)!;
        Assert.Equal("cloud", bond.CompanionId);
        Assert.Equal(LibraryStats.Day(Now), bond.AdoptedDay);
        Assert.Equal("cloud", keeper.Current(Now.AddDays(2))!.CompanionId);
        var roll = Assert.Single(store.RollLog(10));
        Assert.Equal(("first", "cloud", "common", 2), (roll.Reason, roll.CompanionId, roll.Rarity, roll.PoolSize));
    }

    [Fact]
    public void A_companion_whose_art_is_missing_is_kept_not_replaced()
    {
        using var store = new LibraryStore(_db);
        var guard = Guard(Sequence(0.97, 0.0));
        Sealed(store, guard, "geralt", Now.AddDays(-5));
        var keeper = new CompanionKeeper(store, Catalog(), guard, new MoodRules());

        Assert.Null(keeper.View(Now, goal: 10));
        Assert.Equal("geralt", keeper.MissingArt());
        Assert.Equal("geralt", store.ActiveCompanion()!.CompanionId);
        Assert.Empty(store.RollLog(10));
    }

    [Fact]
    public void Only_one_companion_is_active_and_a_new_one_starts_its_own_days()
    {
        using var store = new LibraryStore(_db);
        store.Adopt("miku", Now.AddDays(-10));
        store.Adopt("joker", Now);
        Assert.Equal("joker", store.ActiveCompanion()!.CompanionId);
        store.Adopt("miku", Now.AddDays(1));
        var bond = store.ActiveCompanion()!;
        Assert.Equal(("miku", LibraryStats.Day(Now.AddDays(1))), (bond.CompanionId, bond.AdoptedDay));
    }

    [Fact]
    public void The_view_tells_the_mood_and_the_days_together()
    {
        using var store = new LibraryStore(_db);
        var guard = Guard(Sequence(0.5));
        Sealed(store, guard, "joker", Now.AddDays(-2));
        for (var d = 2; d >= 0; d--)
            store.AddActivity(DayAction.Lookup, Now.AddDays(-d), d == 0 ? 10 : 1);
        var keeper = new CompanionKeeper(store, Catalog(), guard, new MoodRules());

        var view = keeper.View(Now, goal: 10)!;

        Assert.Equal(("joker", Mood.Happy, 3), (view.Companion.Id, view.Mood, view.DaysTogether));
    }

    [Fact]
    public void An_older_library_gets_the_companion_tables_and_keeps_a_backup()
    {
        using (var store = new LibraryStore(_db))
            store.AddActivity(DayAction.Line, Now);
        using (var db = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE companions; DROP TABLE companion_rolls; PRAGMA user_version = 11;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_db);
        Assert.True(File.Exists(_db + ".v11.bak"));
        Assert.Single(migrated.ActivityDays(DateOnly.MinValue));
        Assert.Null(migrated.ActiveCompanion());
        migrated.Adopt("miku", Now);
        Assert.Equal("miku", migrated.ActiveCompanion()!.CompanionId);
    }

    [Fact]
    public void A_bond_from_before_the_seal_retires_with_the_update_and_is_never_sealed_by_it()
    {
        using (var store = new LibraryStore(_db))
            store.AddActivity(DayAction.Line, Now);
        using (var db = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            // A v12 library with a companion written in by hand before the update.
            cmd.CommandText = """
                DROP TABLE companions; DROP TABLE companion_bans;
                CREATE TABLE companions(companion_id TEXT PRIMARY KEY, adopted_utc TEXT NOT NULL, adopted_day TEXT NOT NULL,
                  status TEXT NOT NULL DEFAULT 'active', retired_utc TEXT);
                INSERT INTO companions(companion_id, adopted_utc, adopted_day) VALUES('ueno', '2026-10-01T10:00:00.0000000Z', '2026-10-01');
                PRAGMA user_version = 12;
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_db);

        Assert.True(File.Exists(_db + ".v12.bak"));
        Assert.Null(migrated.ActiveCompanion());
        Assert.Empty(migrated.Bans());
    }
}
