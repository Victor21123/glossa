using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Glossa.Core.Library;

/// <summary>
/// A word in the user's library: one entry per word, however many sentences it was found in.
/// <see cref="Id"/> is permanent and doubles as the Anki note identity. The context fields mirror the newest
/// sentence (<see cref="Contexts"/>[0]) so exports keep working with one sentence per word.
/// </summary>
public sealed record SavedWord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public required string Language { get; init; }
    public required string Word { get; init; }
    public string? DictionaryForm { get; init; }
    public string? Reading { get; init; }
    public string? PartOfSpeech { get; init; }
    public string? Level { get; init; }
    public string? Definition { get; init; }

    /// <summary>The definition in the user's language when <see cref="Definition"/> is in the word's own.</summary>
    public string? DefinitionTranslation { get; init; }

    public string? Translation { get; init; }
    public string? Context { get; init; }
    public int ContextOffset { get; init; } = -1;
    public string? ContextTranslation { get; init; }
    public string? Explanation { get; init; }
    public string? Register { get; init; }
    public string? UsageNote { get; init; }
    public IReadOnlyList<string> Synonyms { get; init; } = [];
    public IReadOnlyList<string> KeyForms { get; init; } = [];
    public IReadOnlyList<CardComponent> Components { get; init; } = [];
    public string? AppExe { get; init; }
    public string? WindowTitle { get; init; }
    /// <summary>Screenshot path relative to the data folder.</summary>
    public string? ShotFile { get; init; }
    /// <summary>Where the word is inside the screenshot, in screenshot pixels.</summary>
    public PixelRect? WordBox { get; init; }

    /// <summary>How many times the word was looked up, in any sentence.</summary>
    public int Lookups { get; init; } = 1;

    /// <summary>«Не могу запомнить»: the word goes into every study session until unpinned.</summary>
    public bool Pinned { get; init; }

    /// <summary>When the word was pinned: answers after it count towards suggesting to unpin it.</summary>
    public DateTime? PinnedUtc { get; init; }

    /// <summary>Every sentence the word was found in, newest first.</summary>
    public IReadOnlyList<WordContext> Contexts { get; init; } = [];

    /// <summary>Manual collections the word was put into (smart ones are computed, not stored).</summary>
    public IReadOnlyList<string> CollectionIds { get; init; } = [];

    public string Headword => DictionaryForm ?? Word;

    /// <summary>When the word was last met (its newest sentence).</summary>
    public DateTime LastSeenUtc => Contexts.Count > 0 ? Contexts[0].CreatedUtc : CreatedUtc;

    public static SavedWord FromCard(WordCard card, string context, int contextOffset) => new()
    {
        Language = card.Language,
        Word = card.Word,
        DictionaryForm = card.DictionaryForm,
        Reading = card.Reading,
        PartOfSpeech = card.PartOfSpeech,
        Level = card.Level,
        Definition = card.Definition,
        DefinitionTranslation = card.DefinitionTranslation,
        Translation = card.Translation,
        Context = context,
        ContextOffset = contextOffset,
        ContextTranslation = card.ContextTranslation,
        Explanation = card.Explanation,
        Register = card.Register,
        UsageNote = card.UsageNote,
        Synonyms = card.Synonyms,
        KeyForms = card.KeyForms,
        Components = card.Components,
    };
}

/// <summary>One sentence a word was found in, with what the AI said about the word there.</summary>
public sealed record WordContext
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string? Context { get; init; }
    public int ContextOffset { get; init; } = -1;

    /// <summary>The word as it stood in this sentence (a conjugated form may differ from the headword).</summary>
    public string? Surface { get; init; }

    public string? ContextTranslation { get; init; }

    /// <summary>The translation chosen for this sentence (the word's own translation may differ).</summary>
    public string? Translation { get; init; }

    /// <summary>«Контекст сцены»: what the word means in this sentence and why.</summary>
    public string? UsageNote { get; init; }

    public string? AppExe { get; init; }
    public string? WindowTitle { get; init; }
    public string? ShotFile { get; init; }
    public PixelRect? WordBox { get; init; }
}

