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

public sealed partial class LibraryStore
{
    /// <summary>
    /// Keeps a translated line. The same line in the same game (spaces, punctuation and case aside) is the same quote:
    /// it is counted and dated again, and keeps its first translation and frame. <paramref name="shot"/> is asked for
    /// only when the quote has no frame yet, so a frame is never written for nothing. Returns the quote's id.
    /// </summary>
    public string RecordQuote(Quote quote, Func<(string File, PixelRect? Box)?>? shot = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var key = TextBlocks.Key(quote.Text);
            string? id = null;
            string? file = null;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, shot_file FROM quotes WHERE text_key = $k AND COALESCE(app_exe, '') = $app LIMIT 1";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$app", quote.AppExe ?? "");
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    id = r.GetString(0);
                    file = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            var frame = file is null ? shot?.Invoke() : null;
            var box = frame?.Box is { } b ? JsonSerializer.Serialize(b) : null;
            if (id is null)
            {
                id = quote.Id;
                Run("""
                    INSERT INTO quotes(id, created_utc, seen_utc, seen, language, text, text_key, translation, app_exe, window_title,
                                       shot_file, box, source)
                    VALUES($id, $created, $seen, 1, $lang, $text, $k, $tr, $app, $title, $shot, $box, $source)
                    """, ("$id", id), ("$created", Iso(quote.CreatedUtc)), ("$seen", Iso(quote.SeenUtc)), ("$lang", quote.Language),
                    ("$text", quote.Text), ("$k", key), ("$tr", quote.Translation), ("$app", quote.AppExe), ("$title", quote.WindowTitle),
                    ("$shot", frame?.File), ("$box", box), ("$source", quote.Source));
            }
            else
            {
                Run("""
                    UPDATE quotes SET seen = seen + 1, seen_utc = $seen, translation = COALESCE(translation, $tr),
                      window_title = COALESCE($title, window_title),
                      shot_file = COALESCE(shot_file, $shot), box = CASE WHEN shot_file IS NULL THEN $box ELSE box END
                    WHERE id = $id
                    """, ("$seen", Iso(quote.SeenUtc)), ("$tr", quote.Translation), ("$title", quote.WindowTitle),
                    ("$shot", frame?.File), ("$box", box), ("$id", id));
            }
            tx.Commit();
            return id;
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

    /// <summary>Removes quotes; returns the frames (relative to the data folder) no quote shows any more, for the caller to delete.</summary>
    public IReadOnlyList<string> DeleteQuotes(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var files = new List<string>();
            foreach (var id in ids)
            {
                if (Scalar("SELECT shot_file FROM quotes WHERE id = $id", ("$id", id)) is string file) files.Add(file);
                Run("DELETE FROM quotes WHERE id = $id", ("$id", id));
            }
            var unused = Unused(files);
            tx.Commit();
            return unused;
        }
    }

    /// <summary>
    /// Forgets the frames of these quotes, or of all of them (null); the quotes stay. Returns the frames no quote shows
    /// any more, for the caller to delete (a frame of «Весь экран» may be shared by several of its paragraphs).
    /// </summary>
    public IReadOnlyList<string> ClearQuoteShots(IEnumerable<string>? ids)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var wanted = ids?.ToHashSet(StringComparer.Ordinal);
            var cleared = new List<(string Id, string File)>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, shot_file FROM quotes WHERE shot_file IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (wanted is null || wanted.Contains(r.GetString(0))) cleared.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var (id, _) in cleared) Run("UPDATE quotes SET shot_file = NULL, box = NULL WHERE id = $id", ("$id", id));
            var unused = Unused(cleared.Select(c => c.File));
            tx.Commit();
            return unused;
        }
    }

    /// <summary>The frames among <paramref name="files"/> that no quote refers to.</summary>
    private List<string> Unused(IEnumerable<string> files) =>
        files.Distinct().Where(f => Scalar("SELECT 1 FROM quotes WHERE shot_file = $f LIMIT 1", ("$f", f)) is null).ToList();

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
