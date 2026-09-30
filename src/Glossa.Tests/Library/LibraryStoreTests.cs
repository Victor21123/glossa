using Glossa.Core.Library;
using Glossa.Core.Pictures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossa.Tests.Library;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glossa-library-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
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
    public void A_corrected_lookup_is_taken_back()
    {
        using var store = new LibraryStore(_path);
        SavedWord Seen(string word, string sentence) =>
            new() { Language = "en", Word = word, Translation = "т", Context = sentence, ShotFile = $"shots/{word}-{sentence.Length}.jpg" };

        // A misread word the lookup created goes away entirely, its frame with it.
        store.Record(Seen("Wching", "Wching you."), newLookup: true, out var misread);
        Assert.True(misread!.NewWord);
        Assert.Equal(["shots/Wching-11.jpg"], store.Retract(misread));
        Assert.Empty(store.List());
        Assert.Empty(store.ListDeleted());

        // A word already there keeps its old sentence and count; only this lookup's sentence goes.
        var id = store.Record(Seen("watching", "Keep watching."), newLookup: true);
        store.Record(Seen("watching", "I'm watching you."), newLookup: true, out var again);
        Assert.False(again!.NewWord);
        Assert.Equal(["shots/watching-17.jpg"], store.Retract(again));
        var kept = Assert.Single(store.List());
        Assert.Equal(id, kept.Id);
        Assert.Equal(1, kept.Lookups);
        Assert.Equal("Keep watching.", Assert.Single(kept.Contexts).Context);

        // A frame another word still shows stays (frames are stored by content: one still frame, many words).
        store.Record(Seen("you", "I'm watching you.") with { ShotFile = "shots/frame.jpg" }, newLookup: true);
        store.Record(Seen("wtching", "I'm wtching you.") with { ShotFile = "shots/frame.jpg" }, newLookup: true, out var sameFrame);
        Assert.Empty(store.Retract(sameFrame!));
        Assert.Equal(2, store.List().Count);

        // A deleted word brought back by the lookup is deleted again.
        store.Delete(id);
        store.Record(Seen("watching", "Keep watching."), newLookup: true, out var back);
        Assert.True(back!.Revived);
        Assert.Empty(store.Retract(back)); // the sentence was already there
        Assert.Equal("you", Assert.Single(store.List()).Word);
        Assert.Single(store.ListDeleted());
    }

    [Fact]
    public void A_meaning_picture_is_kept_through_edits_and_replaced_and_its_query_comes_from_the_first_card()
    {
        using var store = new LibraryStore(_path);
        var id = store.Record(new SavedWord { Language = "en", Word = "bat", PictureQuery = "fruit bat", Context = "A bat flew by." }, newLookup: true);
        store.Record(new SavedWord { Language = "en", Word = "bat", PictureQuery = "baseball bat", Context = "Swing the bat." }, newLookup: true);
        Assert.Equal("fruit bat", Assert.Single(store.List()).PictureQuery);

        var wiki = new MeaningPicture("images/a.jpg", "Википедия", "Ann", "CC BY-SA 3.0", "https://commons.wikimedia.org/wiki/File:A.jpg");
        Assert.Null(store.SetPicture(id, wiki));
        store.Update(Assert.Single(store.List()) with { Translation = "летучая мышь" });
        Assert.Equal(wiki, Assert.Single(store.List()).Picture);

        // The user's own search is remembered for the next one; the replaced file is the caller's to delete.
        Assert.Equal("images/a.jpg", store.SetPicture(id, new MeaningPicture("images/b.jpg", MeaningPicture.OwnSource), query: "flying fox"));
        var word = Assert.Single(store.List());
        Assert.Equal(("images/b.jpg", "flying fox"), (word.Picture!.File, word.PictureQuery));

        Assert.Equal("images/b.jpg", store.SetPicture(id, null));
        Assert.Null(Assert.Single(store.List()).Picture);
    }

    [Fact]
    public void A_picture_value_that_does_not_read_is_no_picture_and_the_library_still_opens()
    {
        string id;
        using (var store = new LibraryStore(_path)) id = store.Record(new SavedWord { Language = "en", Word = "bat" }, newLookup: true);
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE words SET picture = '{\"File\": \"images/cut' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using var reopened = new LibraryStore(_path);
        Assert.Null(Assert.Single(reopened.List()).Picture);
        Assert.Null(reopened.SetPicture(id, new MeaningPicture("images/new.jpg", MeaningPicture.OwnSource)));
    }

    [Fact]
    public void Older_duplicates_merge_into_the_oldest_entry_keeping_its_anki_identity()
    {
        // A library written before sentences were kept per word: one row per word and sentence.
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
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

        using var store = new LibraryStore(_path);
        var word = Assert.Single(store.List());
        Assert.Equal("old", word.Id);
        Assert.Equal(2, word.Lookups);
        Assert.Equal(new[] { "I will reconsider the offer.", "You should reconsider." }, word.Contexts.Select(c => c.Context));
        Assert.Equal("Disco Elysium", word.WindowTitle);
        Assert.Contains(store.ListDeleted(), w => w.Id == "new");
    }
}
