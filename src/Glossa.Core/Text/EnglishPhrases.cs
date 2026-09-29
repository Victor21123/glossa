namespace Glossa.Core.Text;

/// <summary>Search forms for an English word: phrases around it (phrasal verbs, idioms), longest first.</summary>
public static class EnglishPhrases
{
    /// <summary>
    /// Dictionaries list idioms with placeholder pronouns: the subject's own ("go fuck oneself", "make up one's
    /// mind") or someone else's ("blow someone's mind", "get on someone's nerves").
    /// </summary>
    private static readonly Dictionary<string, string> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        ["myself"] = "oneself", ["yourself"] = "oneself", ["himself"] = "oneself", ["herself"] = "oneself",
        ["itself"] = "oneself", ["ourselves"] = "oneself", ["yourselves"] = "oneself", ["themselves"] = "oneself",
        ["thyself"] = "oneself",
        ["my"] = "one's", ["your"] = "one's", ["his"] = "one's", ["her"] = "one's", ["its"] = "one's",
        ["our"] = "one's", ["their"] = "one's", ["thy"] = "one's",
    };

    private static readonly Dictionary<string, string> Someone = new(StringComparer.OrdinalIgnoreCase)
    {
        ["my"] = "someone's", ["your"] = "someone's", ["his"] = "someone's", ["her"] = "someone's",
        ["our"] = "someone's", ["their"] = "someone's",
        ["me"] = "someone", ["you"] = "someone", ["him"] = "someone", ["us"] = "someone", ["them"] = "someone",
    };

    /// <summary>Objects that split a phrasal verb: "piss him off" is listed as "piss off".</summary>
    private static readonly HashSet<string> Objects = new(StringComparer.OrdinalIgnoreCase)
        { "me", "you", "him", "her", "it", "us", "them", "this", "that" };

    /// <summary>
    /// "She looked after the cat" at "looked": spans of 2..<paramref name="maxWords"/> words that contain the word
    /// and start at most <paramref name="maxBefore"/> words before it, longest first and, of equal length, those
    /// starting at the word first. Each span also comes with pronouns as dictionary placeholders ("fuck yourself"
    /// → "fuck oneself", "blew my mind" → "blew someone's mind") and without an object inside it ("piss him off"
    /// → "piss off"). The word itself comes last. Only a plain space joins words; punctuation ends a phrase.
    /// </summary>
    public static IReadOnlyList<string> Candidates(WordHit hit, int maxWords = 4, int maxBefore = 3)
    {
        var (words, at) = WordsAround(hit);
        var result = new List<string>();
        if (at < 0)
        {
            result.Add(hit.Word);
            return result;
        }

        for (var n = Math.Min(maxWords, words.Count); n >= 2; n--)
        {
            for (var start = at; start >= Math.Max(0, at - maxBefore); start--)
            {
                var end = start + n - 1;
                if (end < at || end >= words.Count || !Joined(words, start, end)) continue;
                var span = words.GetRange(start, n).Select(w => w.Text).ToList();
                Add(result, string.Join(' ', span));
                Add(result, string.Join(' ', span.Select(w => Own.GetValueOrDefault(w, w))));
                Add(result, string.Join(' ', span.Select(w => Someone.GetValueOrDefault(w, w))));
                if (n >= 3)
                    Add(result, string.Join(' ', span.Where((w, k) => k == 0 || k == n - 1 || !Objects.Contains(w))));
            }
        }
        Add(result, hit.Word);
        return result;

        static void Add(List<string> list, string s)
        {
            if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }
    }

    private sealed record Word(string Text, bool SpaceAfter);

    /// <summary>Words of the context with whether a single space (not punctuation) follows; the index of the hit word.</summary>
    private static (List<Word> Words, int At) WordsAround(WordHit hit)
    {
        var words = new List<Word>();
        var at = -1;
        var text = hit.Context;
        if (hit.ContextOffset < 0 || hit.ContextOffset >= text.Length) return (words, at);

        var i = 0;
        while (i < text.Length)
        {
            if (!Scripts.IsWordChar(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && Scripts.IsWordChar(text[i])) i++;
            var word = text[start..i].Trim('\'', '’', '-');
            if (word.Length == 0) continue;
            if (start <= hit.ContextOffset && hit.ContextOffset < i) at = words.Count;
            var spaceAfter = i < text.Length && text[i] == ' ' && i + 1 < text.Length && Scripts.IsWordChar(text[i + 1]);
            words.Add(new Word(word, spaceAfter));
        }
        return (words, at);
    }

    private static bool Joined(List<Word> words, int start, int end)
    {
        for (var k = start; k < end; k++)
            if (!words[k].SpaceAfter) return false;
        return true;
    }
}
