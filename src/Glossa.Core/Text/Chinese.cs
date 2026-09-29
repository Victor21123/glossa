using System.Text;

namespace Glossa.Core.Text;

public sealed record CedictEntry(string Traditional, string Simplified, string Pinyin, IReadOnlyList<string> Glosses)
{
    public bool IsProperName => Pinyin.Length > 0 && char.IsUpper(Pinyin[0]);
}

/// <summary>
/// CC-CEDICT: word boundaries for Chinese (bidirectional maximum matching over the dictionary), pinyin with
/// tone marks and English senses that are handed to the AI as candidates.
/// </summary>
public sealed class ChineseDictionary : ITermMatcher
{
    private readonly Dictionary<string, List<CedictEntry>> _entries = new(StringComparer.Ordinal);
    private readonly int _maxLength;

    public ChineseDictionary(string cedictPath)
    {
        foreach (var line in File.ReadLines(cedictPath, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var e = Parse(line);
            if (e is null) continue;
            Add(e.Simplified, e);
            if (e.Traditional != e.Simplified) Add(e.Traditional, e);
            _maxLength = Math.Max(_maxLength, e.Simplified.Length);
        }
    }

    public int Count => _entries.Count;

    public IReadOnlyList<CedictEntry> Lookup(string word) =>
        _entries.TryGetValue(word, out var list) ? list : [];

    public bool Contains(string word) => _entries.ContainsKey(word);

    /// <summary>The dictionary word covering <paramref name="index"/> in a segmentation of the whole text.</summary>
    public (int Start, int Length) Match(string text, int index)
    {
        var start = index;
        var end = index + 1;
        // Segment only the run of Han characters around the cursor; punctuation and kana split words anyway.
        while (start > 0 && Scripts.Of(text[start - 1]) == Script.Han) start--;
        while (end < text.Length && Scripts.Of(text[end]) == Script.Han) end++;
        if (Scripts.Of(text[index]) != Script.Han) return (index, 1);

        var run = text[start..end];
        var segments = Segment(run);
        var pos = 0;
        foreach (var s in segments)
        {
            if (index - start < pos + s.Length) return (start + pos, s.Length);
            pos += s.Length;
        }
        return (index, 1);
    }

    /// <summary>Bidirectional maximum matching: picks the forward or backward split with fewer, longer words.</summary>
    public List<string> Segment(string run)
    {
        var fwd = Forward(run);
        var bwd = Backward(run);
        if (fwd.Count != bwd.Count) return fwd.Count < bwd.Count ? fwd : bwd;
        var fwdSingles = fwd.Count(w => w.Length == 1);
        var bwdSingles = bwd.Count(w => w.Length == 1);
        return bwdSingles < fwdSingles ? bwd : fwd; // backward wins ties on singles, as is usual for Chinese
    }

    private List<string> Forward(string s)
    {
        var result = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var len = Math.Min(_maxLength, s.Length - i);
            while (len > 1 && !_entries.ContainsKey(s.Substring(i, len))) len--;
            result.Add(s.Substring(i, len));
            i += len;
        }
        return result;
    }

    private List<string> Backward(string s)
    {
        var result = new List<string>();
        var j = s.Length;
        while (j > 0)
        {
            var len = Math.Min(_maxLength, j);
            while (len > 1 && !_entries.ContainsKey(s.Substring(j - len, len))) len--;
            result.Insert(0, s.Substring(j - len, len));
            j -= len;
        }
        return result;
    }

    /// <summary>Common readings first (proper names last), as tone-marked pinyin.</summary>
    public string? PinyinOf(string word)
    {
        var entries = Lookup(word);
        var common = entries.FirstOrDefault(e => !e.IsProperName) ?? entries.FirstOrDefault();
        return common is null ? null : ToToneMarks(common.Pinyin);
    }

