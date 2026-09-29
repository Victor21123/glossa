using System.Text;
using System.Text.RegularExpressions;
using Glossa.Core.Text;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// Whether the AI's translation is one a dictionary lists, for the user to see ("✓ как в словаре" or
/// "в словаре: …"). It is never fed back to the model: the context decides the sense, the dictionary only shows
/// where the two part.
/// </summary>
public sealed record DictionaryCheck(bool Matches, IReadOnlyList<string> Equivalents)
{
    private const int MaxItemLength = 40;

    /// <returns>Null when there is no translation or no dictionary into <paramref name="target"/>.</returns>
    public static DictionaryCheck? Of(string? translation, IReadOnlyList<DictSection> sections, string target, int show = 3)
    {
        if (string.IsNullOrWhiteSpace(translation)) return null;
        var equivalents = Collect(sections, target);
        if (equivalents.Count == 0) return null;

        var mine = SplitItems(translation).Select(Words).Where(w => w.Length > 0).ToList();
        var theirs = equivalents.Select(Words).ToList();
        var matches = mine.Any(m => theirs.Any(t => Contains(t, m) || Contains(m, t)));
        return new DictionaryCheck(matches, equivalents.Take(show).ToList());
    }

    /// <summary>
    /// Target-language equivalents from the packs that translate into <paramref name="target"/>, in pack order:
    /// the highlighted translations when an entry marks them (Wiktionary, JMdict), otherwise its plain text
    /// without labels, examples and comments (БКРС, Warodai; lines with CJK are examples there).
    /// </summary>
    internal static List<string> Collect(IReadOnlyList<DictSection> sections, string target)
    {
        var items = new List<string>();
        foreach (var section in sections.Where(s => s.Pack.TargetLanguage == target))
        {
            foreach (var entry in section.Entries)
            {
                var lines = DictMarkup.Parse(entry.Body);
                var accented = lines.SelectMany(l => l.Spans).Where(s => s.Style.HasFlag(SpanStyle.Accent)).Select(s => s.Text).ToList();
                var texts = accented.Count > 0
                    ? accented
                    : lines.Where(l => !Scripts.ContainsCjk(l.Text))
                        .Select(l => string.Concat(l.Spans.Where(s => (s.Style & Skipped) == 0).Select(s => s.Text)));
                foreach (var text in texts)
                    foreach (var item in SplitItems(text))
                        if (item.Length <= MaxItemLength && InScript(item, target) && !items.Contains(item)) items.Add(item);
            }
        }
        return items;
    }

    private const SpanStyle Skipped = SpanStyle.Label | SpanStyle.Example | SpanStyle.Muted | SpanStyle.Ref | SpanStyle.Italic;

    private static readonly Regex Parenthesized = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Numbering = new(@"(?:^|\s)(?:\d+[.)]|[а-яa-z]\))\s*", RegexOptions.Compiled);

    private static IEnumerable<string> SplitItems(string text)
    {
        text = Parenthesized.Replace(text, " ");
        foreach (var part in text.Split([';', ',', '/', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = Numbering.Replace(part, " ").Trim(' ', '.', '!', '?', '…', '«', '»', '"', '—', '-', '\t');
            if (item.Length > 0) yield return item;
        }
    }

    private static bool InScript(string item, string target) => target switch
    {
        "ru" => item.Any(c => Scripts.Of(c) == Script.Cyrillic),
        "en" => item.Any(c => Scripts.Of(c) == Script.Latin),
        _ => true,
    };

    /// <summary>Lower-case words with ё folded and punctuation dropped.</summary>
    private static string[] Words(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw == 'ё' ? 'е' : raw;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The words of <paramref name="part"/> appear in <paramref name="whole"/> in a row, forms allowed.</summary>
    private static bool Contains(string[] whole, string[] part)
    {
        // "да" inside "Черт возьми, да!" is no evidence.
        if (part.Length == 0 || part.Length > whole.Length || part.Sum(w => w.Length) < 4) return false;
        for (var start = 0; start + part.Length <= whole.Length; start++)
        {
            var all = true;
            for (var k = 0; k < part.Length && all; k++) all = SameWord(whole[start + k], part[k]);
            if (all) return true;
        }
        return false;
    }

    /// <summary>
    /// Equal, or the same stem with a different ending (сиськи / сиська, ублюдок / ублюдки): a common prefix of at
    /// least 5 letters that leaves at most 3 letters of either word.
    /// </summary>
    private static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n >= 5 && a.Length - n <= 3 && b.Length - n <= 3;
    }
}
