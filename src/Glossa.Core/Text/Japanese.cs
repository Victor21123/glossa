using System.Text;
using NMeCab.Specialized;

namespace Glossa.Core.Text;

/// <param name="Lemma">Dictionary form as written (UniDic orthBase): つけ → つける.</param>
/// <param name="Lexeme">Standard lexeme spelling (UniDic lemma): つけ → 付ける.</param>
/// <param name="Subclass">UniDic pos3: "サ変可能" marks a noun that takes する (合体 → 合体する).</param>
public sealed record JaToken(
    string Surface,
    int Start,
    string Lemma,
    string Reading,
    string LemmaReading,
    string PartOfSpeech,
    string Lexeme = "",
    string Subclass = "")
{
    public int End => Start + Surface.Length;
}

/// <summary>A word or set phrase found at a position, with the forms to look up in dictionaries.</summary>
public sealed record JaWord(
    int Start,
    int Length,
    string DictionaryForm,
    string? Reading,
    string PartOfSpeech,
    IReadOnlyList<string> Candidates);

/// <summary>
/// Japanese morphological analysis on UniDic 2.1 (the unidic-lite build) via NMeCab.
/// Supplies readings and dictionary forms so the LLM never has to guess them.
/// </summary>
public sealed class JapaneseAnalyzer : ITermMatcher, IDisposable
{
    private readonly MeCabUniDic21Tagger _tagger;
    private readonly object _gate = new();

    public JapaneseAnalyzer(string dicDir)
    {
        _tagger = MeCabUniDic21Tagger.Create(dicDir);
    }

    public IReadOnlyList<JaToken> Tokenize(string text)
    {
        MeCabUniDic21Node[] nodes;
        lock (_gate) nodes = _tagger.Parse(text);

        var tokens = new List<JaToken>(nodes.Length);
        foreach (var n in nodes)
        {
            if (string.IsNullOrEmpty(n.Surface)) continue;
            // UniDic lemmas may carry an English gloss suffix ("為る" is fine, "ニャー-meow" is not).
            var lemma = StripGloss(n.OrthBase ?? n.Lemma ?? n.Surface);
            tokens.Add(new JaToken(
                n.Surface,
                n.BPos,
                lemma,
                ToHiragana(n.Kana ?? ""),
                ToHiragana(n.KanaBase ?? ""),
                n.Pos1 ?? "",
                StripGloss(n.Lemma ?? lemma),
                n.Pos3 ?? ""));
        }
        return tokens;
    }

    /// <summary>
    /// The word at a character: the longest dictionary phrase that starts at the token under it (up to
    /// <paramref name="maxTokens"/> tokens; the last one in dictionary form, so 気をつけた gives 気をつける), else
    /// the token itself. This replaces rule-based deinflection: UniDic already knows every inflected form.
    /// </summary>
    public JaWord? WordAt(string text, int index, Func<string, bool>? exists = null, int maxTokens = 6)
    {
        var tokens = Tokenize(text);
        var i = -1;
        for (var t = 0; t < tokens.Count; t++)
        {
            if (index >= tokens[t].Start && index < tokens[t].End) { i = t; break; }
        }
        if (i < 0) return null;
        var first = tokens[i];

        if (exists is not null)
        {
            for (var k = Math.Min(tokens.Count - 1, i + maxTokens - 1); k > i; k--)
            {
                if (!Contiguous(tokens, i, k) || tokens[k].PartOfSpeech is "補助記号" or "空白") continue;
                var last = tokens[k];
                var prefix = string.Concat(tokens.Skip(i).Take(k - i).Select(t => t.Surface));
                var prefixReading = string.Concat(tokens.Skip(i).Take(k - i).Select(t => t.Reading));
                foreach (var (form, reading) in new[] { (prefix + last.Lemma, prefixReading + last.LemmaReading), (prefix + last.Surface, prefixReading + last.Reading) })
                {
                    if (!exists(form)) continue;
                    return new JaWord(first.Start, last.End - first.Start, form, reading, first.PartOfSpeech,
                        Distinct(form, first.Lemma, first.Lexeme, first.Surface));
                }
            }
        }

        // A する-noun used as a verb (合体していく): its dictionary form is the verb, 合体する, which dictionaries head as 合体.
        if (first.PartOfSpeech == "名詞" && first.Subclass == "サ変可能" && i + 1 < tokens.Count
            && tokens[i + 1].Lexeme == "為る" && Contiguous(tokens, i, i + 1))
        {
            var verb = first.Lemma + "する";
            return new JaWord(first.Start, tokens[i + 1].End - first.Start, verb, first.Reading + "する", "動詞",
                Distinct(verb, first.Lemma, first.Lexeme, first.Surface));
        }

        // The surface reading is exact for the form on screen; the lemma reading covers inflected words.
        var single = first.Lemma == first.Surface ? first.Reading : first.LemmaReading;
        return new JaWord(first.Start, first.Surface.Length, first.Lemma, single, first.PartOfSpeech,
            Distinct(first.Lemma, first.Lexeme, first.Surface));
    }

    private static bool Contiguous(IReadOnlyList<JaToken> tokens, int from, int to)
    {
        for (var t = from; t < to; t++)
        {
            if (tokens[t].End != tokens[t + 1].Start || tokens[t].PartOfSpeech is "補助記号" or "空白") return false;
        }
        return true;
    }

    private static List<string> Distinct(params string[] forms) =>
        forms.Where(f => f.Length > 0).Distinct().ToList();

    /// <summary>Span of the token covering <paramref name="index"/>; falls back to one character.</summary>
    public (int Start, int Length) Match(string text, int index)
    {
        foreach (var t in Tokenize(text))
        {
            if (index >= t.Start && index < t.End) return (t.Start, t.Surface.Length);
        }
        return (index, 1);
    }

    /// <summary>The token covering a character plus its analysis, or null.</summary>
    public JaToken? TokenAt(string text, int index) =>
        Tokenize(text).FirstOrDefault(t => index >= t.Start && index < t.End);

    public static string ToHiragana(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    private static string StripGloss(string lemma)
    {
        var dash = lemma.IndexOf('-');
        return dash > 0 ? lemma[..dash] : lemma;
    }

    public void Dispose() => _tagger.Dispose();
}
