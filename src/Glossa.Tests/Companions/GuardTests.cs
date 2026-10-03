using Glossa.Core.Companions;
using Glossa.Core.Library;
using Microsoft.Data.Sqlite;

namespace Glossa.Tests.Companions;

/// <summary>The companion's protection offline (the user, 2026-10-03): seal, fate, a week away, the outside copy.</summary>
public sealed class GuardTests : IDisposable
{
    private readonly TestFolders _folders = new();
    private readonly string _art;
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    public GuardTests()
    {
        _art = _folders.New();
        CatalogTests.Character(_art, "1-legendary", "ueno", 189);
        CatalogTests.Character(_art, "2-epic", "alice", 256);
        CatalogTests.Character(_art, "3-rare", "joker", 253);
        CatalogTests.Character(_art, "4-common", "miku", 255);
        CatalogTests.Character(_art, "4-common", "cloud", 255);
    }

    public void Dispose() => _folders.Dispose();

    private string NewDb() => Path.Combine(_folders.New(), "library.db");

    /// <summary>The registry, as the app keeps the ban's copy.</summary>
    private sealed class Registry
    {
        public string? Value;
    }

    private CompanionKeeper Keeper(LibraryStore store, string machine = "pc-1", Registry? registry = null, double draw = 0.999) =>
        new(store, CompanionCatalog.Load([_art]), new CompanionGuard(KeeperTests.SealKey, KeeperTests.FateKey, machine, () => draw,
            fateByPc: true, () => registry?.Value, v => { if (registry is not null) registry.Value = v; }), new MoodRules());

