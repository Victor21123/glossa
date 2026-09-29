using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.App.Theme;
using Glossa.Core.Lookup;
using Glossa.Core.Text;

namespace Glossa.App.Views;

/// <summary>State of the pop-up card; updated field by field while the AI streams.</summary>
public sealed class LookupViewModel : ObservableObject
{
    private string _headword = "";
    private string? _reading;
    private string _language = "en";
    private string? _level;
    private string? _partOfSpeech;
    private string? _usageNote;
    private string? _dictionaryMark;
    private bool _dictionaryMarkMatches;
    private string? _translation;
    private string? _definition;
    private string? _definitionTranslation;
    private string? _contextTranslation;
    private string? _synonyms;
    private string? _keyForms;
    private IReadOnlyList<CardComponent> _components = [];
    private IReadOnlyList<DictSectionItem> _dictionaries = [];
    private string _context = "";
    private int _contextOffset = -1;
    private int _wordLength;
    private string? _status;
    private string? _timing;
    private string? _error;
    private bool _isSaved;
    private bool _isBusy;
    private string? _message;
    private string _preset = "standard";
    private bool _expanded;
    private bool _hideTranslation;
    private bool _revealed;
    private bool _dictionariesOpen;
    private bool _translationOnly;
    private IReadOnlySet<string> _hidden = new HashSet<string>();
    private double? _width;

    public string Headword { get => _headword; set => SetProperty(ref _headword, value); }

