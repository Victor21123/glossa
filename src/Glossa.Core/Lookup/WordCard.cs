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

    /// <summary>Translations go to Russian, except Russian text which goes to English.</summary>
    public static string TargetFor(string source, string native = "ru") => source == native ? "en" : native;
}
