using System.Security.Cryptography;
using Glossa.Core.Companions;

namespace Glossa.Tests.Companions;

public sealed class PackTests : IDisposable
{
    private readonly TestFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private Dictionary<string, byte[]> Catalog()
    {
        var root = _folders.New();
        CatalogTests.Character(root, "1-legendary", "ueno", 189, null, "blink");
        CatalogTests.Character(root, "4-common", "cloud", 255);
        File.WriteAllBytes(Path.Combine(root, "4-common", "cloud", "cloud_sheet.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "README.md"), "notes");
        return CompanionPack.ReadFolder(root);
    }

    [Fact]
    public void The_pack_opens_with_its_key_and_holds_the_catalog_without_sheets_or_notes()
    {
        var files = Catalog();
        var key = RandomNumberGenerator.GetBytes(32);

        var opened = CompanionPack.Open(CompanionPack.Seal(files, key), key);

        Assert.NotNull(opened);
        Assert.DoesNotContain(opened!.Keys, k => k.EndsWith("_sheet.png") || k.EndsWith(".md"));
        Assert.Contains("1-legendary/ueno/ueno_blink.png", opened.Keys);
        var catalog = CompanionCatalog.Load(opened);
        Assert.Equal(new[] { "ueno", "cloud" }, catalog.All.Select(c => c.Id));
    }

    [Fact]
    public void Another_key_or_one_changed_byte_opens_nothing()
    {
        var files = Catalog();
        var key = RandomNumberGenerator.GetBytes(32);
        var pack = CompanionPack.Seal(files, key);

        Assert.Null(CompanionPack.Open(pack, RandomNumberGenerator.GetBytes(32)));
        var changed = (byte[])pack.Clone();
        changed[^10] ^= 1;
        Assert.Null(CompanionPack.Open(changed, key));
        Assert.Null(CompanionPack.Open("not a pack"u8, key));
    }

    [Fact]
    public void The_program_reads_only_the_pack_and_a_build_without_the_key_has_no_companions()
    {
        var files = Catalog();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(_folders.New(), "companions.pack");
        File.WriteAllBytes(path, CompanionPack.Seal(files, key));

        Assert.Equal(2, CompanionCatalog.LoadPack(path, key).All.Count);
        Assert.Empty(CompanionCatalog.LoadPack(path, null).All);
        Assert.Empty(CompanionCatalog.LoadPack(path, RandomNumberGenerator.GetBytes(32)).All);
        Assert.Empty(CompanionCatalog.LoadPack(Path.Combine(_folders.New(), "missing.pack"), key).All);
    }

    [Fact]
    public void The_keys_are_masked_in_the_generated_file_and_unmasked_at_start()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);

        var masked = CompanionSecrets.Mask(key, salt);

        Assert.NotEqual(key, masked);
        Assert.Equal(key, CompanionSecrets.Unmask(masked, salt));
    }

    [Fact]
    public void The_secret_word_matches_only_itself()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var word = new AdminWord(salt, AdminWord.Derive("лунная дорожка над морем", salt));

        Assert.True(word.Matches("лунная дорожка над морем"));
        Assert.False(word.Matches("лунная дорожка над мором"));
    }
}
