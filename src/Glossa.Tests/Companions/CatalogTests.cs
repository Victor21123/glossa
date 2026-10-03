using Glossa.Core.Companions;
using Glossa.Core.Logging;

namespace Glossa.Tests.Companions;

public sealed class CatalogTests : IDisposable
{
    private readonly TestFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>A PNG header of the given size: the catalog reads sizes from it and never decodes the picture.</summary>
    internal static void FakePng(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }
            .CopyTo(bytes, 0);
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        File.WriteAllBytes(path, bytes);
    }

    internal static void Character(string root, string rarity, string id, int size = 100, string? json = null,
        params string[] frames)
    {
        var dir = Path.Combine(root, rarity, id);
        FakePng(Path.Combine(dir, $"{id}_base.png"), size, size);
        foreach (var f in frames)
            FakePng(Path.Combine(dir, $"{id}_{f}.png"), size, size);
        File.WriteAllText(Path.Combine(dir, $"{id}.json"),
            json ?? $$"""{"grid": 4.0, "head_bottom": 70, "name": "{{id}} name", "game": "{{id}} game", "gender": "f"}""");
    }

    private sealed class Log : ILog
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) => Warnings.Add(message);
    }

    [Fact]
    public void Rarities_come_from_numbered_folders_and_their_chances_add_up()
    {
        Assert.Equal(Rarity.Legendary, Rarities.FromFolder("1-legendary"));
        Assert.Equal(Rarity.Common, Rarities.FromFolder("4-common"));
        Assert.Equal(Rarity.Rare, Rarities.FromFolder("rare"));
        Assert.Null(Rarities.FromFolder("5-mythic"));
        Assert.Null(Rarities.FromFolder("_review"));
        Assert.Equal("2-epic", Rarities.Folder(Rarity.Epic));
        Assert.Equal(1.0, Rarities.Chances.Values.Sum(), 9);
        Assert.Equal((0.60, 0.25, 0.12, 0.03),
            (Rarities.Chances[Rarity.Common], Rarities.Chances[Rarity.Rare], Rarities.Chances[Rarity.Epic],
             Rarities.Chances[Rarity.Legendary]));
    }

    [Fact]
    public void A_character_is_a_folder_and_a_broken_one_is_skipped_with_a_note()
    {
        var root = _folders.New();
        Character(root, "1-legendary", "ueno", 189, null, "blink", "breath");
        Character(root, "4-common", "cloud", 255);
        Directory.CreateDirectory(Path.Combine(root, "3-rare", "nobase"));
        File.WriteAllText(Path.Combine(root, "3-rare", "nobase", "nobase.json"), "{}");
        Character(root, "2-epic", "badjson", 100, "{ not json");
        Character(root, "notes", "stray", 100);
        var log = new Log();

        var catalog = CompanionCatalog.Load([root], log);

        Assert.Equal(new[] { "cloud", "ueno" }, catalog.All.Select(c => c.Id).Order());
        var ueno = catalog.Find("ueno")!;
        Assert.Equal((Rarity.Legendary, "ueno name", "ueno game", "f", 189, 189),
            (ueno.Rarity, ueno.Name, ueno.Game, ueno.Gender, ueno.Width, ueno.Height));
        Assert.NotSame(ueno.Frame(CompanionFrames.Base), ueno.Frame(CompanionFrames.Blink));
        Assert.Same(ueno.Frame(CompanionFrames.Base), ueno.Frame(CompanionFrames.Sad));
        Assert.False(ueno.Has(CompanionFrames.Sad));
        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains(log.Warnings, w => w.Contains("nobase"));
        Assert.Contains(log.Warnings, w => w.Contains("badjson"));
        Assert.Equal(255, catalog.TallestHeight);
        Assert.Equal(new[] { "ueno" }, catalog.Of(Rarity.Legendary).Select(c => c.Id));
        Assert.Empty(catalog.Of(Rarity.Epic));
    }

    [Fact]
    public void A_later_root_replaces_a_character_of_the_same_name()
    {
        var shipped = _folders.New();
        var own = _folders.New();
        Character(shipped, "4-common", "miku", json: """{"name": "Shipped", "game": "g"}""");
        Character(own, "2-epic", "miku", json: """{"name": "Own", "game": "g"}""");

        var catalog = CompanionCatalog.Load([shipped, own, Path.Combine(own, "missing")]);

        var miku = Assert.Single(catalog.All);
        Assert.Equal((Rarity.Epic, "Own"), (miku.Rarity, miku.Name));
    }

    [Fact]
    public void A_frame_of_another_size_is_left_out_and_the_base_shows_instead()
    {
        var root = _folders.New();
        Character(root, "3-rare", "joker", 253, null, "happy");
        FakePng(Path.Combine(root, "3-rare", "joker", "joker_blink.png"), 10, 10);
        var log = new Log();

        var joker = CompanionCatalog.Load([root], log).Find("joker")!;

        Assert.True(joker.Has(CompanionFrames.Happy));
        Assert.False(joker.Has(CompanionFrames.Blink));
        Assert.Same(joker.Frame(CompanionFrames.Base), joker.Frame(CompanionFrames.Blink));
        Assert.Contains(log.Warnings, w => w.Contains("joker_blink"));
    }

    [Fact]
    public void The_sleep_is_a_pose_of_its_own_and_may_be_of_another_size()
    {
        var root = _folders.New();
        Character(root, "3-rare", "kiryu", 255);
        FakePng(Path.Combine(root, "3-rare", "kiryu", "kiryu_sleep.png"), 252, 252);
        var log = new Log();

        var kiryu = CompanionCatalog.Load([root], log).Find("kiryu")!;

        Assert.True(kiryu.Has(CompanionFrames.Sleep));
        Assert.Empty(log.Warnings);
    }

    /// <summary>
    /// The author's catalog (assets\companions, kept out of the repository - only the encrypted pack ships): every
    /// approved character complete - all frames of one size, a name and a game - and every rarity present. Fails on a
    /// folder added half-done; passes with nothing to check in a clone without the art.
    /// </summary>
    [Fact]
    public void The_shipped_companions_are_complete()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glossa.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var art = Path.Combine(dir!.FullName, "assets", "companions");
        if (!Directory.Exists(art))
            return; // a public clone: the art is the author's, not in the repository
        var log = new Log();

        var catalog = CompanionCatalog.Load([art], log);

        Assert.Empty(log.Warnings);
        Assert.True(catalog.All.Count >= 11, $"{catalog.All.Count} companions");
        foreach (var rarity in Enum.GetValues<Rarity>())
            Assert.NotEmpty(catalog.Of(rarity));
        foreach (var c in catalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Game), c.Id);
            Assert.Contains(c.Gender, new[] { "f", "m" });
            foreach (var frame in CompanionFrames.All)
                Assert.True(c.Has(frame), $"{c.Id}: {frame}");
        }
    }
}
