using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// Word levels from published lists: JLPT N5–N1 (Jonathan Waller's lists via open-anki-jlpt-decks), HSK 3.0
/// levels 1–9 (complete-hsk-vocabulary) and CEFR A1–C2 (CEFR-J and Octanove profiles). A listed level replaces
/// the AI's guess.
/// </summary>
public sealed class LevelService : IDisposable
{
    private SqliteConnection? _db;
    private SqliteCommand? _find;
    private readonly object _gate = new();

    public LevelService(string path)
    {
        if (!File.Exists(path)) return;
        LevelsBuilder.Upgrade(path);
        _db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        _db.Open();
        _find = _db.CreateCommand();
        _find.CommandText = "SELECT level, reading FROM levels WHERE lang = $l AND term = $t ORDER BY rank";
        _find.Parameters.Add("$l", SqliteType.Text);
        _find.Parameters.Add("$t", SqliteType.Text);
        _find.Prepare();
    }

    public bool Available => _db is not null;

    /// <summary>
    /// The easiest listed level of the word; for Japanese the row with the same reading wins. Returns null once
    /// disposed: the app swaps in a new instance on reload while a lookup may still hold this one.
    /// </summary>
    public string? LevelOf(string language, string term, string? reading = null)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var rows = new List<(string Level, string? Reading)>();
        lock (_gate)
        {
            if (_find is null) return null;
            _find.Parameters["$l"].Value = language;
            _find.Parameters["$t"].Value = DictKeys.Normalize(term);
            using var r = _find.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        if (rows.Count == 0) return null;
        if (reading is not null)
        {
            var norm = DictKeys.Normalize(reading);
            foreach (var row in rows)
                if (row.Reading == norm) return row.Level;
        }
        return rows[0].Level;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _find?.Dispose();
            _db?.Dispose();
            _find = null;
            _db = null;
        }
    }
}

public static class LevelsBuilder
{
    public static readonly string[] JlptFiles = ["jlpt_n5.csv", "jlpt_n4.csv", "jlpt_n3.csv", "jlpt_n2.csv", "jlpt_n1.csv"];
    public const string HskFile = "hsk_complete.min.json";
    public const string CefrFile = "cefrj-vocabulary-profile-1.5.csv";
    public const string CefrC1C2File = "octanove-vocabulary-profile-c1c2-1.0.csv";

    public static void Build(string sourcesDir, string path, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var db = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            db.Open();
            using (var create = db.CreateCommand())
            {
                create.CommandText = "CREATE TABLE levels(lang TEXT NOT NULL, term TEXT NOT NULL, reading TEXT, level TEXT NOT NULL, rank INTEGER NOT NULL);";
                create.ExecuteNonQuery();
            }
            using var tx = db.BeginTransaction();
            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO levels(lang, term, reading, level, rank) VALUES ($l, $t, $r, $v, $n)";
            var pl = insert.Parameters.Add("$l", SqliteType.Text);
            var pt = insert.Parameters.Add("$t", SqliteType.Text);
            var pr = insert.Parameters.Add("$r", SqliteType.Text);
            var pv = insert.Parameters.Add("$v", SqliteType.Text);
            var pn = insert.Parameters.Add("$n", SqliteType.Integer);

            void Add(string lang, string term, string? reading, string level, int rank)
            {
                var t = DictKeys.Normalize(term);
                if (t.Length == 0) return;
                pl.Value = lang;
                pt.Value = t;
                pr.Value = reading is null ? DBNull.Value : DictKeys.Normalize(reading);
                pv.Value = level;
                pn.Value = rank;
                insert.ExecuteNonQuery();
            }

            // JLPT: expression,reading,meaning,tags,guid
            for (var i = 0; i < JlptFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var level = $"JLPT N{5 - i}";
                foreach (var row in Csv(Path.Combine(sourcesDir, JlptFiles[i])).Skip(1))
                {
                    if (row.Count < 2) continue;
                    Add("ja", row[0], row[1], level, i + 1);
                    if (row[1] != row[0]) Add("ja", row[1], row[1], level, i + 1 + 10); // kana spelling, weaker
                }
            }

            // HSK 3.0: {"s": simplified, "l": ["n1", "o2", ...], "f": [{"t": traditional, ...}]}
            ct.ThrowIfCancellationRequested();
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourcesDir, HskFile))))
            {
                foreach (var w in doc.RootElement.EnumerateArray())
                {
                    var s = w.GetProperty("s").GetString() ?? "";
                    var levels = w.GetProperty("l").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    var newest = levels.Where(l => l.StartsWith('n')).Select(l => int.TryParse(l[1..], out var n) ? n : 99).DefaultIfEmpty(99).Min();
                    var old = levels.Where(l => l.StartsWith('o')).Select(l => int.TryParse(l[1..], out var n) ? n : 99).DefaultIfEmpty(99).Min();
                    string label;
                    int rank;
                    if (newest < 99) { label = newest >= 7 ? "HSK 7-9" : $"HSK {newest}"; rank = newest; }
                    else if (old < 99) { label = $"HSK {old}"; rank = old + 10; }
                    else continue;
                    Add("zh", s, null, label, rank);
                    if (w.TryGetProperty("f", out var forms))
                        foreach (var f in forms.EnumerateArray())
                            if (f.TryGetProperty("t", out var t) && t.GetString() is { } trad && trad != s) Add("zh", trad, null, label, rank);
                }
            }

            // CEFR-J (A1–B2) and Octanove (C1–C2): headword,pos,CEFR,...; "a.m./A.M./am/AM" lists spellings.
            foreach (var file in new[] { CefrFile, CefrC1C2File })
            {
                ct.ThrowIfCancellationRequested();
                foreach (var row in Csv(Path.Combine(sourcesDir, file)).Skip(1))
                {
                    if (row.Count < 3) continue;
                    var cefr = row[2].Trim().ToUpperInvariant();
                    var rank = cefr switch { "A1" => 1, "A2" => 2, "B1" => 3, "B2" => 4, "C1" => 5, "C2" => 6, _ => 0 };
                    if (rank == 0) continue;
                    foreach (var spelling in row[0].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        Add("en", spelling, null, cefr, rank);
                }
            }
            tx.Commit();
            using var index = db.CreateCommand();
            index.CommandText = $"CREATE INDEX levels_term ON levels(lang, term, rank); ANALYZE; PRAGMA user_version = {Version};";
            index.ExecuteNonQuery();
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>1: "HSK 7-9" with a hyphen (the first builds wrote an en dash, char 8211; 2026-09-29).</summary>
    public const int Version = 1;

    /// <summary>Brings a levels.db built by an older Glossa up to <see cref="Version"/> without the sources.</summary>
    public static void Upgrade(string path)
    {
        if (!File.Exists(path)) return;
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(cmd.ExecuteScalar()) >= Version) return;
            cmd.CommandText = $"UPDATE levels SET level = 'HSK 7-9' WHERE level = 'HSK 7' || char(8211) || '9'; PRAGMA user_version = {Version};";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields with commas and doubled quotes.</summary>
    internal static IEnumerable<List<string>> Csv(string path)
    {
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            yield return fields;
        }
    }
}
