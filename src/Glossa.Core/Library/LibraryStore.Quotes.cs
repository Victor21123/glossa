using System.Globalization;
using System.Text.Json;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using Microsoft.Data.Sqlite;

namespace Glossa.Core.Library;

/// <summary>How a quote came: «Реплика», «Зона», a paragraph of «Весь экран», or a live subtitle saved by its key.</summary>
public static class QuoteSource
{
    public const string Line = "line";
    public const string Zone = "zone";
    public const string Screen = "screen";
    public const string Live = "live";
}

/// <summary>
/// A line translated in «Только перевод», kept whole with its translation, game and (when wanted) the frame, decided
/// 2026-09-30: they are what that mode leaves behind, as words are the dictionary's. Quotes are not studied.
/// </summary>
public sealed record Quote
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>When the line was last translated; a line met again is the same quote, seen once more.</summary>
    public DateTime SeenUtc { get; init; } = DateTime.UtcNow;

    public int Seen { get; init; } = 1;
    public required string Language { get; init; }
    public required string Text { get; init; }
    public string? Translation { get; init; }
    public string? AppExe { get; init; }
    public string? WindowTitle { get; init; }

    /// <summary>The frame, relative to the data folder (under quotes\, downscaled); null without one.</summary>
    public string? ShotFile { get; init; }

    /// <summary>Where the text is in the stored frame, in its pixels.</summary>
    public PixelRect? Box { get; init; }

    public string Source { get; init; } = QuoteSource.Line;
}

/// <summary>
/// A quote's frame, encoded beforehand (<see cref="ShotStore.EncodeQuoteFrame"/>, outside the store's lock); the store
/// writes it only if the quote takes it. <paramref name="Box"/> is the text in its pixels.
/// </summary>
public sealed record QuoteFrame(string File, byte[] Jpeg, PixelRect? Box);

public sealed partial class LibraryStore
{
    /// <summary>
    /// The data folder, which frames are relative to: the one library.db is in. Quote frames are written and deleted
    /// by the store, under its lock, so a frame a new quote takes can never be deleted as unused a moment later.
    /// </summary>
    private string DataRoot => Path.GetDirectoryName(Path.GetFullPath(_path))!;

    /// <summary>Whether this line of this game is a quote with a frame already (then no frame need be encoded for it).</summary>
    public bool QuoteHasFrame(string text, string? appExe)
    {
        lock (_gate)
            return Scalar("SELECT 1 FROM quotes WHERE text_key = $k AND app_exe IS $app AND shot_file IS NOT NULL LIMIT 1",
                ("$k", TextBlocks.Key(text)), ("$app", appExe)) is not null;
    }

