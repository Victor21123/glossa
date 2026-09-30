using System.Globalization;
using System.Text;
using Glossa.Core.Library;

namespace Glossa.Core.Export;

/// <summary>Plain exports: a Quizlet import set (term TAB definition) and a CSV with every field.</summary>
public static class TextExport
{
    public static void WriteQuizlet(string path, IEnumerable<SavedWord> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            var term = w.Reading is { Length: > 0 } r && r != w.Headword ? $"{w.Headword} [{r}]" : w.Headword;
            var def = string.Join(" — ", new[] { w.Translation, w.Definition, w.DefinitionTranslation }.Where(s => !string.IsNullOrWhiteSpace(s)));
            sb.Append(Clean(term)).Append('\t').Append(Clean(def)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static void WriteCsv(string path, IEnumerable<SavedWord> words)
    {
        var sb = new StringBuilder();
        sb.AppendLine("language,word,headword,reading,level,part_of_speech,translation,definition,context,context_translation,synonyms,app,created");
        foreach (var w in words)
        {
            sb.AppendJoin(',', new[]
            {
                w.Language, w.Word, w.Headword, w.Reading, w.Level, w.PartOfSpeech, w.Translation, w.Definition,
                w.Context, w.ContextTranslation, string.Join("; ", w.Synonyms), w.WindowTitle ?? w.AppExe,
                w.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            }.Select(Csv));
            sb.AppendLine();
        }
        // BOM so Excel opens Cyrillic and CJK correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Quotes as a table: the line, its translation, language, game, when, how many times it came.</summary>
    public static void WriteQuotesCsv(string path, IEnumerable<Quote> quotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("language,text,translation,app,seen,first,last");
        foreach (var q in quotes)
        {
            sb.AppendJoin(',', new[]
            {
                q.Language, q.Text, q.Translation, q.WindowTitle ?? q.AppExe, q.Seen.ToString(CultureInfo.InvariantCulture),
                q.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                q.SeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }.Select(Csv));
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string Csv(string? s)
    {
        s ??= "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
