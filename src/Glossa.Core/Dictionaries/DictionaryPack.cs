using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// Lookup keys: NFKC, lower case, curly apostrophes folded, katakana folded to hiragana, ё to е, stress marks
/// dropped. The same for every language, so a dictionary can be indexed before its language is known.
/// </summary>
public static class DictKeys
{
    public static string Normalize(string text, string language = "")
    {
        var s = text.Normalize(NormalizationForm.FormKC).Trim();
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var raw in s)
        {
            var c = raw;
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (c == '́') continue; // combining acute: stress mark in Russian dictionaries
            if (space) { sb.Append(' '); space = false; }
            c = char.ToLowerInvariant(c);
            if (c is '’' or '‘' or 'ʼ') c = '\'';
            else if (c is >= 'ァ' and <= 'ヶ') c = (char)(c - 0x60);
            else if (c == 'ё') c = 'е';
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <param name="Rank">0 - the headword, 1 (<see cref="Alias"/>) - a reading, synonym or link, 2 - an inflected form
/// (Wiktionary's conjugation tables), 3 - a dated-only article.</param>
public readonly record struct DictKey(string Text, int Rank = 0)
{
    public const int Alias = 1;
}

public sealed record DictEntry(long Id, string Headword, string? Reading, string Body, int Rank);

/// <summary>What a pack is, from its meta table.</summary>
public sealed record PackInfo(
    string Id,
    string Title,
    string SourceLanguage,
    string TargetLanguage,
    long Entries,
    int Priority,
    string? Description,
    string? License,
    string? Origin,
    string Path)
{
    public long SizeBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;
}

/// <summary>
/// A dictionary converted to Glossa's own format: one SQLite file with the entries (markup bodies, see
/// <see cref="DictMarkup"/>) and a key table that maps every searchable form to its entry.
/// </summary>
public sealed class DictionaryPack : IDisposable
{
    public const string Extension = ".gdict";
    public const int FormatVersion = 1;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly SqliteCommand _find;
    private readonly SqliteCommand _has;

    public DictionaryPack(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        _find = _db.CreateCommand();
        _has = _db.CreateCommand();
        try
        {
            // A foreign or damaged file fails here; the connection must not outlive the failed constructor.
            _db.Open();
            Info = ReadInfo(_db, path);

            _find.CommandText = """
                SELECT e.id, e.headword, e.reading, e.body, k.rank
                FROM keys k JOIN entries e ON e.id = k.entry
                WHERE k.key = $key ORDER BY k.rank, e.id LIMIT $limit
                """;
            _find.Parameters.Add("$key", SqliteType.Text);
            _find.Parameters.Add("$limit", SqliteType.Integer);
            _find.Prepare();

            _has.CommandText = "SELECT 1 FROM keys WHERE key = $key AND rank <= $rank LIMIT 1";
            _has.Parameters.Add("$key", SqliteType.Text);
            _has.Parameters.Add("$rank", SqliteType.Integer);
            _has.Prepare();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public PackInfo Info { get; private set; }

    /// <summary>Entries for an already normalized key, best rank first.</summary>
    public IReadOnlyList<DictEntry> Find(string key, int limit = 8)
    {
        var list = new List<DictEntry>();
        lock (_gate)
        {
            _find.Parameters["$key"].Value = key;
            _find.Parameters["$limit"].Value = limit;
            using var r = _find.ExecuteReader();
            while (r.Read())
                list.Add(new DictEntry(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt32(4)));
        }
        return list;
    }

    /// <param name="maxRank">Only keys up to this rank: <see cref="DictKey.Alias"/> leaves out inflected forms.</param>
    public bool HasKey(string key, int maxRank = int.MaxValue)
    {
        lock (_gate)
        {
            _has.Parameters["$key"].Value = key;
            _has.Parameters["$rank"].Value = maxRank;
            return _has.ExecuteScalar() is not null;
        }
    }

    public static PackInfo ReadInfo(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        return ReadInfo(db, path);
    }

    private static PackInfo ReadInfo(SqliteConnection db, string path)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM meta";
            using var r = cmd.ExecuteReader();
            while (r.Read()) meta[r.GetString(0)] = r.GetString(1);
        }
        string Get(string k, string fallback = "") => meta.TryGetValue(k, out var v) ? v : fallback;
        return new PackInfo(
            Get("id", System.IO.Path.GetFileNameWithoutExtension(path)),
            Get("title", System.IO.Path.GetFileNameWithoutExtension(path)),
            Get("source_language", "?"),
            Get("target_language", "?"),
            long.TryParse(Get("entries"), out var n) ? n : 0,
            int.TryParse(Get("priority"), out var p) ? p : 50,
            meta.GetValueOrDefault("description"),
            meta.GetValueOrDefault("license"),
            meta.GetValueOrDefault("origin"),
            path);
    }

    /// <summary>Changes one meta value (the source language of an imported dictionary, for instance).</summary>
    public static void SetMeta(string path, string key, string value)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _find.Dispose();
        _has.Dispose();
        _db.Dispose();
    }
}

/// <summary>Description of a pack being built.</summary>
public sealed record PackMeta(
    string Id,
    string Title,
    string SourceLanguage,
    string TargetLanguage,
    int Priority = 50,
    string? Description = null,
    string? License = null,
    string? Origin = null);

/// <summary>
/// Builds a pack into a temporary file and moves it into place on <see cref="Complete"/>, so a failed or
/// cancelled build never leaves a half-written dictionary behind.
/// </summary>
public sealed class DictionaryPackWriter : IDisposable
{
    private readonly string _path;
    private readonly string _tmp;
    private readonly SqliteConnection _db;
    private readonly SqliteTransaction _tx;
    private readonly SqliteCommand _entry;
    private readonly SqliteCommand _key;
    private readonly SqliteCommand _link;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _completed;

