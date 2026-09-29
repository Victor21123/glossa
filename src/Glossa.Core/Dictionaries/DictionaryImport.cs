using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glossa.Core.Dictionaries;

public readonly record struct ImportProgress(string Stage, double Fraction);

/// <summary>Imports a user's dictionary file of any supported format into a pack.</summary>
public static class DictionaryImport
{
    public const string FileFilter = "Словари (*.dsl;*.dz;*.gz;*.ifo;*.mdx;*.zip)|*.dsl;*.dz;*.gz;*.ifo;*.mdx;*.zip";

    public static IDictionaryImporter? ImporterFor(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".dsl") || name.EndsWith(".dsl.dz") || name.EndsWith(".dsl.gz")) return new DslImporter();
        if (name.EndsWith(".ifo")) return new StarDictImporter();
        if (name.EndsWith(".mdx")) return new MdxImporter();
        if (name == "warodai.txt") return new WarodaiImporter();
        if (name.EndsWith(".zip")) return IsYomitan(path) ? new YomitanImporter() : null;
        if (name.EndsWith(".dz") || name.EndsWith(".gz")) return new DslImporter();
        return null;
    }

    /// <summary>The pack id and title an import of this file will get (to close an older copy first).</summary>
    public static PackMeta? DefaultMeta(string path)
    {
        var importer = ImporterFor(path);
        if (importer is null)
        {
            // A zip of DSL/StarDict/MDX files: named after the archive.
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return new PackMeta("user-" + Slug(name) + "-" + ShortHash(Path.GetFileName(path)), name, "?", "?", 50, Origin: Path.GetFileName(path));
        }
        var info = importer.Probe(path);
        return new PackMeta("user-" + Slug(info.Title) + "-" + ShortHash(Path.GetFileName(path)), info.Title,
            info.SourceLanguage ?? "?", info.TargetLanguage ?? "?", 50, info.Description, Origin: Path.GetFileName(path));
    }

    /// <summary>
    /// Imports <paramref name="path"/>. A zip that is not a Yomitan dictionary is unpacked and every DSL, StarDict
    /// or MDX file inside goes into one pack (БКРС, for one, ships as three DSL parts).
    /// </summary>
    public static PackInfo Import(string path, string packsDir, string workDir, PackMeta? meta = null,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        string? unpacked = null;
        try
        {
            var parts = new List<(string File, IDictionaryImporter Importer)>();
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !IsYomitan(path))
            {
                progress?.Report(new ImportProgress("Распаковка", 0));
                unpacked = Path.Combine(workDir, "unzip-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    ZipFile.ExtractToDirectory(path, unpacked);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("Архив не распаковывается (возможно, он защищён паролем) — распакуйте его и выберите файл словаря.", ex);
                }
                foreach (var f in Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (f.EndsWith("_abrv.dsl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImporterFor(f) is { } imp and not YomitanImporter) parts.Add((f, imp));
                }
            }
            else if (ImporterFor(path) is { } importer)
            {
                parts.Add((path, importer));
            }
            if (parts.Count == 0) throw new NotSupportedException("Формат не поддерживается: нужен DSL, StarDict (.ifo), MDX, Yomitan (.zip) или архив с ними.");

            if (meta is null || meta.SourceLanguage == "?" && meta.Title == Path.GetFileNameWithoutExtension(path))
            {
                // Title and languages from the dictionary inside the archive, id from the archive name.
                var info = parts[0].Importer.Probe(parts[0].File);
                meta = new PackMeta(
                    meta?.Id ?? "user-" + Slug(info.Title) + "-" + ShortHash(Path.GetFileName(path)),
                    info.Title,
                    info.SourceLanguage ?? "?",
                    info.TargetLanguage ?? "?",
                    Priority: 50,
                    Description: info.Description,
                    Origin: Path.GetFileName(path));
            }

            var packPath = Path.Combine(packsDir, meta.Id + DictionaryPack.Extension);
            using var writer = new DictionaryPackWriter(packPath, meta);
            for (var p = 0; p < parts.Count; p++)
            {
                var (file, imp) = parts[p];
                var stage = parts.Count > 1 ? $"Импорт {p + 1}/{parts.Count}" : "Импорт";
                var part = p;
                imp.Import(file, writer, workDir, new SyncProgress(f => progress?.Report(new ImportProgress(stage, (part + f) / parts.Count))), ct);
            }
            if (writer.Count == 0) throw new InvalidDataException("В словаре не найдено ни одной статьи.");
            progress?.Report(new ImportProgress("Индекс", 1));
            return writer.Complete(
                meta.SourceLanguage == "?" ? writer.Stats.Source : null,
                meta.TargetLanguage == "?" ? writer.Stats.Target : null);
        }
        finally
        {
            if (unpacked is not null)
            {
                try { Directory.Delete(unpacked, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static bool IsYomitan(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.GetEntry("index.json") is not null && zip.Entries.Any(e => e.Name.StartsWith("term_bank_", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 24) break;
        }
        return sb.ToString().Trim('-') is { Length: > 0 } slug ? slug : "dict";
    }

    private static string ShortHash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..6].ToLowerInvariant();

    /// <summary>Reports on the calling thread; Progress&lt;T&gt; would post to a captured context.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>
/// Yomitan / Yomichan dictionaries (.zip with index.json and term_bank_*.json). Structured content is turned into
/// HTML and then into markup, so both paths share one converter.
/// </summary>
public sealed class YomitanImporter : IDictionaryImporter
{
    public SourceInfo Probe(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("index.json")!.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new SourceInfo(Str("title") ?? Path.GetFileNameWithoutExtension(path),
            Str("sourceLanguage"), Str("targetLanguage"), Str("description") ?? Str("attribution"));
    }

    public void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var banks = zip.Entries
            .Where(e => e.Name.StartsWith("term_bank_", StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(e => BankNumber(e.Name))
            .ToList();

        for (var b = 0; b < banks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)b / banks.Count);
            using var stream = banks[b].Open();
            using var doc = JsonDocument.Parse(stream);

            // Rows of one JMdict-style entry share expression, reading and sequence: merge them into one article.
            string? expression = null, reading = null;
            long sequence = -1;
            var senses = new List<string>();

            void Flush()
            {
                if (expression is null || senses.Count == 0) return;
                var body = senses.Count == 1 ? senses[0] : string.Join("\n", senses.Select((s, i) => $"{i + 1}. {s}"));
                var keys = new List<DictKey> { new(expression, 0) };
                if (!string.IsNullOrEmpty(reading) && reading != expression) keys.Add(new DictKey(reading, 1));
                writer.Add(expression, string.IsNullOrEmpty(reading) || reading == expression ? null : reading, body, keys);
                senses.Clear();
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;
                var expr = row[0].GetString() ?? "";
                var read = row[1].GetString() ?? "";
                var tags = row[2].ValueKind == JsonValueKind.String ? row[2].GetString() : null;
                var seq = row.GetArrayLength() > 6 && row[6].ValueKind == JsonValueKind.Number ? row[6].GetInt64() : -1;

                var sense = Sense(tags, row[5].ValueKind == JsonValueKind.Array ? row[5] : default, row);
                if (sense.Length == 0) continue;
                if (expr != expression || read != reading || seq != sequence || seq <= 0)
                {
                    Flush();
                    expression = expr;
                    reading = read;
                    sequence = seq;
                }
                senses.Add(sense);
            }
            Flush();
        }
    }

    private static int BankNumber(string name) =>
        int.TryParse(name.AsSpan("term_bank_".Length, name.Length - "term_bank_".Length - ".json".Length), out var n) ? n : 0;

    private static string Sense(string? tags, JsonElement glossary, JsonElement row)
    {
        var html = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(tags)) html.Append("<abbr>").Append(WebUtility.HtmlEncode(tags)).Append("</abbr> ");

        var items = new List<JsonElement>();
        if (glossary.ValueKind == JsonValueKind.Array) items.AddRange(glossary.EnumerateArray());
        else for (var i = 5; i < row.GetArrayLength(); i++) items.Add(row[i]); // format 1: glossary strings follow

        var strings = items.Count(i => i.ValueKind == JsonValueKind.String);
        foreach (var item in items)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    html.Append(WebUtility.HtmlEncode(item.GetString()));
                    html.Append(strings > 1 ? "; " : "");
                    break;
                case JsonValueKind.Object when item.TryGetProperty("type", out var type):
                    if (type.GetString() == "text" && item.TryGetProperty("text", out var text))
                        html.Append(WebUtility.HtmlEncode(text.GetString())).Append("; ");
                    else if (type.GetString() == "structured-content" && item.TryGetProperty("content", out var content))
                        StructuredToHtml(content, html);
                    break;
                // Arrays are deinflection hints ([base form, [rules]]): nothing to show.
            }
        }
        var body = HtmlMarkup.Convert(html.ToString().TrimEnd(' ', ';'));
        // One sense per numbered line: keep its own lines, indented.
        return body.Replace("\n", "\n\t");
    }

    private static void StructuredToHtml(JsonElement node, StringBuilder html)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                html.Append(WebUtility.HtmlEncode(node.GetString()));
                return;
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray()) StructuredToHtml(child, html);
                return;
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        var tag = node.TryGetProperty("tag", out var t) ? t.GetString() ?? "span" : "span";
        if (tag is "img" or "image") return;
        if (tag == "br")
        {
            html.Append("<br>");
            return;
        }
        var cls = "";
        if (node.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("content", out var dc) && dc.GetString() is { } role)
        {
            cls = role switch
            {
                _ when role.Contains("example", StringComparison.OrdinalIgnoreCase) => "ex",
                _ when role.Contains("part-of-speech", StringComparison.OrdinalIgnoreCase) || role.Contains("tag", StringComparison.OrdinalIgnoreCase) => "p",
                _ when role.Contains("note", StringComparison.OrdinalIgnoreCase) || role.Contains("info", StringComparison.OrdinalIgnoreCase) => "c",
                _ => "",
            };
        }
        var safeTag = tag.All(char.IsLetterOrDigit) ? tag : "span";
        html.Append('<').Append(safeTag);
        if (cls.Length > 0) html.Append(" class=\"").Append(cls).Append('"');
        if (safeTag == "a" && node.TryGetProperty("href", out _)) html.Append(" href=\"#\"");
        html.Append('>');
        if (node.TryGetProperty("content", out var content)) StructuredToHtml(content, html);
        html.Append("</").Append(safeTag).Append('>');
    }
}