    public string? Reading
    {
        get => _reading;
        set
        {
            if (!SetProperty(ref _reading, value)) return;
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(ShownReading));
        }
    }

    /// <summary>What the card shows: Show[part] in XAML. «Свой» hides some parts (see CustomCard.Hidden); presets hide none.</summary>
    public CardParts Show => new(_hidden);

    /// <summary>Card widths: the presets' own, or the one «Свой» sets for all of them.</summary>
    public double LessWidth => _width ?? 440;
    public double StandardWidth => _width ?? 560;
    public double MoreWidth => _width ?? 472;

    /// <summary>«Свой»: parts to hide and the width; null width and nothing hidden for the presets.</summary>
    public void SetLook(IEnumerable<string> hidden, double? width)
    {
        _hidden = hidden.ToHashSet();
        _width = width;
        OnPropertyChanged(string.Empty); // every part and width at once; happens only when settings change
    }

    private bool Shown(string part) => !_hidden.Contains(part);

    public string? ShownReading => Shown("reading") ? _reading : null;
    public string? ShownLevel => Shown("level") ? _level : null;
    public string? ShownPartOfSpeech => Shown("pos") ? _partOfSpeech : null;
    public string? ShownUsageNote => Shown("scene") ? _usageNote : null;
    public string? ShownDefinition => Shown("definition") ? _definition : null;
    public string? ShownKeyForms => Shown("forms") ? _keyForms : null;
    public string? ShownSynonyms => Shown("synonyms") ? _synonyms : null;
    public bool ShowComponents => HasComponents && Shown("components");
    public bool ShowLineRow => Shown("line") || Shown("lineTranslation");

    /// <summary>Language of the word on screen; picks the font so Han characters get Japanese or Chinese forms.</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value)) return;
            OnPropertyChanged(nameof(WordFont));
            OnPropertyChanged(nameof(PlateFont));
        }
    }

    public FontFamily WordFont => UiFonts.For(_language);

    /// <summary>The More card's nameplate is serif, except for Japanese and Chinese, which Literata does not cover.</summary>
    public FontFamily PlateFont => _language is "ja" or "zh" ? UiFonts.For(_language) : UiFonts.Serif;

    /// <summary>The Less card's second line after the word: "がったいする · N4 · гл. suru".</summary>
    public string Meta => string.Join(" · ", new[] { ShownReading, ShownLevel, ShownPartOfSpeech }.Where(x => !string.IsNullOrEmpty(x)));

    public string? Level
    {
        get => _level;
        set
        {
            if (!SetProperty(ref _level, value)) return;
            OnPropertyChanged(nameof(LevelScale));
            OnPropertyChanged(nameof(LevelValue));
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(ShownLevel));
        }
    }

    /// <summary>The list the level comes from, shown above it in the Standard card.</summary>
    public string? LevelScale => _level switch
    {
        null => null,
        _ when _level.StartsWith("HSK", StringComparison.Ordinal) => "HSK",
        _ when _level.Length == 2 && _level[0] == 'N' && char.IsDigit(_level[1]) => "JLPT",
        _ => "CEFR",
    };

    public string? LevelValue => _level is { } l && l.StartsWith("HSK ", StringComparison.Ordinal) ? l[4..] : _level;

    public string? PartOfSpeech
    {
        get => _partOfSpeech;
        set
        {
            if (!SetProperty(ref _partOfSpeech, value)) return;
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(ShownPartOfSpeech));
        }
    }

    public string? UsageNote
    {
        get => _usageNote;
        set
        {
            if (SetProperty(ref _usageNote, value)) OnPropertyChanged(nameof(ShownUsageNote));
        }
    }

    /// <summary>"✓ есть в словаре" / "нет в словаре — перевод по контексту".</summary>
    public string? DictionaryMark
    {
        get => _dictionaryMark;
        private set
        {
            if (SetProperty(ref _dictionaryMark, value)) OnPropertyChanged(nameof(DictionaryMarkShort));
        }
    }

    /// <summary>The Less card's footer has room for "✓ есть в словаре" / "нет в словаре" only.</summary>
    public string? DictionaryMarkShort => _dictionaryMark is null ? null : _dictionaryMarkMatches ? "✓ есть в словаре" : "нет в словаре";
    public bool DictionaryMarkMatches { get => _dictionaryMarkMatches; private set => SetProperty(ref _dictionaryMarkMatches, value); }

    public void SetDictionaryMark(bool matches)
    {
        DictionaryMarkMatches = matches;
        DictionaryMark = matches ? "✓ есть в словаре" : "нет в словаре — перевод по контексту";
    }

    public string? Translation { get => _translation; set => SetProperty(ref _translation, value); }
    public string? Definition
    {
        get => _definition;
        set
        {
            if (SetProperty(ref _definition, value)) OnPropertyChanged(nameof(ShownDefinition));
        }
    }

    /// <summary>The definition in the user's language under a monolingual one.</summary>
    public string? DefinitionTranslation { get => _definitionTranslation; set => SetProperty(ref _definitionTranslation, value); }
    public string? ContextTranslation { get => _contextTranslation; set => SetProperty(ref _contextTranslation, value); }
    public string? Synonyms
    {
        get => _synonyms;
        set
        {
            if (SetProperty(ref _synonyms, value)) OnPropertyChanged(nameof(ShownSynonyms));
        }
    }

    public string? KeyForms
    {
        get => _keyForms;
        set
        {
            if (SetProperty(ref _keyForms, value)) OnPropertyChanged(nameof(ShownKeyForms));
        }
    }

    public IReadOnlyList<CardComponent> Components
    {
        get => _components;
        set
        {
            if (!SetProperty(ref _components, value)) return;
            OnPropertyChanged(nameof(HasComponents));
            OnPropertyChanged(nameof(ShowComponents));
        }
    }

    public bool HasComponents => _components.Count > 0;

    /// <summary>Offline dictionary articles shown under the AI card.</summary>
    public IReadOnlyList<DictSectionItem> Dictionaries
    {
        get => _dictionaries;
        set
        {
            if (!SetProperty(ref _dictionaries, value)) return;
            OnPropertyChanged(nameof(HasDictionaries));
            OnPropertyChanged(nameof(DictionarySummary));
            OnPropertyChanged(nameof(ShowDictionaries));
        }
    }

    public bool HasDictionaries => _dictionaries.Count > 0;

    /// <summary>Footer line of the Standard card: how many articles are folded away, or that there are none.</summary>
    public string DictionarySummary
    {
        get
        {
            var n = _dictionaries.Sum(d => d.Entries.Count);
            if (n == 0) return "статьи нет";
            var word = (n % 10, n % 100) switch
            {
                (1, not 11) => "статья",
                (2 or 3 or 4, not (12 or 13 or 14)) => "статьи",
                _ => "статей",
            };
            return $"{n} {word} {(_dictionariesOpen ? "▾" : "▸")}";
        }
    }

    /// <summary>Standard folds the articles until clicked; More always shows them.</summary>
    public bool DictionariesOpen
    {
        get => _dictionariesOpen;
        set
        {
            if (!SetProperty(ref _dictionariesOpen, value)) return;
            OnPropertyChanged(nameof(DictionarySummary));
            OnPropertyChanged(nameof(ShowDictionaries));
        }
    }

    /// <summary>Open articles: always in More; in Standard when unfolded, or when «Свой» hides the footer that unfolds them.</summary>
    public bool ShowDictionaries => HasDictionaries && Shown("dictionaries") && (IsMore || _dictionariesOpen || !Shown("footer"));

    public string Context { get => _context; set => SetProperty(ref _context, value); }
    public int ContextOffset { get => _contextOffset; set => SetProperty(ref _contextOffset, value); }
    public int WordLength { get => _wordLength; set => SetProperty(ref _wordLength, value); }

    /// <summary>A passing note while the card fills ("Загружаю модель…", a missing voice).</summary>
    public string? Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(Footer));
        }
    }

    /// <summary>"ИИ 2,1 с · gemma26b" once the card is complete.</summary>
    public string? Timing
    {
        get => _timing;
        set
        {
            if (SetProperty(ref _timing, value)) OnPropertyChanged(nameof(Footer));
        }
    }

    public string? Footer => _status ?? _timing;

    public string? Error { get => _error; set => SetProperty(ref _error, value); }
    public bool IsSaved { get => _isSaved; set => SetProperty(ref _isSaved, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    /// <summary>A one-line notice instead of a card ("no text under the cursor").</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message is not null;

    /// <summary>less, standard or more (settings); Tab opens a Less card as Standard until it closes.</summary>
    public string Preset
    {
        get => _preset;
        set
        {
            if (SetProperty(ref _preset, value)) OnLayoutChanged();
        }
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (SetProperty(ref _expanded, value)) OnLayoutChanged();
        }
    }

    public bool IsLess => !_translationOnly && _preset == "less" && !_expanded;
    public bool IsStandard => !_translationOnly && (_preset == "standard" || (_preset == "less" && _expanded));
    public bool IsMore => !_translationOnly && _preset == "more";

    /// <summary>«Только перевод»: the card is just the line and its translation, whatever the preset.</summary>
    public bool IsTranslation => _translationOnly;

    /// <summary>The translation card's width: the Standard one, or «Свой».</summary>
    public double TranslationWidth => _width ?? 560;

    /// <summary>A paragraph to translate (Только перевод, «Реплика»): no word, no dictionaries, nothing to save.</summary>
    public void BeginTranslation(string text, string language)
    {
        Message = null;
        Error = null;
        Status = null;
        Timing = null;
        Language = language;
        Headword = "";
        Translation = null;
        ContextTranslation = null;
        Dictionaries = [];
        Context = text;
        ContextOffset = -1;
        WordLength = 0;
        IsSaved = false;
        _translationOnly = true;
        OnLayoutChanged();
        IsBusy = true;
    }

    /// <summary>Training mode: the translation waits for Space.</summary>
    public bool HideTranslation
    {
        get => _hideTranslation;
        set
        {
            if (SetProperty(ref _hideTranslation, value)) OnPropertyChanged(nameof(ShowTranslation));
        }
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (SetProperty(ref _revealed, value)) OnPropertyChanged(nameof(ShowTranslation));
        }
    }

    public bool ShowTranslation => !_hideTranslation || _revealed;

    public void Begin(WordHit hit, WordCard seed, string language, bool saved)
    {
        Message = null;
        Error = null;
        _translationOnly = false;
        OnLayoutChanged();
        Language = language;
        Headword = seed.DictionaryForm ?? hit.Word;
        Reading = seed.Reading;
        Level = null;
        PartOfSpeech = null;
        UsageNote = null;
        DictionaryMark = null;
        Translation = null;
        Definition = null;
        DefinitionTranslation = null;
        ContextTranslation = null;
        Synonyms = null;
        KeyForms = null;
        Components = [];
        Dictionaries = [];
        DictionariesOpen = false;
        Expanded = false;
        Revealed = false;
        Context = hit.Context;
        ContextOffset = hit.ContextOffset;
        WordLength = hit.Word.Length;
        IsSaved = saved;
        IsBusy = true;
        Status = null;
        Timing = null;
    }

    public void Apply(WordCard card)
    {
        Headword = card.DictionaryForm ?? card.Word;
        Reading = card.Reading;
        Level = card.Level;
        PartOfSpeech = card.PartOfSpeech;
        UsageNote = card.UsageNote;
        Translation = card.Translation;
        Definition = card.Definition;
        DefinitionTranslation = card.DefinitionTranslation;
        if (card.ContextTranslation is not null) ContextTranslation = card.ContextTranslation;
        Synonyms = card.Synonyms.Count > 0 ? string.Join(", ", card.Synonyms) : null;
        KeyForms = card.KeyForms.Count > 0 ? string.Join(" · ", card.KeyForms) : null;
        if (card.Components.Count > 0) Components = card.Components;
        if (card.Error is not null) Error = card.Error;
    }

    public void ShowMessage(string text)
    {
        Message = text;
        IsBusy = false;
    }

    private void OnLayoutChanged()
    {
        OnPropertyChanged(nameof(IsLess));
        OnPropertyChanged(nameof(IsStandard));
        OnPropertyChanged(nameof(IsMore));
        OnPropertyChanged(nameof(IsTranslation));
        OnPropertyChanged(nameof(ShowDictionaries));
    }
}

/// <summary>Show[part] for the card's XAML: false for a part «Свой» hides.</summary>
public sealed class CardParts(IReadOnlySet<string> hidden)
{
    public bool this[string part] => !hidden.Contains(part);
}

public sealed record DictEntryItem(string Headword, string? Reading, string Body);

public sealed record DictSectionItem(string Title, IReadOnlyList<DictEntryItem> Entries);
