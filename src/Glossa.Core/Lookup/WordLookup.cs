using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Core.Lookup;

/// <summary>Everything known about a word before the AI answers.</summary>
public sealed record LookupPlan(
    WordHit Hit,
    string Language,
    string Target,
    WordCard Seed,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<DictSection> Sections,
    string? Hint);

/// <summary>
/// The deterministic half of a lookup, shared by the app and glossa-cli: word under a point, its language,
/// dictionary form and reading, offline dictionary articles, listed level and the dictionary hint for the AI.
/// </summary>
public sealed class WordLookup(
    Func<JapaneseAnalyzer?> japanese,
    Func<ChineseDictionary?> chinese,
    DictionaryService dictionaries,
    Func<LevelService?> levels)
{
    private readonly HitTester _hitTester = new();

    public WordHit? Hit(OcrPage page, double x, double y, string preferredCjk) =>
        _hitTester.Hit(page, x, y, Matcher(preferredCjk));

    /// <summary>Word boundaries in Japanese and Chinese lines, as a lookup draws them (a still frame steps word by word).</summary>
    public ITermMatcher Matcher(string preferredCjk) =>
        new CjkMatcher(japanese(), chinese(), preferredCjk, JapaneseExists, ChineseExists);

    /// <param name="language">The user's choice for a word typed in by hand; null detects it from the text.</param>
    public LookupPlan Plan(WordHit hit, string preferredCjk, string nativeLanguage, DictionarySettings ds, string? language = null)
    {
        var lang = language ?? Languages.Detect(hit, preferredCjk);
        var target = Languages.TargetFor(lang, nativeLanguage);
        var (seed, candidates) = Seed(hit, lang);

        var phrase = candidates.Count > 0 && candidates[0].Contains(' ');
        var sections = ds.ShowInPopup || ds.HintAi
            ? dictionaries.Lookup(lang, candidates, seed.Reading, ds.EntriesPerDictionary + (phrase ? 1 : 0))
            : [];
        seed = seed with { Level = LevelOf(lang, seed, sections) };

        var hint = ds.HintAi ? DictionaryService.Hint(sections, target) : null;
        if (hint is null && lang == "zh") hint = chinese()?.SensesOf(seed.DictionaryForm ?? hit.Word);
        return new LookupPlan(hit, lang, target, seed, candidates, sections, hint);
    }

    /// <summary>Articles for the dictionary form the AI named, when the form on screen found nothing.</summary>
    public IReadOnlyList<DictSection> LookupLemma(string language, string lemma, string? reading, int perPack) =>
        dictionaries.Lookup(language, [lemma], reading, perPack);

    /// <summary>
    /// Fields known without AI (Japanese dictionary form and reading from UniDic, Chinese pinyin from CC-CEDICT)
    /// and the forms to look up offline, best first.
    /// </summary>
    private (WordCard Seed, IReadOnlyList<string> Candidates) Seed(WordHit hit, string lang)
    {
        var seed = new WordCard { Word = hit.Word, Language = lang };
        IReadOnlyList<string> candidates = [hit.Word];
        if (lang == "ja" && japanese() is { } ja && hit.ContextOffset >= 0)
        {
            var word = ja.WordAt(hit.Context, hit.ContextOffset, JapaneseExists);
            if (word is not null && word.DictionaryForm.Length > 0)
            {
                var hasKanji = word.DictionaryForm.Any(c => Scripts.Of(c) == Script.Han);
                seed = seed with { DictionaryForm = word.DictionaryForm, Reading = hasKanji ? word.Reading : null };
                candidates = word.Candidates;
            }
        }
        else if (lang == "zh" && chinese() is { } zh)
        {
            // The word or phrase was matched against dictionaries: the AI describes it rather than renaming it,
            // so pinyin, level and dictionary articles all belong to the same headword.
            seed = seed with { DictionaryForm = hit.Word, Reading = zh.PhrasePinyinOf(hit.Word) };
            // Traditional characters on screen: the simplified spelling is what БКРС is keyed by.
            candidates = new[] { hit.Word }.Concat(zh.Lookup(hit.Word).Select(e => e.Simplified)).Distinct().ToList();
        }
        else if (lang == "en")
        {
            candidates = EnglishPhrases.Candidates(hit);
        }
        return (seed, candidates);
    }

    private bool JapaneseExists(string term) => dictionaries.HasKey("ja", term);

    private bool ChineseExists(string term) => dictionaries.HasKey("zh", term) || chinese()?.Contains(term) == true;

    /// <summary>Listed level of the word, else of the headwords the dictionaries matched it to (abandoned → abandon).</summary>
    private string? LevelOf(string lang, WordCard seed, IReadOnlyList<DictSection> sections)
    {
        if (levels() is not { Available: true } lv) return null;
        var level = lv.LevelOf(lang, seed.DictionaryForm ?? seed.Word, seed.Reading);
        if (level is not null) return level;
        foreach (var e in sections.SelectMany(s => s.Entries).Take(4))
        {
            var headword = lang == "ja" && e.Headword.IndexOf('【') is var b and > 0 ? e.Headword[(b + 1)..].TrimEnd('】').Split('・')[0] : e.Headword;
            level = lv.LevelOf(lang, headword, e.Reading);
            if (level is not null) return level;
        }
        return null;
    }

    /// <summary>
    /// Japanese via UniDic tokens extended to the longest dictionary phrase, Chinese via CC-CEDICT segmentation
    /// extended the same way; a single character otherwise.
    /// </summary>
    private sealed class CjkMatcher(
        JapaneseAnalyzer? ja, ChineseDictionary? zh, string preferredCjk, Func<string, bool> jaExists, Func<string, bool> zhExists) : ITermMatcher
    {
        public (int Start, int Length) Match(string text, int index)
        {
            var isJapanese = Scripts.Dominant(text) == Script.Kana || preferredCjk == "ja";
            if (isJapanese && ja is not null)
                return ja.WordAt(text, index, jaExists) is { } w ? (w.Start, w.Length) : (index, 1);
            if (!isJapanese && zh is not null) return zh.MatchPhrase(text, index, zhExists);
            return (index, 1);
        }
    }
}
