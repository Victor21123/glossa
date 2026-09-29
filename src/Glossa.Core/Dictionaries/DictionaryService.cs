using System.Text;

namespace Glossa.Core.Dictionaries;

/// <summary>Entries one pack returned for a lookup.</summary>
public sealed record DictSection(PackInfo Pack, IReadOnlyList<DictEntry> Entries);

/// <summary>
/// All installed packs, searched in the user's order. Lookups take candidate forms best-first (a set phrase, the
/// dictionary form, the surface form) and return each pack's entries for the first candidates that match.
/// </summary>
public sealed class DictionaryService : IDisposable
{
    private readonly string _dir;
    private readonly object _gate = new();
    private List<DictionaryPack> _all = [];
    private List<DictionaryPack> _enabled = [];

    public DictionaryService(string packsDir)
    {
        _dir = packsDir;
    }

    public string PacksDir => _dir;

    /// <summary>Every installed pack in display order, disabled ones included.</summary>
    public IReadOnlyList<PackInfo> Installed
    {
        get { lock (_gate) return _all.Select(p => p.Info).ToList(); }
    }

    public bool AnyFor(string language)
    {
        lock (_gate) return _enabled.Any(p => p.Info.SourceLanguage == language);
    }

    /// <summary>Reopens the packs folder. Unreadable files are skipped and reported.</summary>
    public IReadOnlyList<string> Reload(IReadOnlyList<string> order, IReadOnlyCollection<string> disabled)
    {
        var errors = new List<string>();
        var opened = new List<DictionaryPack>();
        if (Directory.Exists(_dir))
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*" + DictionaryPack.Extension))
            {
                try
                {
                    opened.Add(new DictionaryPack(file));
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        var ordered = opened
            .OrderBy(p => order.IndexOf(p.Info.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(p => p.Info.Priority)
            .ThenBy(p => p.Info.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        List<DictionaryPack> old;
        lock (_gate)
        {
            old = _all;
            _all = ordered;
            _enabled = ordered.Where(p => !disabled.Contains(p.Info.Id)).ToList();
        }
        foreach (var p in old) p.Dispose();
        return errors;
    }

    /// <summary>True if any enabled pack for the language has this exact key (used to find set phrases).</summary>
    /// <summary>
    /// Dictionary keys spelled closest to <paramref name="term"/>: the same first letter, at most
    /// <paramref name="maxDistance"/> letters inserted, dropped or replaced, one word (no phrases). Nearest first; the
    /// caller ranks further (how common each is: a form like watching is no worse a guess than a rare headword).
    /// </summary>
    public IReadOnlyList<(string Key, int Distance)> Near(string language, string term, int maxDistance, int limit)
    {
        var key = DictKeys.Normalize(term);
        if (key.Length < 3 || key.Contains(' ')) return [];
        var best = new Dictionary<string, (int Distance, int Rank)>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var p in _enabled)
            {
                if (p.Info.SourceLanguage != language) continue;
                foreach (var (k, rank) in p.KeysFrom(key[0], key.Length - maxDistance, key.Length + maxDistance))
                {
                    if (k == key || k.Contains(' ')) continue;
                    var d = Glossa.Core.Text.Spelling.Distance(key, k, maxDistance);
                    if (d > maxDistance) continue;
                    if (!best.TryGetValue(k, out var seen) || (d, rank).CompareTo(seen) < 0) best[k] = (d, rank);
                }
            }
        }
        return best.OrderBy(b => b.Value.Distance).ThenBy(b => b.Key, StringComparer.Ordinal)
            .Take(limit).Select(b => (b.Key, b.Value.Distance)).ToList();
    }

    /// <param name="maxRank">Only keys up to this rank (<see cref="DictKey.Alias"/>: headwords, readings, synonyms).</param>
    public bool HasKey(string language, string term, int maxRank = int.MaxValue)
    {
        var key = DictKeys.Normalize(term);
        if (key.Length == 0) return false;
        lock (_gate)
        {
            foreach (var p in _enabled)
                if (p.Info.SourceLanguage == language && p.HasKey(key, maxRank)) return true;
        }
        return false;
    }

    /// <param name="candidates">Forms to try, best first; each pack stops at <paramref name="perPack"/> entries.</param>
    /// <param name="reading">Entries with this reading come first (homographs in Japanese).</param>
    public IReadOnlyList<DictSection> Lookup(string language, IReadOnlyList<string> candidates, string? reading = null, int perPack = 2)
    {
        var keys = candidates.Select(c => DictKeys.Normalize(c)).Where(k => k.Length > 0).Distinct().ToList();
        var wantReading = reading is null ? null : DictKeys.Normalize(reading);
        var sections = new List<DictSection>();
        if (keys.Count == 0) return sections;

        lock (_gate)
        {
            foreach (var pack in _enabled)
            {
                if (pack.Info.SourceLanguage != language) continue;
                var found = new List<DictEntry>();
                var seen = new HashSet<long>();
                foreach (var key in keys)
                {
                    var entries = pack.Find(key, 16);
                    if (wantReading is not null)
                        entries = entries.OrderBy(e => e.Reading is { } r && DictKeys.Normalize(r) == wantReading ? 0 : 1).ToList();
                    foreach (var e in entries)
                    {
                        if (found.Count >= perPack) break;
                        if (seen.Add(e.Id)) found.Add(ExpandReferences(pack, e));
                    }
                    if (found.Count >= perPack) break;
                }
                if (found.Count > 0) sections.Add(new DictSection(pack.Info, found));
            }
        }
        return sections;
    }

    private const int MaxInlinedReferences = 3;

    /// <summary>
    /// Inlines the target under each line that is only a cross-reference ("2) вм. 我肏"). БКРС has ~88 000 entries
    /// like 傻逼 → «вм. 傻屄» whose meaning — often the crude one — lives only in the target. One level deep, plus
    /// one more when the target is itself only a reference.
    /// </summary>
    internal static DictEntry ExpandReferences(DictionaryPack pack, DictEntry entry, int depth = 0)
    {
        if (!entry.Body.Contains("[ref]", StringComparison.Ordinal)) return entry;
        var sb = new StringBuilder();
        var inlined = 0;
        foreach (var raw in entry.Body.Split('\n'))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(raw);
            if (inlined >= MaxInlinedReferences || DictMarkup.Parse(raw) is not [var line] || ReferenceOnly(line) is not { } targets)
                continue;
            foreach (var target in targets)
            {
                if (inlined >= MaxInlinedReferences) break;
                var hit = pack.Find(DictKeys.Normalize(target), 2).FirstOrDefault(e => e.Id != entry.Id);
                if (hit is null) continue;
                if (depth == 0) hit = ExpandReferences(pack, hit, depth + 1);
                var indent = new string('\t', line.Indent + 1);
                foreach (var t in hit.Body.Split('\n'))
                    if (t.Length > 0) sb.Append('\n').Append(indent).Append(t);
                inlined++;
            }
        }
        return inlined == 0 ? entry : entry with { Body = sb.ToString() };
    }

    private static readonly System.Text.RegularExpressions.Regex ReferenceFiller = new(
        @"^(?:\s|\d|[.,;:()=→\-]|(?-i:[IVX]+)\b|см\b|вм\b|тж\b|также\b|see\b|cf\b)*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The referenced headwords when the line holds nothing but references, labels and numbering.</summary>
    private static List<string>? ReferenceOnly(MarkupLine line)
    {
        var targets = new List<string>();
        foreach (var span in line.Spans)
        {
            if (span.Style.HasFlag(SpanStyle.Ref)) targets.Add(span.Text.Trim());
            else if (!span.Style.HasFlag(SpanStyle.Label) && !ReferenceFiller.IsMatch(span.Text)) return null;
        }
        return targets.Count > 0 ? targets : null;
    }

    /// <summary>
    /// Dictionary text for the AI prompt: the first pack that translates into <paramref name="target"/>, else the
    /// first one at all. Keeps the model on real senses and, for Russian, on real Russian equivalents.
    /// </summary>
    public static string? Hint(IReadOnlyList<DictSection> sections, string target, int maxChars = 500)
    {
        var section = sections.FirstOrDefault(s => s.Pack.TargetLanguage == target) ?? sections.FirstOrDefault();
        if (section is null) return null;
        var sb = new StringBuilder();
        foreach (var e in section.Entries)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(e.Headword);
            if (e.Reading is { Length: > 0 } r && !e.Headword.Contains(r)) sb.Append(" (").Append(r).Append(')');
            sb.Append(": ").Append(DictMarkup.ToPlain(e.Body, maxChars));
            if (sb.Length >= maxChars) break;
        }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) + "…" : sb.ToString();
    }

    /// <summary>Closes and deletes a pack.</summary>
    public void Remove(string id)
    {
        if (Close(id) is { } path) File.Delete(path);
    }

    /// <summary>Closes a pack so its file can be replaced (a rebuild); the next <see cref="Reload"/> reopens it.</summary>
    public string? Close(string id)
    {
        DictionaryPack? pack;
        lock (_gate)
        {
            pack = _all.FirstOrDefault(p => p.Info.Id == id);
            if (pack is null) return null;
            _all.Remove(pack);
            _enabled.Remove(pack);
        }
        var path = pack.Info.Path;
        pack.Dispose(); // unpooled: the file can be replaced right away
        return path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var p in _all) p.Dispose();
            _all = [];
            _enabled = [];
        }
    }
}

internal static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