    /// <summary>
    /// Keeps a translated line. The same line in the same game (spaces, punctuation and case aside) is the same quote:
    /// it is counted and dated again, and keeps its first translation and frame; <paramref name="frame"/> is written only
    /// for a quote without one. A frame written for a quote that then could not be saved goes again. Returns its id.
    /// </summary>
    public string RecordQuote(Quote quote, QuoteFrame? frame = null)
    {
        lock (_gate)
        {
            var key = TextBlocks.Key(quote.Text);
            string? id = null;
            string? file = null;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, shot_file FROM quotes WHERE text_key = $k AND app_exe IS $app LIMIT 1";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$app", (object?)quote.AppExe ?? DBNull.Value);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    id = r.GetString(0);
                    file = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            var taken = file is null ? frame : null;
            var created = taken is not null && WriteFrame(taken);
            try
            {
                using var tx = _db.BeginTransaction();
                var box = taken?.Box is { } b ? JsonSerializer.Serialize(b) : null;
                if (id is null)
                {
                    id = quote.Id;
                    Run("""
                        INSERT INTO quotes(id, created_utc, seen_utc, seen, language, text, text_key, translation, app_exe, window_title,
                                           shot_file, box, source)
                        VALUES($id, $created, $seen, 1, $lang, $text, $k, $tr, $app, $title, $shot, $box, $source)
                        """, ("$id", id), ("$created", Iso(quote.CreatedUtc)), ("$seen", Iso(quote.SeenUtc)), ("$lang", quote.Language),
                        ("$text", quote.Text), ("$k", key), ("$tr", quote.Translation), ("$app", quote.AppExe), ("$title", quote.WindowTitle),
                        ("$shot", taken?.File), ("$box", box), ("$source", quote.Source));
                }
                else
                {
                    Run("""
                        UPDATE quotes SET seen = seen + 1, seen_utc = $seen, translation = COALESCE(translation, $tr),
                          window_title = COALESCE($title, window_title),
                          shot_file = COALESCE(shot_file, $shot), box = CASE WHEN shot_file IS NULL THEN $box ELSE box END
                        WHERE id = $id
                        """, ("$seen", Iso(quote.SeenUtc)), ("$tr", quote.Translation), ("$title", quote.WindowTitle),
                        ("$shot", taken?.File), ("$box", box), ("$id", id));
                }
                tx.Commit();
                return id;
            }
            catch
            {
                if (created) DeleteFrame(taken!.File);
                throw;
            }
        }
    }

    /// <summary>The quotes, most recently seen first.</summary>
    public IReadOnlyList<Quote> ListQuotes()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM quotes ORDER BY seen_utc DESC";
            using var r = cmd.ExecuteReader();
            var list = new List<Quote>();
            while (r.Read()) list.Add(ReadQuote(r));
            return list;
        }
    }

    /// <summary>Removes quotes, and the frames no other quote shows. Returns how many quotes went.</summary>
    public int DeleteQuotes(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var files = new List<string>();
            var deleted = 0;
            using (var tx = _db.BeginTransaction())
            {
                foreach (var id in ids)
                {
                    if (Scalar("SELECT shot_file FROM quotes WHERE id = $id", ("$id", id)) is string file) files.Add(file);
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = "DELETE FROM quotes WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    deleted += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            foreach (var file in Unused(files)) DeleteFrame(file);
            return deleted;
        }
    }

    /// <summary>
    /// Forgets the frames of these quotes (the quotes stay) and deletes the files no quote shows any more (a frame of
    /// «Весь экран» may be shared by several of its paragraphs). Returns how many files went.
    /// </summary>
    public int ClearQuoteShots(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var files = new List<string>();
            using (var tx = _db.BeginTransaction())
            {
                foreach (var id in ids)
                {
                    if (Scalar("SELECT shot_file FROM quotes WHERE id = $id", ("$id", id)) is not string file) continue;
                    files.Add(file);
                    Run("UPDATE quotes SET shot_file = NULL, box = NULL WHERE id = $id", ("$id", id));
                }
                tx.Commit();
            }
            var unused = Unused(files);
            foreach (var file in unused) DeleteFrame(file);
            return unused.Count;
        }
    }

    /// <summary>
    /// «Очистить все»: every quote forgets its frame and the quotes folder is emptied, leftovers of earlier runs included.
    /// Under the lock: a quote being kept this very moment either comes before (and loses its frame too) or after.
    /// Returns how many files went.
    /// </summary>
    public int ClearAllQuoteShots()
    {
        lock (_gate)
        {
            Exec("UPDATE quotes SET shot_file = NULL, box = NULL WHERE shot_file IS NOT NULL");
            var folder = Path.Combine(DataRoot, ShotStore.QuotesFolder);
            if (!Directory.Exists(folder)) return 0;
            var deleted = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
                if (TryDelete(path)) deleted++;
            return deleted;
        }
    }

    /// <summary>The frames among <paramref name="files"/> that no quote refers to.</summary>
    private List<string> Unused(IEnumerable<string> files) =>
        files.Distinct().Where(f => Scalar("SELECT 1 FROM quotes WHERE shot_file = $f LIMIT 1", ("$f", f)) is null).ToList();

    /// <summary>
    /// Writes the frame unless its file is there; true when it wrote it. A file already there (the same frame, another
    /// paragraph of it) is touched, so it counts as new for anything that goes by file times.
    /// </summary>
    private bool WriteFrame(QuoteFrame frame)
    {
        var path = Path.Combine(DataRoot, frame.File);
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, frame.Jpeg);
        return true;
    }

    private void DeleteFrame(string file) => TryDelete(Path.Combine(DataRoot, file));

    /// <summary>A frame locked by a viewer or gone already stays for the next «Очистить все».</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Quote ReadQuote(SqliteDataReader r)
    {
        string? S(string col) => r[col] is DBNull ? null : (string)r[col];
        return new Quote
        {
            Id = (string)r["id"],
            CreatedUtc = Date(r["created_utc"]),
            SeenUtc = Date(r["seen_utc"]),
            Seen = Convert.ToInt32(r["seen"], CultureInfo.InvariantCulture),
            Language = (string)r["language"],
            Text = (string)r["text"],
            Translation = S("translation"),
            AppExe = S("app_exe"),
            WindowTitle = S("window_title"),
            ShotFile = S("shot_file"),
            Box = S("box") is { } box ? JsonSerializer.Deserialize<PixelRect>(box) : null,
            Source = (string)r["source"],
        };
    }
}
