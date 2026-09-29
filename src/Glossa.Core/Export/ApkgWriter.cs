using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glossa.Core.Library;
using Microsoft.Data.Sqlite;

namespace Glossa.Core.Export;

public sealed record AnkiExportOptions(bool ReverseCards = false, bool IncludeImages = true, Func<SavedWord, byte[]?>? Audio = null);

/// <summary>
/// Writes an Anki package (.apkg): a legacy collection.anki2 SQLite database plus media, the format every
/// Anki version imports. Layout follows genanki. Note GUIDs are the permanent word ids, so importing again
/// updates the existing notes instead of duplicating them.
/// </summary>
public static class ApkgWriter
{
    public static int Write(string apkgPath, string dataRoot, IReadOnlyList<SavedWord> words, AnkiExportOptions options,
        IProgress<string>? progress = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "glossa-apkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dbPath = Path.Combine(work, "collection.anki2");
            var media = new List<(string Name, byte[] Data)>();
            var now = DateTimeOffset.UtcNow;
            var ts = now.ToUnixTimeSeconds();
            long nextId = now.ToUnixTimeMilliseconds();

            using (var db = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                db.Open();
                Exec(db, Schema);
                var decks = words.Select(w => AnkiNoteType.DeckFor(w.Language)).Distinct().ToList();
                Exec(db, "INSERT INTO col VALUES(null,$crt,$mod,$mod,11,0,0,0,$conf,$models,$decks,$dconf,'{}')",
                    ("$crt", ts - ts % 86400), ("$mod", now.ToUnixTimeMilliseconds()),
                    ("$conf", ColConf), ("$models", ModelsJson(ts, AnkiNoteType.DeckId(decks.FirstOrDefault() ?? AnkiNoteType.RootDeck))),
                    ("$decks", DecksJson(decks)), ("$dconf", DeckConf));

                using var tx = db.BeginTransaction();
                var i = 0;
                foreach (var w in words)
                {
                    progress?.Report($"Карточки: {++i} / {words.Count}");
                    var image = options.IncludeImages ? AnkiNoteType.CardImage(dataRoot, w) : null;
                    var audio = options.Audio?.Invoke(w);
                    if (image is not null) media.Add((AnkiNoteType.ImageFileName(w), image));
                    if (audio is not null) media.Add((AnkiNoteType.AudioFileName(w), audio));

                    var fields = AnkiNoteType.FieldValues(w, image is not null, audio is not null, options.ReverseCards);
                    var noteId = nextId++;
                    Exec(db, "INSERT INTO notes VALUES($id,$guid,$mid,$mod,-1,$tags,$flds,$sfld,0,0,'')",
                        ("$id", noteId), ("$guid", w.Id), ("$mid", AnkiNoteType.ModelId), ("$mod", ts),
                        ("$tags", $" glossa {w.Language} "), ("$flds", string.Join('\x1f', fields)), ("$sfld", w.Headword));

                    var did = AnkiNoteType.DeckId(AnkiNoteType.DeckFor(w.Language));
                    var ords = options.ReverseCards ? new[] { 0, 1 } : [0];
                    foreach (var ord in ords)
                        Exec(db, "INSERT INTO cards VALUES($id,$nid,$did,$ord,$mod,-1,0,0,$due,0,0,0,0,0,0,0,0,'')",
                            ("$id", nextId++), ("$nid", noteId), ("$did", did), ("$ord", ord), ("$mod", ts), ("$due", i));
                }
                tx.Commit();
            }

            progress?.Report("Упаковка...");
            if (File.Exists(apkgPath)) File.Delete(apkgPath);
            using (var zip = ZipFile.Open(apkgPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbPath, "collection.anki2");
                var map = new JsonObject();
                for (var m = 0; m < media.Count; m++)
                {
                    map[m.ToString()] = media[m].Name;
                    var entry = zip.CreateEntry(m.ToString(), CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(media[m].Data);
                }
                var mediaEntry = zip.CreateEntry("media");
                using var ms = new StreamWriter(mediaEntry.Open());
                ms.Write(map.ToJsonString());
            }
            return words.Count;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static string ModelsJson(long ts, long deckId)
    {
        var flds = new JsonArray();
        for (var i = 0; i < AnkiNoteType.Fields.Length; i++)
            flds.Add(new JsonObject
            {
                ["name"] = AnkiNoteType.Fields[i], ["ord"] = i, ["font"] = "Segoe UI", ["media"] = new JsonArray(),
                ["rtl"] = false, ["size"] = 20, ["sticky"] = false,
            });
        var tmpls = new JsonArray();
        for (var i = 0; i < AnkiNoteType.Templates.Length; i++)
        {
            var t = AnkiNoteType.Templates[i];
            tmpls.Add(new JsonObject
            {
                ["name"] = t.Name, ["ord"] = i, ["qfmt"] = t.Front, ["afmt"] = t.Back,
                ["bafmt"] = "", ["bqfmt"] = "", ["bfont"] = "", ["bsize"] = 0, ["did"] = null,
            });
        }
        var headword = Array.IndexOf(AnkiNoteType.Fields, "Headword");
        var reverse = Array.IndexOf(AnkiNoteType.Fields, "Reverse");
        var model = new JsonObject
        {
            ["css"] = AnkiNoteType.Css, ["did"] = deckId, ["flds"] = flds, ["id"] = AnkiNoteType.ModelId.ToString(),
            ["latexPost"] = "\\end{document}", ["latexPre"] = LatexPre, ["latexsvg"] = false, ["mod"] = ts,
            ["name"] = AnkiNoteType.Name,
            ["req"] = new JsonArray(
                new JsonArray(0, "any", new JsonArray(headword)),
                new JsonArray(1, "all", new JsonArray(reverse))),
            ["sortf"] = headword, ["tags"] = new JsonArray(), ["tmpls"] = tmpls, ["type"] = 0, ["usn"] = -1,
            ["vers"] = new JsonArray(),
        };
        return new JsonObject { [AnkiNoteType.ModelId.ToString()] = model }.ToJsonString();
    }

    private static string DecksJson(IEnumerable<string> names)
    {
        var decks = new JsonObject { ["1"] = Deck(1, "Default") };
        foreach (var n in names.Prepend(AnkiNoteType.RootDeck).Distinct())
            decks[AnkiNoteType.DeckId(n).ToString()] = Deck(AnkiNoteType.DeckId(n), n);
        return decks.ToJsonString();

        static JsonObject Deck(long id, string name) => new()
        {
            ["collapsed"] = false, ["conf"] = 1, ["desc"] = "", ["dyn"] = 0, ["extendNew"] = 0, ["extendRev"] = 50,
            ["id"] = id, ["lrnToday"] = new JsonArray(0, 0), ["mod"] = 1727470000, ["name"] = name,
            ["newToday"] = new JsonArray(0, 0), ["revToday"] = new JsonArray(0, 0), ["timeToday"] = new JsonArray(0, 0),
            ["usn"] = -1,
        };
    }

    private static void Exec(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private const string LatexPre =
        "\\documentclass[12pt]{article}\n\\special{papersize=3in,5in}\n\\usepackage[utf8]{inputenc}\n\\usepackage{amssymb,amsmath}\n\\pagestyle{empty}\n\\setlength{\\parindent}{0in}\n\\begin{document}\n";

    private static readonly string ColConf = JsonSerializer.Serialize(new
    {
        activeDecks = new[] { 1 }, addToCur = true, collapseTime = 1200, curDeck = 1,
        curModel = AnkiNoteType.ModelId.ToString(), dueCounts = true, estTimes = true, newBury = true, newSpread = 0,
        nextPos = 1, sortBackwards = false, sortType = "noteFld", timeLim = 0,
    });

    private const string DeckConf = """
        {"1":{"autoplay":true,"id":1,"lapse":{"delays":[10],"leechAction":0,"leechFails":8,"minInt":1,"mult":0},
        "maxTaken":60,"mod":0,"name":"Default","new":{"bury":true,"delays":[1,10],"initialFactor":2500,"ints":[1,4,7],
        "order":1,"perDay":20,"separate":true},"replayq":true,"rev":{"bury":true,"ease4":1.3,"fuzz":0.05,"ivlFct":1,
        "maxIvl":36500,"minSpace":1,"perDay":100},"timer":0,"usn":0}}
        """;

    private const string Schema = """
        CREATE TABLE col (id integer primary key, crt integer not null, mod integer not null, scm integer not null,
          ver integer not null, dty integer not null, usn integer not null, ls integer not null, conf text not null,
          models text not null, decks text not null, dconf text not null, tags text not null);
        CREATE TABLE notes (id integer primary key, guid text not null, mid integer not null, mod integer not null,
          usn integer not null, tags text not null, flds text not null, sfld integer not null, csum integer not null,
          flags integer not null, data text not null);
        CREATE TABLE cards (id integer primary key, nid integer not null, did integer not null, ord integer not null,
          mod integer not null, usn integer not null, type integer not null, queue integer not null, due integer not null,
          ivl integer not null, factor integer not null, reps integer not null, lapses integer not null,
          left integer not null, odue integer not null, odid integer not null, flags integer not null, data text not null);
        CREATE TABLE revlog (id integer primary key, cid integer not null, usn integer not null, ease integer not null,
          ivl integer not null, lastIvl integer not null, factor integer not null, time integer not null, type integer not null);
        CREATE TABLE graves (usn integer not null, oid integer not null, type integer not null);
        CREATE INDEX ix_notes_usn on notes (usn);
        CREATE INDEX ix_cards_usn on cards (usn);
        CREATE INDEX ix_revlog_usn on revlog (usn);
        CREATE INDEX ix_cards_nid on cards (nid);
        CREATE INDEX ix_cards_sched on cards (did, queue, due);
        CREATE INDEX ix_revlog_cid on revlog (cid);
        CREATE INDEX ix_notes_csum on notes (csum);
        """;
}