public sealed partial class LibraryStore : IDisposable
{
    private const int SchemaVersion = 7;
    private readonly SqliteConnection _db;
    private readonly string _path;
    private readonly object _gate = new();

    public LibraryStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _path = dbPath;
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is > 0 and < SchemaVersion)
        {
            // A consistent copy of the user's library before its schema changes: library.db.v2.bak next to it.
            var backup = $"{_path}.v{version}.bak";
            if (!File.Exists(backup)) Exec($"VACUUM INTO '{backup.Replace("'", "''")}'");
        }
        if (version < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS words(
                  id TEXT PRIMARY KEY,
                  created_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  language TEXT NOT NULL,
                  word TEXT NOT NULL,
                  dictionary_form TEXT, reading TEXT, part_of_speech TEXT, level TEXT,
                  definition TEXT, translation TEXT,
                  context TEXT, context_offset INTEGER NOT NULL DEFAULT -1, context_translation TEXT,
                  explanation TEXT, synonyms TEXT, key_forms TEXT, components TEXT,
                  app_exe TEXT, window_title TEXT, shot_file TEXT, word_box TEXT,
                  deleted INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS words_headword ON words(language, dictionary_form, word);
                CREATE INDEX IF NOT EXISTS words_created ON words(created_utc);
                """);
        }
        if (version < 2)
        {
            // Register (vulgar, slang, …) and a usage note from the AI card.
            Exec("ALTER TABLE words ADD COLUMN register TEXT; ALTER TABLE words ADD COLUMN usage_note TEXT;");
        }
        if (version < 3)
        {
            // One entry per word: sentences move to their own table, repeated lookups count, «не могу запомнить».
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE contexts(
                  id TEXT PRIMARY KEY,
                  word_id TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  context TEXT, context_offset INTEGER NOT NULL DEFAULT -1, surface TEXT, context_translation TEXT,
                  translation TEXT, usage_note TEXT,
                  app_exe TEXT, window_title TEXT, shot_file TEXT, word_box TEXT);
                CREATE INDEX contexts_word ON contexts(word_id, created_utc);
                ALTER TABLE words ADD COLUMN lookups INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE words ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;
                INSERT INTO contexts(id, word_id, created_utc, context, context_offset, surface, context_translation, translation,
                                     usage_note, app_exe, window_title, shot_file, word_box)
                  SELECT lower(hex(randomblob(16))), id, created_utc, context, context_offset, word, context_translation, translation,
                         usage_note, app_exe, window_title, shot_file, word_box
                  FROM words;
                """);
            MergeDuplicates();
            tx.Commit();
        }
        if (version < 4)
        {
            // Collections: manual ones list their words, smart ones keep a filter (JSON) and list nothing.
            Exec("""
                CREATE TABLE collections(
                  id TEXT PRIMARY KEY,
                  name TEXT NOT NULL,
                  filter TEXT,
                  created_utc TEXT NOT NULL);
                CREATE TABLE collection_words(
                  collection_id TEXT NOT NULL,
                  word_id TEXT NOT NULL,
                  added_utc TEXT NOT NULL,
                  PRIMARY KEY(collection_id, word_id));
                """);
        }
        if (version < 5)
        {
            // The definition in the user's language beside a monolingual (English) one.
            Exec("ALTER TABLE words ADD COLUMN definition_tr TEXT;");
        }
        if (version < 6)
        {
            // Study: Anki's card state per word (a word without a row is new), its review log, and when a word was pinned.
            // Ease is kept in thousandths, as Anki keeps it, so a saved state reads back to the same float.
            // One transaction: a migration cut short must not leave half the tables for the next start to trip over.
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE review_state(
                  word_id TEXT PRIMARY KEY,
                  queue TEXT NOT NULL,
                  remaining_steps INTEGER NOT NULL DEFAULT 0,
                  due_at TEXT, due_day TEXT,
                  interval_days INTEGER NOT NULL DEFAULT 0,
                  ease INTEGER NOT NULL DEFAULT 0,
                  reps INTEGER NOT NULL DEFAULT 0,
                  lapses INTEGER NOT NULL DEFAULT 0,
                  leech INTEGER NOT NULL DEFAULT 0,
                  answered_utc TEXT);
                CREATE TABLE review_log(
                  id TEXT PRIMARY KEY,
                  word_id TEXT NOT NULL,
                  answered_utc TEXT NOT NULL,
                  rating INTEGER NOT NULL,
                  queue_before TEXT NOT NULL,
                  early INTEGER NOT NULL DEFAULT 0,
                  interval_before INTEGER NOT NULL,
                  interval_after INTEGER NOT NULL,
                  ease INTEGER NOT NULL,
                  taken_ms INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX review_log_word ON review_log(word_id, answered_utc);
                CREATE INDEX review_log_time ON review_log(answered_utc);
                ALTER TABLE words ADD COLUMN pinned_utc TEXT;
                UPDATE words SET pinned_utc = updated_utc WHERE pinned = 1;
                PRAGMA user_version = 6;
                """);
            tx.Commit();
        }
        if (version < 7)
        {
            // "HSK 7-9" with a hyphen: levels and smart filters were written with an en dash (char 8211); the filters'
            // JSON keeps it escaped as backslash (char 92) + "u2013".
            Exec("""
                UPDATE words SET level = 'HSK 7-9' WHERE level = 'HSK 7' || char(8211) || '9';
                UPDATE collections
                  SET filter = replace(replace(filter, 'HSK 7' || char(8211) || '9', 'HSK 7-9'),
                                       'HSK 7' || char(92) || 'u20139', 'HSK 7-9')
                  WHERE filter IS NOT NULL;
                """);
        }
        Exec($"PRAGMA user_version = {SchemaVersion}");
    }

    /// <summary>
    /// Earlier versions kept one row per word and sentence. The oldest row survives (its id is the Anki note);
    /// the others give it their sentences and are soft-deleted so Anki sync can retire their notes.
    /// </summary>
    private void MergeDuplicates()
    {
        var groups = new List<List<string>>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT language, COALESCE(dictionary_form, word) AS head, id FROM words
                WHERE deleted = 0 ORDER BY language, head, created_utc
                """;
            using var r = cmd.ExecuteReader();
            string? key = null;
            while (r.Read())
            {
                var k = r.GetString(0) + "\u0001" + r.GetString(1);
                if (k != key) groups.Add([]);
                key = k;
                groups[^1].Add(r.GetString(2));
            }
        }
        foreach (var ids in groups.Where(g => g.Count > 1))
        {
            var keep = ids[0];
            foreach (var other in ids.Skip(1))
            {
                Run("UPDATE contexts SET word_id = $keep WHERE word_id = $other", ("$keep", keep), ("$other", other));
                Run("UPDATE words SET deleted = 1 WHERE id = $other", ("$other", other));
            }
            Run("UPDATE words SET lookups = (SELECT COUNT(*) FROM contexts WHERE word_id = $keep) WHERE id = $keep", ("$keep", keep));
            MirrorNewest(keep);
        }
    }

    /// <summary>
    /// Records a lookup: a new word, or another sentence (or the same one again) for a word already in the library.
    /// What the user edited stays; empty word fields are filled from the card. <paramref name="newLookup"/> is false
    /// when the same lookup is saved again (the S key after autosave): it updates that sentence and does not count.
    /// Returns the word's id.
    /// </summary>
    public string Record(SavedWord w, bool newLookup)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var id = !newLookup && Exists(w.Id) ? w.Id : FindWord(w.Language, w.Headword);
            if (id is null)
            {
                id = w.Id;
                InsertWord(w);
            }
            else
            {
                FillBlanks(id, w);
                if (newLookup) Run("UPDATE words SET lookups = lookups + 1 WHERE id = $id", ("$id", id));
            }
            SaveContext(id, w, newLookup);
            MirrorNewest(id);
            tx.Commit();
            return id;
        }
    }

    /// <summary>The user's edits: word fields overwrite, and the edited sentence if one is given.</summary>
    public void Update(SavedWord w, WordContext? context = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE words SET updated_utc = $updated, word = $word, dictionary_form = $dict, reading = $reading,
                      part_of_speech = $pos, level = $level, definition = $def, definition_tr = $deftr, translation = $tr, explanation = $expl,
                      synonyms = $syn, key_forms = $forms, components = $comp, register = $register
                    WHERE id = $id
                    """;
                Bind(cmd, w);
                cmd.ExecuteNonQuery();
            }
            if (context is not null)
                Run("""
                    UPDATE contexts SET context_translation = $ctxtr, translation = $tr, usage_note = $usage
                    WHERE id = $cid AND word_id = $id
                    """, ("$ctxtr", context.ContextTranslation), ("$tr", context.Translation), ("$usage", context.UsageNote),
                    ("$cid", context.Id), ("$id", w.Id));
            MirrorNewest(w.Id);
            tx.Commit();
        }
    }

    public void SetPinned(string id, bool pinned)
    {
        // Pinning again keeps the first time; unpinning forgets it.
        lock (_gate) Run("""
            UPDATE words SET pinned = $p, updated_utc = $now,
              pinned_utc = CASE WHEN $p = 1 THEN COALESCE(CASE WHEN pinned = 1 THEN pinned_utc END, $now) END
            WHERE id = $id
            """, ("$p", pinned ? 1 : 0), ("$now", Iso(DateTime.UtcNow)), ("$id", id));
    }

    public IReadOnlyList<WordCollection> Collections()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, filter FROM collections ORDER BY created_utc";
            using var r = cmd.ExecuteReader();
            var list = new List<WordCollection>();
            while (r.Read())
            {
                var filter = r.IsDBNull(2) ? null : JsonSerializer.Deserialize<SmartFilter>(r.GetString(2));
                list.Add(new WordCollection(r.GetString(0), r.GetString(1), filter));
            }
            return list;
        }
    }

    /// <summary>A manual collection when <paramref name="filter"/> is null, a smart one otherwise. Returns its id.</summary>
    public string CreateCollection(string name, SmartFilter? filter)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate) Run("INSERT INTO collections(id, name, filter, created_utc) VALUES($id, $name, $filter, $now)",
            ("$id", id), ("$name", name.Trim()), ("$filter", filter is null ? null : JsonSerializer.Serialize(filter)), ("$now", Iso(DateTime.UtcNow)));
        return id;
    }

    public void RenameCollection(string id, string name)
    {
        lock (_gate) Run("UPDATE collections SET name = $name WHERE id = $id", ("$name", name.Trim()), ("$id", id));
    }

    public void UpdateFilter(string id, SmartFilter filter)
    {
        lock (_gate) Run("UPDATE collections SET filter = $filter WHERE id = $id", ("$filter", JsonSerializer.Serialize(filter)), ("$id", id));
    }

    /// <summary>Removes the collection; its words stay in the library.</summary>
    public void DeleteCollection(string id)
    {
        lock (_gate)
        {
            Run("DELETE FROM collection_words WHERE collection_id = $id", ("$id", id));
            Run("DELETE FROM collections WHERE id = $id", ("$id", id));
        }
    }

    public void AddToCollection(string collectionId, string wordId)
    {
        lock (_gate) Run("INSERT OR IGNORE INTO collection_words(collection_id, word_id, added_utc) VALUES($c, $w, $now)",
            ("$c", collectionId), ("$w", wordId), ("$now", Iso(DateTime.UtcNow)));
    }

    public void RemoveFromCollection(string collectionId, string wordId)
    {
        lock (_gate) Run("DELETE FROM collection_words WHERE collection_id = $c AND word_id = $w", ("$c", collectionId), ("$w", wordId));
    }

    /// <summary>Words the user removed; Anki sync tags their notes instead of deleting them.</summary>
    public IReadOnlyList<SavedWord> ListDeleted() => Load(deleted: true);

    /// <summary>The library, most recently met words first.</summary>
    public IReadOnlyList<SavedWord> List() => Load(deleted: false);

    public int Count()
    {
        lock (_gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM words WHERE deleted = 0"), CultureInfo.InvariantCulture);
    }

    public bool Contains(string language, string headword)
    {
        lock (_gate) return FindWord(language, headword) is not null;
    }

    /// <summary>Soft delete: the row stays so Anki sync can tag the note instead of losing track of it.</summary>
    public void Delete(string id)
    {
        lock (_gate) Run("UPDATE words SET deleted = 1, updated_utc = $now WHERE id = $id", ("$id", id), ("$now", Iso(DateTime.UtcNow)));
    }

    private IReadOnlyList<SavedWord> Load(bool deleted)
    {
        lock (_gate)
        {
            var contexts = new Dictionary<string, List<WordContext>>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT c.* FROM contexts c JOIN words w ON w.id = c.word_id
                    WHERE w.deleted = $deleted ORDER BY c.created_utc DESC
                    """;
                cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var wordId = (string)r["word_id"];
                    if (!contexts.TryGetValue(wordId, out var list)) contexts[wordId] = list = [];
                    list.Add(ReadContext(r));
                }
            }
            var collections = new Dictionary<string, List<string>>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT word_id, collection_id FROM collection_words ORDER BY added_utc";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!collections.TryGetValue(r.GetString(0), out var ids)) collections[r.GetString(0)] = ids = [];
                    ids.Add(r.GetString(1));
                }
            }
            var words = new List<SavedWord>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM words WHERE deleted = $deleted";
                cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var w = ReadWord(r);
                    if (contexts.TryGetValue(w.Id, out var list)) w = w with { Contexts = list };
                    if (collections.TryGetValue(w.Id, out var ids)) w = w with { CollectionIds = ids };
                    words.Add(w);
                }
            }
            return words.OrderByDescending(w => w.LastSeenUtc).ToList();
        }
    }

    private bool Exists(string id) =>
        Scalar("SELECT 1 FROM words WHERE deleted = 0 AND id = $id", ("$id", id)) is not null;

    private string? FindWord(string language, string headword) =>
        Scalar("SELECT id FROM words WHERE deleted = 0 AND language = $l AND COALESCE(dictionary_form, word) = $h ORDER BY created_utc LIMIT 1",
            ("$l", language), ("$h", headword)) as string;

    private void InsertWord(SavedWord w)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO words(id, created_utc, updated_utc, language, word, dictionary_form, reading, part_of_speech,
              level, definition, translation, context, context_offset, context_translation, explanation, synonyms,
              key_forms, components, app_exe, window_title, shot_file, word_box, register, usage_note, lookups, pinned, definition_tr)
            VALUES($id, $created, $updated, $lang, $word, $dict, $reading, $pos, $level, $def, $tr, $ctx, $off,
              $ctxtr, $expl, $syn, $forms, $comp, $exe, $title, $shot, $box, $register, $usage, 1, 0, $deftr)
            """;
        Bind(cmd, w);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A later lookup only fills what is still empty, so the user's edits survive.</summary>
    private void FillBlanks(string id, SavedWord w)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE words SET updated_utc = $updated,
              reading = COALESCE(reading, $reading), part_of_speech = COALESCE(part_of_speech, $pos),
              level = COALESCE(level, $level), definition = COALESCE(definition, $def), definition_tr = COALESCE(definition_tr, $deftr),
              translation = COALESCE(translation, $tr),
              explanation = COALESCE(explanation, $expl), register = COALESCE(register, $register),
              synonyms = CASE WHEN synonyms IS NULL OR synonyms = '[]' THEN $syn ELSE synonyms END,
              key_forms = CASE WHEN key_forms IS NULL OR key_forms = '[]' THEN $forms ELSE key_forms END,
              components = CASE WHEN components IS NULL OR components = '[]' THEN $comp ELSE components END
            WHERE id = $wid
            """;
        Bind(cmd, w);
        cmd.Parameters.AddWithValue("$wid", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the sentence, or refreshes it when the word already has it. A repeated lookup keeps the user's edits to
    /// the sentence; a re-save of the same lookup overwrites (the card may have finished streaming meanwhile).
    /// </summary>
    private void SaveContext(string wordId, SavedWord w, bool newLookup)
    {
        var existing = Scalar("SELECT id FROM contexts WHERE word_id = $w AND COALESCE(context, '') = $ctx LIMIT 1",
            ("$w", wordId), ("$ctx", w.Context ?? "")) as string;
        using var cmd = _db.CreateCommand();
        if (existing is null)
        {
            cmd.CommandText = """
                INSERT INTO contexts(id, word_id, created_utc, context, context_offset, surface, context_translation, translation,
                                     usage_note, app_exe, window_title, shot_file, word_box)
                VALUES($cid, $w, $created, $ctx, $off, $surface, $ctxtr, $tr, $usage, $exe, $title, $shot, $box)
                """;
            cmd.Parameters.AddWithValue("$cid", Guid.NewGuid().ToString("N"));
        }
        else
        {
            cmd.CommandText = newLookup
                ? """
                  UPDATE contexts SET created_utc = $created, context_offset = $off, surface = $surface,
                    context_translation = COALESCE(context_translation, $ctxtr), translation = COALESCE(translation, $tr),
                    usage_note = COALESCE(usage_note, $usage), app_exe = $exe, window_title = $title,
                    shot_file = COALESCE($shot, shot_file), word_box = COALESCE($box, word_box)
                  WHERE id = $cid
                  """
                : """
                  UPDATE contexts SET context_offset = $off, surface = $surface, context_translation = $ctxtr, translation = $tr, usage_note = $usage,
                    shot_file = COALESCE($shot, shot_file), word_box = COALESCE($box, word_box)
                  WHERE id = $cid
                  """;
            cmd.Parameters.AddWithValue("$cid", existing);
        }
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$created", Iso(w.CreatedUtc));
        cmd.Parameters.AddWithValue("$ctx", (object?)w.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$off", w.ContextOffset);
        cmd.Parameters.AddWithValue("$surface", w.Word);
        cmd.Parameters.AddWithValue("$ctxtr", (object?)w.ContextTranslation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tr", (object?)w.Translation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$usage", (object?)w.UsageNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exe", (object?)w.AppExe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)w.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shot", (object?)w.ShotFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$box", w.WordBox is { } b ? JsonSerializer.Serialize(b) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Copies the newest sentence into the word row, which exports read.</summary>
    private void MirrorNewest(string wordId) => Run("""
        UPDATE words SET (context, context_offset, context_translation, usage_note, app_exe, window_title, shot_file, word_box) =
          (SELECT context, context_offset, context_translation, usage_note, app_exe, window_title, shot_file, word_box
           FROM contexts WHERE word_id = $w ORDER BY created_utc DESC LIMIT 1)
        WHERE id = $w AND EXISTS (SELECT 1 FROM contexts WHERE word_id = $w)
        """, ("$w", wordId));

    private static void Bind(SqliteCommand cmd, SavedWord w)
    {
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.Parameters.AddWithValue("$created", Iso(w.CreatedUtc));
        cmd.Parameters.AddWithValue("$updated", Iso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$lang", w.Language);
        cmd.Parameters.AddWithValue("$word", w.Word);
        cmd.Parameters.AddWithValue("$dict", (object?)w.DictionaryForm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reading", (object?)w.Reading ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pos", (object?)w.PartOfSpeech ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$level", (object?)w.Level ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$def", (object?)w.Definition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deftr", (object?)w.DefinitionTranslation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tr", (object?)w.Translation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ctx", (object?)w.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$off", w.ContextOffset);
        cmd.Parameters.AddWithValue("$ctxtr", (object?)w.ContextTranslation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expl", (object?)w.Explanation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$syn", JsonSerializer.Serialize(w.Synonyms));
        cmd.Parameters.AddWithValue("$forms", JsonSerializer.Serialize(w.KeyForms));
        cmd.Parameters.AddWithValue("$comp", JsonSerializer.Serialize(w.Components));
        cmd.Parameters.AddWithValue("$exe", (object?)w.AppExe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)w.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shot", (object?)w.ShotFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$box", w.WordBox is { } b ? JsonSerializer.Serialize(b) : DBNull.Value);
        cmd.Parameters.AddWithValue("$register", (object?)w.Register ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$usage", (object?)w.UsageNote ?? DBNull.Value);
    }

    private static SavedWord ReadWord(SqliteDataReader r)
    {
        string? S(string col) => r[col] is DBNull ? null : (string)r[col];
        T J<T>(string col, T fallback) => S(col) is { } s ? JsonSerializer.Deserialize<T>(s) ?? fallback : fallback;

        return new SavedWord
        {
            Id = (string)r["id"],
            CreatedUtc = Date(r["created_utc"]),
            UpdatedUtc = Date(r["updated_utc"]),
            Language = (string)r["language"],
            Word = (string)r["word"],
            DictionaryForm = S("dictionary_form"),
            Reading = S("reading"),
            PartOfSpeech = S("part_of_speech"),
            Level = S("level"),
            Definition = S("definition"),
            DefinitionTranslation = S("definition_tr"),
            Translation = S("translation"),
            Context = S("context"),
            ContextOffset = Convert.ToInt32(r["context_offset"], CultureInfo.InvariantCulture),
            ContextTranslation = S("context_translation"),
            Explanation = S("explanation"),
            Register = S("register"),
            UsageNote = S("usage_note"),
            Synonyms = J<List<string>>("synonyms", []),
            KeyForms = J<List<string>>("key_forms", []),
            Components = J<List<CardComponent>>("components", []),
            AppExe = S("app_exe"),
            WindowTitle = S("window_title"),
            ShotFile = S("shot_file"),
            WordBox = S("word_box") is { } box ? JsonSerializer.Deserialize<PixelRect>(box) : null,
            Lookups = Convert.ToInt32(r["lookups"], CultureInfo.InvariantCulture),
            Pinned = Convert.ToInt32(r["pinned"], CultureInfo.InvariantCulture) != 0,
            PinnedUtc = S("pinned_utc") is { } pinned ? Date(pinned) : null,
        };
    }

    private static WordContext ReadContext(SqliteDataReader r)
    {
        string? S(string col) => r[col] is DBNull ? null : (string)r[col];
        return new WordContext
        {
            Id = (string)r["id"],
            CreatedUtc = Date(r["created_utc"]),
            Context = S("context"),
            ContextOffset = Convert.ToInt32(r["context_offset"], CultureInfo.InvariantCulture),
            Surface = S("surface"),
            ContextTranslation = S("context_translation"),
            Translation = S("translation"),
            UsageNote = S("usage_note"),
            AppExe = S("app_exe"),
            WindowTitle = S("window_title"),
            ShotFile = S("shot_file"),
            WordBox = S("word_box") is { } box ? JsonSerializer.Deserialize<PixelRect>(box) : null,
        };
    }

    private static DateTime Date(object value) =>
        DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Run(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Saves screenshots as JPEG, named by content hash so one frame used by several words is stored once.</summary>
public static class ShotStore
{
    public static string SaveJpeg(string dataRoot, byte[] bgra, int width, int height, int stride, int quality = 85)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bgra))[..24].ToLowerInvariant();
        var now = DateTime.Now;
        var rel = Path.Combine("shots", now.ToString("yyyy", CultureInfo.InvariantCulture),
            now.ToString("MM", CultureInfo.InvariantCulture), hash + ".jpg");
        var full = Path.Combine(dataRoot, rel);
        if (File.Exists(full)) return rel;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var src = new SKBitmap();
            src.InstallPixels(info, handle.AddrOfPinnedObject(), stride);
            src.CopyTo(bitmap);
        }
        finally
        {
            handle.Free();
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        using (var fs = File.Create(full)) data.SaveTo(fs);
        return rel;
    }
}
