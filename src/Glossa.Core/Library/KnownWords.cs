namespace Glossa.Core.Library;

/// <summary>
/// The user's dictionary as a table for a still frame: which words on screen are already saved, and which of them are
/// «Не могу запомнить». A word matches by itself, its dictionary form or any form it was met in, ignoring case.
/// </summary>
public sealed class KnownWords
{
    private readonly Dictionary<string, bool> _forms = new(StringComparer.OrdinalIgnoreCase);

    public KnownWords(IEnumerable<SavedWord> words)
    {
        foreach (var w in words)
            foreach (var form in new[] { w.Word, w.DictionaryForm }.Concat(w.Contexts.Select(c => c.Surface)))
            {
                if (form?.Trim() is not { Length: > 0 } f || IsNoise(f)) continue;
                _forms[f] = _forms.GetValueOrDefault(f) || w.Pinned;
            }
    }

    public int Count => _forms.Count;

    /// <summary>True for a pinned saved word, false for another saved word, null for a word not in the dictionary.</summary>
    public bool? Find(string text) => text.Trim() is { Length: > 0 } t && !IsNoise(t) && _forms.TryGetValue(t, out var pinned) ? pinned : null;

    /// <summary>A lone Latin letter («I», «a») would mark half the screen; a lone kanji or hanzi is a real word.</summary>
    private static bool IsNoise(string text) => text.Length == 1 && text[0] < 0x2E80;
}