    /// <summary>Pinyin of a phrase CC-CEDICT may not list as a whole: word by word ("bié dānxīn").</summary>
    public string? PhrasePinyinOf(string phrase)
    {
        if (PinyinOf(phrase) is { } whole) return whole;
        var parts = Segment(phrase).Select(PinyinOf).ToList();
        return parts.Any(p => p is null) ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// The segment covering <paramref name="index"/>, extended forward by whole segments while the longer
    /// string is a dictionary headword (别 + 担心 → 别担心, 包 + 在我身上 → 包在我身上), as pop-up dictionaries do.
    /// </summary>
    public (int Start, int Length) MatchPhrase(string text, int index, Func<string, bool> exists, int maxLength = 6)
    {
        var (start, length) = Match(text, index);
        if (length == 0 || Scripts.Of(text[index]) != Script.Han) return (start, length);
        var end = start + length;
        var runEnd = end;
        while (runEnd < text.Length && Scripts.Of(text[runEnd]) == Script.Han) runEnd++;
        if (runEnd == end) return (start, length);

        // Segment boundaries after the word, within the same run of Han characters.
        var bounds = new List<int>();
        var pos = end;
        foreach (var s in Segment(text[end..runEnd]))
        {
            pos += s.Length;
            if (pos - start > maxLength) break;
            bounds.Add(pos);
        }
        for (var b = bounds.Count - 1; b >= 0; b--)
        {
            if (exists(text[start..bounds[b]])) return (start, bounds[b] - start);
        }
        return (start, length);
    }

    /// <summary>Senses as one compact hint line for the AI ("to be born; birth").</summary>
    public string? SensesOf(string word, int max = 8)
    {
        var glosses = Lookup(word)
            .OrderBy(e => e.IsProperName)
            .SelectMany(e => e.Glosses)
            .Where(g => !g.StartsWith("CL:", StringComparison.Ordinal))
            .Distinct()
            .Take(max)
            .ToList();
        return glosses.Count == 0 ? null : string.Join("; ", glosses);
    }

    internal static CedictEntry? Parse(string line)
    {
        // 傳統 传统 [chuan2 tong3] /tradition/traditional/
        var sp1 = line.IndexOf(' ');
        var sp2 = sp1 < 0 ? -1 : line.IndexOf(' ', sp1 + 1);
        var lb = line.IndexOf('[');
        var rb = line.IndexOf(']');
        var slash = line.IndexOf('/');
        if (sp1 < 0 || sp2 < 0 || lb < 0 || rb < lb || slash < rb) return null;
        var glosses = line[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new CedictEntry(line[..sp1], line[(sp1 + 1)..sp2], line[(lb + 1)..rb], glosses);
    }

    /// <summary>"chu1 sheng1" → "chūshēng"; "lu:4" → "lǜ".</summary>
    public static string ToToneMarks(string numbered)
    {
        var sb = new StringBuilder();
        foreach (var raw in numbered.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var syl = raw.Replace("u:", "ü").Replace("v", "ü");
            var tone = syl.Length > 0 && char.IsDigit(syl[^1]) ? syl[^1] - '0' : 5;
            if (char.IsDigit(syl[^1])) syl = syl[..^1];
            sb.Append(tone is >= 1 and <= 4 ? Mark(syl, tone) : syl);
        }
        return sb.ToString();

        static string Mark(string s, int tone)
        {
            // a and e always take the mark; in "ou" the o does; otherwise the last vowel.
            var lower = s.ToLowerInvariant();
            var at = lower.IndexOf('a');
            if (at < 0) at = lower.IndexOf('e');
            if (at < 0) at = lower.IndexOf("ou", StringComparison.Ordinal);
            if (at < 0) at = lower.LastIndexOfAny(['i', 'o', 'u', 'ü']);
            if (at < 0) return s;
            const string vowels = "aeiouü";
            string[] marked = ["āēīōūǖ", "áéíóúǘ", "ǎěǐǒǔǚ", "àèìòùǜ"];
            var v = vowels.IndexOf(lower[at]);
            var c = marked[tone - 1][v];
            if (char.IsUpper(s[at])) c = char.ToUpperInvariant(c);
            return s[..at] + c + s[(at + 1)..];
        }
    }

    private void Add(string key, CedictEntry e)
    {
        if (!_entries.TryGetValue(key, out var list)) _entries[key] = list = [];
        list.Add(e);
    }
}