    /// <summary>Scripts seen so far, for guessing the languages of a source that does not declare them.</summary>
    public LanguageStats Stats { get; } = new();

    public DictionaryPackWriter(string path, PackMeta meta)
    {
        Meta = meta;
        _path = path;
        _tmp = path + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (File.Exists(_tmp)) File.Delete(_tmp);

        _db = new SqliteConnection($"Data Source={_tmp};Pooling=False");
        _link = _db.CreateCommand();
        _entry = _db.CreateCommand();
        _key = _db.CreateCommand();
        try
        {
            _db.Open();
            // Sorts and temporary tables stay in memory: SQLite's temp files would otherwise land on C:.
            Exec("PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA cache_size = -200000; PRAGMA locking_mode = EXCLUSIVE; PRAGMA temp_store = MEMORY;");
            Exec("""
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE entries(id INTEGER PRIMARY KEY, headword TEXT NOT NULL, reading TEXT, body TEXT NOT NULL);
                CREATE TABLE keys_build(key TEXT NOT NULL, entry INTEGER NOT NULL, rank INTEGER NOT NULL);
                CREATE TABLE links(alias TEXT NOT NULL, target TEXT NOT NULL);
                """);
            _tx = _db.BeginTransaction();

            _link.Transaction = _tx;
            _link.CommandText = "INSERT INTO links(alias, target) VALUES ($a, $t)";
            _link.Parameters.Add("$a", SqliteType.Text);
            _link.Parameters.Add("$t", SqliteType.Text);

            _entry.Transaction = _tx;
            _entry.CommandText = "INSERT INTO entries(headword, reading, body) VALUES ($h, $r, $b); SELECT last_insert_rowid();";
            _entry.Parameters.Add("$h", SqliteType.Text);
            _entry.Parameters.Add("$r", SqliteType.Text);
            _entry.Parameters.Add("$b", SqliteType.Text);

            _key.Transaction = _tx;
            _key.CommandText = "INSERT INTO keys_build(key, entry, rank) VALUES ($k, $e, $n)";
            _key.Parameters.Add("$k", SqliteType.Text);
            _key.Parameters.Add("$e", SqliteType.Integer);
            _key.Parameters.Add("$n", SqliteType.Integer);
        }
        catch
        {
            // Disk full or a locked folder: close the file and remove it, the caller never gets a writer to dispose.
            _link.Dispose();
            _entry.Dispose();
            _key.Dispose();
            _db.Dispose();
            try { File.Delete(_tmp); } catch (IOException) { }
            throw;
        }
    }

    public PackMeta Meta { get; }

    public long Count { get; private set; }

    /// <summary>Adds an entry; keys are normalized here, and a key repeated for the same entry keeps its best rank.</summary>
    public long Add(string headword, string? reading, string body, IEnumerable<DictKey> keys)
    {
        _entry.Parameters["$h"].Value = headword;
        _entry.Parameters["$r"].Value = (object?)reading ?? DBNull.Value;
        _entry.Parameters["$b"].Value = body;
        var id = (long)_entry.ExecuteScalar()!;
        Count++;
        Stats.Add(headword, body);

        _seen.Clear();
        foreach (var k in keys.OrderBy(k => k.Rank))
        {
            var norm = DictKeys.Normalize(k.Text, Meta.SourceLanguage);
            if (norm.Length == 0 || !_seen.Add(norm)) continue;
            AddKey(norm, id, k.Rank);
        }
        return id;
    }

