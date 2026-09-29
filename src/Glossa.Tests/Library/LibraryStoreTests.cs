using Glossa.Core.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossa.Tests.Library;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glossa-library-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm", _path + ".v2.bak" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static SavedWord Lookup(string sentence, string here, string game, DateTime at) => new()
    {
        Language = "en",
        Word = "reconsider",
        Translation = "передумать",
        Context = sentence,
        ContextOffset = sentence.IndexOf("reconsider", StringComparison.Ordinal),
        ContextTranslation = "перевод: " + sentence,
        UsageNote = here,
        WindowTitle = game,
        CreatedUtc = at,
    };

    [Fact]
    public void One_word_found_in_two_sentences_is_one_entry_with_both_sentences()
    {
        using var store = new LibraryStore(_path);
        var t0 = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        var first = store.Record(Lookup("You should reconsider.", "одуматься", "Hades II", t0), newLookup: true);
        var second = store.Record(Lookup("I will reconsider the offer.", "пересмотреть", "Disco Elysium", t0.AddDays(2)), newLookup: true);

        Assert.Equal(first, second);
        var word = Assert.Single(store.List());
        Assert.Equal(2, word.Lookups);
        Assert.Equal(2, word.Contexts.Count);
        Assert.Equal("I will reconsider the offer.", word.Contexts[0].Context); // newest first
        Assert.Equal("пересмотреть", word.Contexts[0].UsageNote);
        Assert.Equal("одуматься", word.Contexts[1].UsageNote);
        Assert.Equal("I will reconsider the offer.", word.Context); // the word mirrors its newest sentence for exports
    }

    [Fact]
    public void Saving_the_same_lookup_again_does_not_count_twice()
    {
        using var store = new LibraryStore(_path);
        var lookup = Lookup("You should reconsider.", "одуматься", "Hades II", DateTime.UtcNow);
        var id = store.Record(lookup, newLookup: true);
        store.Record(lookup with { Id = id, ContextTranslation = "уточнённый перевод" }, newLookup: false);

        var word = Assert.Single(store.List());
        Assert.Equal(1, word.Lookups);
        var context = Assert.Single(word.Contexts);
        Assert.Equal("уточнённый перевод", context.ContextTranslation);
    }

    [Fact]
    public void The_same_sentence_looked_up_later_counts_but_stays_one_sentence()
    {
        using var store = new LibraryStore(_path);
        store.Record(Lookup("You should reconsider.", "одуматься", "Hades II", DateTime.UtcNow), newLookup: true);
        store.Record(Lookup("You should reconsider.", "одуматься", "Hades II", DateTime.UtcNow), newLookup: true);

        var word = Assert.Single(store.List());
        Assert.Equal(2, word.Lookups);
        Assert.Single(word.Contexts);
    }

    [Fact]
    public void A_new_lookup_keeps_what_the_user_edited()
    {
        using var store = new LibraryStore(_path);
        var id = store.Record(Lookup("You should reconsider.", "одуматься", "Hades II", DateTime.UtcNow), newLookup: true);
        var word = store.List()[0];
        store.Update(word with { Translation = "передумать (моё)" }, word.Contexts[0] with { UsageNote = "моё пояснение" });

        store.Record(Lookup("I will reconsider the offer.", "пересмотреть", "Disco Elysium", DateTime.UtcNow.AddMinutes(1)), newLookup: true);

        var after = store.List()[0];
        Assert.Equal(id, after.Id);
        Assert.Equal("передумать (моё)", after.Translation);
        Assert.Equal("моё пояснение", after.Contexts.Single(c => c.Context == "You should reconsider.").UsageNote);
    }

    [Fact]
    public void Pin_is_stored()
    {
        using var store = new LibraryStore(_path);
        var id = store.Record(Lookup("You should reconsider.", "одуматься", "Hades II", DateTime.UtcNow), newLookup: true);
        store.SetPinned(id, true);
        Assert.True(store.List()[0].Pinned);
        store.SetPinned(id, false);
        Assert.False(store.List()[0].Pinned);
    }

    [Fact]
    public void Older_duplicates_merge_into_the_oldest_entry_keeping_its_anki_identity()
    {
        // A library written before sentences were kept per word: one row per word and sentence.
        using (var db = new SqliteConnection($"Data Source={_path}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE words(id TEXT PRIMARY KEY, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, language TEXT NOT NULL,
                  word TEXT NOT NULL, dictionary_form TEXT, reading TEXT, part_of_speech TEXT, level TEXT, definition TEXT, translation TEXT,
                  context TEXT, context_offset INTEGER NOT NULL DEFAULT -1, context_translation TEXT, explanation TEXT, synonyms TEXT,
                  key_forms TEXT, components TEXT, app_exe TEXT, window_title TEXT, shot_file TEXT, word_box TEXT,
                  deleted INTEGER NOT NULL DEFAULT 0, register TEXT, usage_note TEXT);
                INSERT INTO words(id, created_utc, updated_utc, language, word, translation, context, usage_note, window_title)
                  VALUES ('old', '2026-09-20T10:00:00.0000000Z', '2026-09-20T10:00:00.0000000Z', 'en', 'reconsider', 'передумать',
                          'You should reconsider.', 'одуматься', 'Hades II');
                INSERT INTO words(id, created_utc, updated_utc, language, word, translation, context, usage_note, window_title)
                  VALUES ('new', '2026-09-22T10:00:00.0000000Z', '2026-09-22T10:00:00.0000000Z', 'en', 'reconsider', 'пересмотреть',
                          'I will reconsider the offer.', 'пересмотреть', 'Disco Elysium');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new LibraryStore(_path);
        var word = Assert.Single(store.List());
        Assert.Equal("old", word.Id);
        Assert.Equal(2, word.Lookups);
        Assert.Equal(new[] { "I will reconsider the offer.", "You should reconsider." }, word.Contexts.Select(c => c.Context));
        Assert.Equal("Disco Elysium", word.WindowTitle);
        Assert.Contains(store.ListDeleted(), w => w.Id == "new");
    }
}