    /// <summary>The library's file changed by hand, as a cheater would with any SQLite editor.</summary>
    private static void Edit(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void The_first_companion_is_this_PCs_fate_whatever_the_dice_say()
    {
        string First(string machine, double draw)
        {
            using var store = new LibraryStore(NewDb());
            return Keeper(store, machine, draw: draw).Current(Now)!.CompanionId;
        }

        // Deleting the library and coming again brings the same companion: no rolling until a legendary falls.
        Assert.Equal(First("pc-1", 0.0), First("pc-1", 0.999));
        Assert.Equal(First("pc-1", 0.5), First("pc-1", 0.1));
    }

    [Fact]
    public void A_companion_written_in_by_hand_sends_the_real_one_away_for_a_week_then_it_comes_back()
    {
        var db = NewDb();
        var registry = new Registry();
        string own;
        using (var store = new LibraryStore(db))
            own = Keeper(store, registry: registry).Current(Now)!.CompanionId;
        var other = own == "ueno" ? "alice" : "ueno";
        Edit(db, $"UPDATE companions SET companion_id = '{other}' WHERE status = 'active'");

        using var reopened = new LibraryStore(db);
        var keeper = Keeper(reopened, registry: registry);
        var later = Now.AddHours(1);

        Assert.Null(keeper.Current(later));
        var away = keeper.Away(later)!;
        Assert.Equal((own, 7, true), (away.Companion.Id, away.DaysLeft, away.JustLeft));
        Assert.Null(keeper.View(later.AddDays(3), goal: 10));
        Assert.False(keeper.Away(later.AddDays(3))!.JustLeft);
        Assert.NotNull(registry.Value);

        var back = keeper.Current(later.AddDays(7).AddMinutes(1))!;
        Assert.Equal(own, back.CompanionId);
        Assert.Null(keeper.Away(later.AddDays(8)));
    }

    [Fact]
    public void Restoring_an_older_library_does_not_end_the_week_away()
    {
        var registry = new Registry();
        var db = NewDb();
        using (var store = new LibraryStore(db))
            Keeper(store, registry: registry).Current(Now);
        Edit(db, "UPDATE companions SET companion_id = 'ueno', adopted_utc = '2026-09-01T00:00:00.0000000Z' WHERE status = 'active'");
        using (var store = new LibraryStore(db))
            Assert.NotNull(Keeper(store, registry: registry).Away(Now));

        // A fresh library (or the backup from before): the copy outside it still keeps the companion away.
        using var fresh = new LibraryStore(NewDb());
        Assert.NotNull(Keeper(fresh, registry: registry).Away(Now.AddDays(2)));
        Assert.Null(Keeper(fresh, registry: registry).Current(Now.AddDays(2)));
    }

    [Fact]
    public void A_library_from_another_PC_is_no_forgery_and_this_PC_gets_its_own_companion()
    {
        var db = NewDb();
        using (var store = new LibraryStore(db))
        {
            var elsewhere = new CompanionGuard(KeeperTests.SealKey, KeeperTests.FateKey, "pc-2", () => 0.5);
            KeeperTests.Sealed(store, elsewhere, "ueno", Now.AddDays(-30));
        }
        string own;
        using (var clean = new LibraryStore(NewDb()))
            own = Keeper(clean).Current(Now)!.CompanionId;

        using var moved = new LibraryStore(db);
        var keeper = Keeper(moved);
        Assert.Equal(own, keeper.Current(Now)!.CompanionId);
        Assert.Null(keeper.Away(Now));
        Assert.Empty(moved.Bans());
    }

    [Fact]
    public void Wiping_the_PC_tag_with_a_legendary_written_in_gets_only_this_PCs_own_companion()
    {
        string own;
        using (var clean = new LibraryStore(NewDb()))
            own = Keeper(clean).Current(Now)!.CompanionId;
        var db = NewDb();
        using (var store = new LibraryStore(db))
            store.Adopt("ueno", Now, null, "made-up seal", null);

        using var reopened = new LibraryStore(db);
        Assert.Equal(own, Keeper(reopened).Current(Now)!.CompanionId);
    }

    [Fact]
    public void A_ban_with_a_made_up_seal_counts_for_nothing()
    {
        var registry = new Registry { Value = "2026-10-03T12:00:00.0000000Z|2026-12-01T00:00:00.0000000Z|ueno|bogus" };
        using var store = new LibraryStore(NewDb());
        store.AddBan(new CompanionBan(Now, Now.AddDays(60), "ueno", "bogus"));

        var keeper = Keeper(store, registry: registry);

        Assert.Null(keeper.Away(Now));
        Assert.NotNull(keeper.Current(Now));
    }

    [Fact]
    public void Another_Windows_account_on_the_PC_gets_the_same_companion_not_a_new_roll()
    {
        string First(string user, double draw)
        {
            using var store = new LibraryStore(NewDb());
            var guard = new CompanionGuard(KeeperTests.SealKey, KeeperTests.FateKey, $"pc-1|{user}", () => draw, fateByPc: true, pc: "pc-1");
            return new CompanionKeeper(store, CompanionCatalog.Load([_art]), guard, new MoodRules()).Current(Now)!.CompanionId;
        }

        Assert.Equal(First("alice-account", 0.1), First("second-account", 0.9));
    }

    [Fact]
    public void The_trays_read_gives_only_a_sealed_companion_that_is_here_and_never_rolls()
    {
        var registry = new Registry();
        var db = NewDb();
        using (var empty = new LibraryStore(db))
        {
            Assert.Null(Keeper(empty, registry: registry).Active(Now));
            Assert.Null(empty.ActiveCompanion());
            Assert.NotNull(Keeper(empty, registry: registry).Current(Now));
            Assert.NotNull(Keeper(empty, registry: registry).Active(Now));
        }
        Edit(db, "UPDATE companions SET companion_id = 'ueno', adopted_utc = '2026-09-01T00:00:00.0000000Z' WHERE status = 'active'");

        using var store = new LibraryStore(db);
        Assert.Null(Keeper(store, registry: registry).Active(Now));
        Assert.Empty(store.Bans());
    }

    [Fact]
    public void The_admin_pick_is_sealed_and_ends_a_week_away()
    {
        var registry = new Registry();
        var db = NewDb();
        using (var store = new LibraryStore(db))
            Keeper(store, registry: registry).Current(Now);
        Edit(db, "UPDATE companions SET adopted_utc = '2026-09-01T00:00:00.0000000Z' WHERE status = 'active'");

        using var reopened = new LibraryStore(db);
        var keeper = Keeper(reopened, registry: registry);
        Assert.NotNull(keeper.Away(Now));
        keeper.Pick("joker", Now.AddHours(1));

        Assert.Null(keeper.Away(Now.AddHours(2)));
        Assert.Equal("joker", keeper.Current(Now.AddHours(2))!.CompanionId);
        Assert.Equal("dev", reopened.RollLog(1)[0].Reason);
    }
}