    /// <summary>Points one more key at an existing entry (StarDict synonyms, MDX links, English word forms).</summary>
    public void AddAlias(string key, long entry, int rank)
    {
        var norm = DictKeys.Normalize(key, Meta.SourceLanguage);
        if (norm.Length > 0) AddKey(norm, entry, rank);
    }

    /// <summary>
    /// A headword that only points at another headword (MDX "@@@LINK="). Resolved in <see cref="Complete"/>,
    /// once every target is known.
    /// </summary>
    public void AddLink(string alias, string target)
    {
        _link.Parameters["$a"].Value = DictKeys.Normalize(alias, Meta.SourceLanguage);
        _link.Parameters["$t"].Value = DictKeys.Normalize(target, Meta.SourceLanguage);
        _link.ExecuteNonQuery();
    }

    private void AddKey(string norm, long entry, int rank)
    {
        _key.Parameters["$k"].Value = norm;
        _key.Parameters["$e"].Value = entry;
        _key.Parameters["$n"].Value = rank;
        _key.ExecuteNonQuery();
    }

    /// <param name="sourceLanguage">Overrides the declared language (after guessing it from <see cref="Stats"/>).</param>
    public PackInfo Complete(string? sourceLanguage = null, string? targetLanguage = null)
    {
        var meta = new Dictionary<string, string?>
        {
            ["id"] = Meta.Id,
            ["title"] = Meta.Title,
            ["source_language"] = sourceLanguage ?? Meta.SourceLanguage,
            ["target_language"] = targetLanguage ?? Meta.TargetLanguage,
            ["priority"] = Meta.Priority.ToString(CultureInfo.InvariantCulture),
            ["description"] = Meta.Description,
            ["license"] = Meta.License,
            ["origin"] = Meta.Origin,
            ["entries"] = Count.ToString(CultureInfo.InvariantCulture),
            ["format"] = DictionaryPack.FormatVersion.ToString(CultureInfo.InvariantCulture),
            ["built"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = _tx;
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
            var k = cmd.Parameters.Add("$k", SqliteType.Text);
            var v = cmd.Parameters.Add("$v", SqliteType.Text);
            foreach (var (key, value) in meta)
            {
                if (value is null) continue;
                k.Value = key;
                v.Value = value;
                cmd.ExecuteNonQuery();
            }
        }
        _tx.Commit();
        _tx.Dispose();
        // Keys as a clustered table (no separate index: a third smaller for БКРС), filled in key order,
        // then links resolved against it, then the file rewritten compactly.
        Exec("""
            CREATE TABLE keys(key TEXT NOT NULL, rank INTEGER NOT NULL, entry INTEGER NOT NULL, PRIMARY KEY(key, rank, entry)) WITHOUT ROWID;
            INSERT OR IGNORE INTO keys SELECT key, rank, entry FROM keys_build ORDER BY key, rank, entry;
            INSERT OR IGNORE INTO keys SELECT l.alias, 1, k.entry FROM links l JOIN keys k ON k.key = l.target AND k.rank = 0;
            DROP TABLE keys_build;
            DROP TABLE links;
            ANALYZE;
            """);
        var compact = _path + ".compact";
        if (File.Exists(compact)) File.Delete(compact);
        using (var vacuum = _db.CreateCommand())
        {
            vacuum.CommandText = "VACUUM INTO $path";
            vacuum.Parameters.AddWithValue("$path", compact);
            vacuum.ExecuteNonQuery();
        }
        _entry.Dispose();
        _key.Dispose();
        _link.Dispose();
        _db.Close();
        _db.Dispose(); // unpooled: the file is released here

        File.Move(compact, _path, overwrite: true);
        File.Delete(_tmp);
        _completed = true;
        return DictionaryPack.ReadInfo(_path);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_completed) return;
        try
        {
            _tx.Dispose();
            _entry.Dispose();
            _key.Dispose();
            _link.Dispose();
            _db.Dispose();
            if (File.Exists(_tmp)) File.Delete(_tmp);
            if (File.Exists(_path + ".compact")) File.Delete(_path + ".compact");
        }
        catch (IOException)
        {
            // Best effort: a stale .tmp is overwritten by the next build.
        }
    }
}
