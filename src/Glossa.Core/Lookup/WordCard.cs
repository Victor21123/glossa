using System.Text.RegularExpressions;
using Glossa.Core.Text;

namespace Glossa.Core.Lookup;

public sealed record CardComponent(string Part, string? Reading, string Meaning);

/// <summary>
/// Everything the pop-up shows for one word. Filled in two passes: deterministic data
/// (analyzers, dictionaries) first, then the context-dependent AI fields as they stream in.
/// </summary>
public sealed record WordCard
{
    public required string Word { get; init; }
    public required string Language { get; init; }
    public string? DictionaryForm { get; init; }
    public string? Reading { get; init; }
    public string? PartOfSpeech { get; init; }
    public string? Level { get; init; }
    public string? Definition { get; init; }

    /// <summary>The definition in the user's language when <see cref="Definition"/> is in the word's own (English).</summary>
    public string? DefinitionTranslation { get; init; }

    public string? Translation { get; init; }
    public string? ContextTranslation { get; init; }
    public string? Explanation { get; init; }

    /// <summary>How the word is used here: neutral, informal, slang, rude, vulgar or sexual (see <see cref="Registers"/>).</summary>
    public string? Register { get; init; }

    /// <summary>What the word expresses in this text, in a few words ("восторг", "оскорбление", "флирт").</summary>
    public string? UsageNote { get; init; }

    public IReadOnlyList<string> Synonyms { get; init; } = [];
    public IReadOnlyList<string> KeyForms { get; init; } = [];
    public IReadOnlyList<CardComponent> Components { get; init; } = [];

    /// <summary>Set while the AI part is still streaming.</summary>
    public bool IsPartial { get; init; }

    /// <summary>User-facing error from the AI step, if it failed.</summary>
    public string? Error { get; init; }
}

/// <summary>Register labels: the AI answers with a fixed English value, the user sees a short Russian label.</summary>
/// <summary>
/// A word's level on its language's list: JLPT for Japanese ("JLPT N3"), HSK for Chinese ("HSK 4", "HSK 7-9"), CEFR
/// for the rest ("B2"). The lists give the full form; the AI may answer "N3", or a CEFR level for a Japanese word.
/// </summary>
public static class WordLevels
{
    private static readonly Regex Jlpt = new(@"^(?:JLPT\s*)?N([1-5])$", RegexOptions.IgnoreCase);
    private static readonly Regex Hsk = new(@"^(?:HSK\s*)?([1-9](?:\s*[-–]\s*9)?)$", RegexOptions.IgnoreCase);
    private static readonly Regex Cefr = new(@"^(?:CEFR\s*)?([ABC][12])$", RegexOptions.IgnoreCase);

    /// <summary>The list and the level on it, or null when the level is not on the language's list.</summary>
    public static (string Scale, string Value)? Split(string? language, string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return null;
        var text = level.Trim();
        Match m;
        switch (language)
        {
            case "ja":
                m = Jlpt.Match(text);
                return m.Success ? ("JLPT", "N" + m.Groups[1].Value) : null;
            case "zh":
                m = Hsk.Match(text);
                return m.Success ? ("HSK", Regex.Replace(m.Groups[1].Value, @"\s*[-–]\s*", "-")) : null;
            default:
                m = Cefr.Match(text);
                return m.Success ? ("CEFR", m.Groups[1].Value.ToUpperInvariant()) : null;
        }
    }

    /// <summary>The level as stored: "JLPT N3", "HSK 4", "B2"; null when it is not on the language's list.</summary>
    public static string? Normalize(string? language, string? level) => Split(language, level) switch
    {
        null => null,
        ("CEFR", var value) => value,
        var (scale, value) => $"{scale} {value}",
    };

    /// <summary>The level in a line of text: "N3", "HSK 4", "B2".</summary>
    public static string? Short(string? language, string? level) => Split(language, level) switch
    {
        null => null,
        ("HSK", var value) => "HSK " + value,
        var (_, value) => value,
    };
}

public static class Registers
{
    public static readonly string[] All = ["neutral", "informal", "slang", "rude", "vulgar", "sexual"];

    public static string? Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() is { } v && All.Contains(v) ? v : null;

    /// <summary>Badge text; null for neutral words, which need no badge.</summary>
    public static string? RussianLabel(string? register) => register switch
    {
        "informal" => "разг.",
        "slang" => "сленг",
        "rude" => "грубо",
        "vulgar" => "мат",
        "sexual" => "18+",
        _ => null,
    };
}

public static class Languages
{
    public static string EnglishName(string code) => code switch
    {
        "ru" => "Russian",
        "en" => "English",
        "ja" => "Japanese",
        "zh" => "Chinese",
        "zh-Hant" => "Traditional Chinese",
        "ko" => "Korean",
        _ => code,
    };

    public static string RussianName(string code) => code switch
    {
        "ru" => "русский",
        "en" => "английский",
        "ja" => "японский",
        "zh" => "китайский",
        "zh-Hant" => "китайский (трад.)",
        "ko" => "корейский",
        _ => code,
    };

    /// <summary>
    /// Language of the word from its script and the surrounding text. Han-only words are Japanese when the
    /// context contains kana; otherwise the profile's preferred CJK language decides.
    /// </summary>
    public static string Detect(WordHit hit, string preferredCjk = "zh") => hit.Script switch
    {
        Script.Kana => "ja",
        Script.Han => Scripts.Dominant(hit.Context) == Script.Kana ? "ja" : preferredCjk,
        Script.Hangul => "ko",
        Script.Cyrillic => "ru",
        _ => "en",
    };

    /// <summary>The language of a whole line or block (screen translation), by its script, as <see cref="Detect"/> does for a word.</summary>
    public static string DetectText(string text, string preferredCjk = "zh") => Scripts.Dominant(text) switch
    {
        Script.Kana => "ja",
        Script.Han => text.Any(c => Scripts.Of(c) == Script.Kana) ? "ja" : preferredCjk,
        Script.Hangul => "ko",
        Script.Cyrillic => "ru",
        _ => "en",
    };

    /// <summary>Translations go to Russian, except Russian text which goes to English.</summary>
    public static string TargetFor(string source, string native = "ru") => source == native ? "en" : native;
}
