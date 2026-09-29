using Glossa.Core.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossa.Tests.Library;

public sealed class CollectionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glossa-collections-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm", _path + ".v6.bak" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static SavedWord Word(string word, string lang = "en", string game = "Hades II", string? register = null, string? level = null) => new()
    {
        Language = lang,
        Word = word,
        Context = $"… {word} …",
        ContextOffset = 2,
        WindowTitle = game,
        Register = register,
        Level = level,
    };

    [Fact]
    public void Words_go_into_and_out_of_a_manual_collection()
    {
        using var store = new LibraryStore(_path);
        var exam = store.CreateCollection("К экзамену N3", filter: null);
        var id = store.Record(Word("reconsider"), newLookup: true);
        store.Record(Word("tsundere"), newLookup: true);

        store.AddToCollection(exam, id);
        store.AddToCollection(exam, id); // twice is still once
        var collection = Assert.Single(store.Collections());
        Assert.False(collection.IsSmart);
        Assert.Equal(new[] { exam }, store.List().Single(w => w.Id == id).CollectionIds);
        Assert.Empty(store.List().Single(w => w.Headword == "tsundere").CollectionIds);

        store.RemoveFromCollection(exam, id);
        Assert.Empty(store.List().Single(w => w.Id == id).CollectionIds);
    }

    [Fact]
    public void Smart_collection_is_a_saved_filter_over_local_data()
    {
        using var store = new LibraryStore(_path);
        var filter = new SmartFilter { Game = "persona 5 royal", Register = "slang" };
        var id = store.CreateCollection("Persona 5 · сленг", filter);
        store.Record(Word("sus", game: "Persona 5 Royal", register: "slang"), newLookup: true);
        store.Record(Word("please", game: "Persona 5 Royal"), newLookup: true);
        store.Record(Word("cringe", game: "Hades II", register: "slang"), newLookup: true);

        var saved = store.Collections().Single(c => c.Id == id);
        Assert.True(saved.IsSmart);
        Assert.Equal(new[] { "sus" }, store.List().Where(w => saved.Filter!.Matches(w)).Select(w => w.Headword));
    }

    [Fact]
    public void Smart_filter_counts_lookups_and_levels()
    {
        var often = Word("reconsider", level: "B2") with { Lookups = 3 };
        var once = Word("please", level: "A1");
        Assert.True(new SmartFilter { MinLookups = 3 }.Matches(often));
        Assert.False(new SmartFilter { MinLookups = 3 }.Matches(once));
        Assert.True(new SmartFilter { Level = "b2" }.Matches(often));
        Assert.False(new SmartFilter { Language = "ja" }.Matches(often));
        Assert.True(new SmartFilter { PinnedOnly = true }.Matches(once with { Pinned = true }));
    }

    [Fact]
    public void A_version_six_library_gets_hsk_7_9_with_a_hyphen()
    {
        var dash = "HSK 7" + (char)0x2013 + "9";
        using (var store = new LibraryStore(_path))
        {
            store.Record(Word("呵护", lang: "zh", level: dash), newLookup: true);
            store.CreateCollection("HSK 7-9", new SmartFilter { Level = dash });
        }
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 6";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryStore(_path);
        Assert.Equal("HSK 7-9", migrated.List().Single().Level);
        Assert.Equal("HSK 7-9", migrated.Collections().Single().Filter!.Level);
    }

    [Fact]
    public void Deleting_a_collection_keeps_its_words()
    {
        using var store = new LibraryStore(_path);
        var c = store.CreateCollection("Временная", filter: null);
        var id = store.Record(Word("reconsider"), newLookup: true);
        store.AddToCollection(c, id);
        store.RenameCollection(c, "Постоянная");
        Assert.Equal("Постоянная", store.Collections().Single().Name);

        store.DeleteCollection(c);
        Assert.Empty(store.Collections());
        var word = Assert.Single(store.List());
        Assert.Empty(word.CollectionIds);
    }
}
