using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.App.Theme;
using Glossa.App.Views;
using Glossa.Core.Config;
using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;

namespace Glossa.App.ViewModels;

/// <summary>
/// A word of the user's dictionary as the «Словарь» page shows it: the word itself plus the sentence being viewed.
/// Each sentence keeps its own «контекст сцены» (what the word means there and why), translation and frame.
/// </summary>
public sealed class WordEntry : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private int _index;
    private bool _isEditing;

    private readonly Func<string?, string?, string?>? _gameName;

    /// <param name="gameName">(program, title) → the game's name as its profile has it.</param>
    public WordEntry(SavedWord word, Func<string?, string?, string?>? gameName = null)
    {
        Word = word;
        _gameName = gameName;
    }

    public SavedWord Word { get; private set; }

    public string Headword => Word.Headword;
    public string? Reading => Word.Reading;
    public string? Translation => Word.Translation;
    public string? Definition => Word.Definition;
    public string? DefinitionTranslation => Word.DefinitionTranslation;
    public string? Explanation => Word.Explanation;
    public string? Synonyms => Word.Synonyms.Count > 0 ? string.Join(", ", Word.Synonyms) : null;
    public string? Level => Word.Level;
    public string? PartOfSpeech => Word.PartOfSpeech;
    public string? Register => Registers.RussianLabel(Word.Register);
    public string LanguageCode => Word.Language.ToUpperInvariant();
    public FontFamily WordFont => UiFonts.For(Word.Language);
    public bool Pinned => Word.Pinned;

    /// <summary>"прил. · B1" next to the word.</summary>
    public string Meta => string.Join(", ", new[] { Word.PartOfSpeech, Word.Level }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Every game the word was met in, newest first.</summary>
    public IReadOnlyList<string> Games =>
        Word.Contexts.Select(GameOf).Where(g => g is not null).Select(g => g!).Distinct().DefaultIfEmpty(GameOf(Word) ?? "").Where(g => g.Length > 0).ToList();

    /// <summary>The game of the newest sentence, for the list row.</summary>
    public string? Game => Word.Contexts.Count > 0 ? GameOf(Word.Contexts[0]) : GameOf(Word);

    public DateTime LastSeenLocal => Word.LastSeenUtc.ToLocalTime();

    /// <summary>List group: «Сегодня», «Вчера» or the date.</summary>
    public string DayGroup
    {
        get
        {
            var d = LastSeenLocal.Date;
            if (d == DateTime.Today) return "СЕГОДНЯ";
            if (d == DateTime.Today.AddDays(-1)) return "ВЧЕРА";
            var format = d.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy";
            return d.ToString(format, Ru).ToUpper(Ru);
        }
    }

    // ---- the sentence being viewed ----

    public WordContext? Current => Word.Contexts.Count == 0 ? null : Word.Contexts[Math.Clamp(_index, 0, Word.Contexts.Count - 1)];
    public int ContextCount => Word.Contexts.Count;
    public bool HasManyContexts => ContextCount > 1;
    public string Pager => $"реплика {_index + 1} из {ContextCount}";

    /// <summary>The translation chosen for this sentence, when it differs from the word's own.</summary>
    public string? HereTranslation => Current?.Translation is { } t && !string.Equals(t, Word.Translation, StringComparison.OrdinalIgnoreCase) ? t : null;

    public string? UsageNote => Current?.UsageNote ?? Word.UsageNote;
    public string? ContextText => Current?.Context ?? Word.Context;
    public int ContextOffset => Current?.ContextOffset ?? Word.ContextOffset;
    public int SurfaceLength => (Current?.Surface ?? Word.Word).Length;
    public string? ContextTranslation => Current?.ContextTranslation ?? Word.ContextTranslation;
    public string? ShotPath => (Current?.ShotFile ?? Word.ShotFile) is { } f ? Path.Combine(DataPaths.Root, f) : null;
    public PixelRect? WordBox => Current?.WordBox ?? Word.WordBox;

    /// <summary>"Disco Elysium · сегодня, 14:32" on the frame.</summary>
    public string SeenText
    {
        get
        {
            var at = (Current?.CreatedUtc ?? Word.CreatedUtc).ToLocalTime();
            var day = at.Date == DateTime.Today ? "сегодня"
                : at.Date == DateTime.Today.AddDays(-1) ? "вчера"
                : at.ToString(at.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Ru);
            var game = Current is { } c ? GameOf(c) : GameOf(Word);
            return (game is null ? "" : game + ", ") + day + at.ToString(", HH:mm", Ru);
        }
    }

    /// <summary>"Disco Elysium, Hades II · искал 3 раза".</summary>
    public string SourceText
    {
        get
        {
            var n = Word.Lookups;
            var times = (n % 10, n % 100) switch
            {
                (2 or 3 or 4, not (12 or 13 or 14)) => "раза",
                _ => "раз",
            };
            var games = Games.Count > 0 ? string.Join(", ", Games) + " - " : "";
            return $"{games}искал {n} {times}";
        }
    }

    public void Step(int delta)
    {
        if (ContextCount < 2) return;
        _index = ((_index + delta) % ContextCount + ContextCount) % ContextCount;
        OnPropertyChanged(string.Empty);
    }

    // ---- editing in place ----

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value)) OnPropertyChanged(nameof(IsViewing));
        }
    }

    public bool IsViewing => !_isEditing;

    public string EditHeadword { get; set; } = "";
    public string EditReading { get; set; } = "";
    public string EditTranslation { get; set; } = "";
    public string EditHere { get; set; } = "";
    public string EditUsage { get; set; } = "";
    public string EditDefinition { get; set; } = "";
    public string EditDefinitionTranslation { get; set; } = "";
    public string EditContextTranslation { get; set; } = "";
    public string EditSynonyms { get; set; } = "";
    public string EditLevel { get; set; } = "";
    public string EditPartOfSpeech { get; set; } = "";
    /// <summary>Register code; "" is «без пометы» (a ComboBox cannot select a null value).</summary>
    public string EditRegister { get; set; } = "";

    public void BeginEdit()
    {
        EditHeadword = Word.Headword;
        EditReading = Word.Reading ?? "";
        EditTranslation = Word.Translation ?? "";
        EditHere = Current?.Translation ?? "";
        EditUsage = UsageNote ?? "";
        EditDefinition = Word.Definition ?? "";
        EditDefinitionTranslation = Word.DefinitionTranslation ?? "";
        EditContextTranslation = ContextTranslation ?? "";
        EditSynonyms = Synonyms ?? "";
        EditLevel = Word.Level ?? "";
        EditPartOfSpeech = Word.PartOfSpeech ?? "";
        EditRegister = Word.Register ?? "";
        OnPropertyChanged(string.Empty);
        IsEditing = true;
    }

    public (SavedWord Word, WordContext? Context) Edited() => (
        Word with
        {
            DictionaryForm = string.IsNullOrWhiteSpace(EditHeadword) ? Word.DictionaryForm : EditHeadword.Trim(),
            Reading = Blank(EditReading),
            Translation = Blank(EditTranslation),
            Definition = Blank(EditDefinition),
            DefinitionTranslation = Blank(EditDefinitionTranslation),
            Level = Blank(EditLevel),
            PartOfSpeech = Blank(EditPartOfSpeech),
            Register = EditRegister.Length == 0 ? null : EditRegister,
            Synonyms = EditSynonyms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        },
        Current is { } c
            ? c with { Translation = Blank(EditHere), UsageNote = Blank(EditUsage), ContextTranslation = Blank(EditContextTranslation) }
            : null);

    /// <summary>Takes the stored version back after a save, keeping the sentence in view.</summary>
    public void Accept(SavedWord word)
    {
        var current = Current?.Id;
        Word = word;
        var i = word.Contexts.ToList().FindIndex(c => c.Id == current);
        _index = Math.Max(0, i);
        IsEditing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The game as its profile names it (Игры и профили), else the window title or program.</summary>
    private string? GameOf(WordContext c) => _gameName?.Invoke(c.AppExe, c.WindowTitle) ?? Blank(c.WindowTitle) ?? Blank(c.AppExe);
    private string? GameOf(SavedWord w) => _gameName?.Invoke(w.AppExe, w.WindowTitle) ?? Blank(w.WindowTitle) ?? Blank(w.AppExe);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>One entry of the catalogue on the left: a slice of the dictionary.</summary>
public sealed class SidebarItem(string key, string name, int count, Func<WordEntry, bool> matches, string? code = null, string? icon = null)
    : ObservableObject
{
    public const string PinIcon = "M9,4 H15 L14,10 L17,13 H7 L10,10 Z M12,16 V21";
    public const string ManualIcon = "M4,7 H20 V20 H4 Z M7,4 H17";
    public const string SmartIcon = "M4,5 H20 L14,12 V18 L10,20 V12 Z";
    public const string AddIcon = "M12,5 V19 M5,12 H19";

    private bool _isSelected;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Count { get; } = count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "";
    public string? Code { get; } = code;

    /// <summary>Stroked 24×24 icon path: pin, manual (box) or smart (funnel) collection, add.</summary>
    public string? Icon { get; } = icon;

    public Func<WordEntry, bool> Matches { get; } = matches;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record SidebarGroup(string Title, IReadOnlyList<SidebarItem> Items);

public sealed record RegisterChoice(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The «Словарь» page: only words the user looked up or added, never the downloaded dictionaries.</summary>
public sealed class LibraryViewModel : ObservableObject
{
    private const int GamesShown = 6;

    private readonly AppServices _services;
    private string _search = "";
    private string _sort = "Сначала новые";
    private WordEntry? _selected;
    private SidebarItem? _scope;
    private string _scopeKey = "all";
    private bool _allGames;
    private string? _status;
    private bool _busy;
    private IReadOnlyList<DictSectionItem> _articles = [];
    private IReadOnlyList<WordCollection> _collections = [];

    public LibraryViewModel(AppServices services)
    {
        _services = services;
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WordEntry.DayGroup)));
        View.Filter = o => o is WordEntry w && (_scope?.Matches(w) ?? true) && Matches(w, _search);
        Reload();
    }

    public ObservableCollection<WordEntry> Items { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<SidebarGroup> Sidebar { get; } = [];

    public static IReadOnlyList<string> Sorts { get; } = ["Сначала новые", "Сначала старые", "По алфавиту", "Чаще искал"];

    public static IReadOnlyList<RegisterChoice> Registers { get; } =
    [
        new("", "без пометы"), new("informal", "разг."), new("slang", "сленг"), new("rude", "грубо"), new("vulgar", "мат"), new("sexual", "18+"),
    ];

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Refresh();
        }
    }

    public string Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value)) Reload();
        }
    }

    public WordEntry? Selected
    {
        get => _selected;
        set
        {
            if (_selected is { IsEditing: true } editing && !ReferenceEquals(editing, value)) editing.IsEditing = false;
            if (!SetProperty(ref _selected, value)) return;
            LoadArticles();
            OnPropertyChanged(nameof(SelectedCollections));
        }
    }

    /// <summary>Articles of the downloaded dictionaries for the selected word (shown, never added to the library).</summary>
    public IReadOnlyList<DictSectionItem> Articles
    {
        get => _articles;
        private set
        {
            if (SetProperty(ref _articles, value)) OnPropertyChanged(nameof(HasArticles));
        }
    }

    public bool HasArticles => _articles.Count > 0;

    public string? Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }
    public string Summary => $"{View.Cast<object>().Count()} из {Items.Count}";

    public void Reload()
    {
        var selectedId = Selected?.Word.Id;
        _collections = _services.Library.Collections();
        OnPropertyChanged(nameof(ManualCollections));
        var words = _services.Library.List();
        IEnumerable<SavedWord> ordered = _sort switch
        {
            "Сначала старые" => words.OrderBy(w => w.LastSeenUtc),
            "По алфавиту" => words.OrderBy(w => w.Headword, StringComparer.CurrentCultureIgnoreCase),
            "Чаще искал" => words.OrderByDescending(w => w.Lookups).ThenByDescending(w => w.LastSeenUtc),
            _ => words,
        };
        Items.Clear();
        foreach (var w in ordered) Items.Add(new WordEntry(w, GameName));
        using (View.DeferRefresh())
        {
            View.GroupDescriptions.Clear();
            if (_sort is "Сначала новые" or "Сначала старые") View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WordEntry.DayGroup)));
        }
        BuildSidebar();
        Selected = Items.FirstOrDefault(i => i.Word.Id == selectedId) ?? View.Cast<WordEntry>().FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Asks the window to open the collection dialog (a new collection or an existing one).</summary>
    public event Action<WordCollection?>? CollectionDialogRequested;

    public void SelectScope(SidebarItem item)
    {
        if (item.Key == "collection:new")
        {
            CollectionDialogRequested?.Invoke(null);
            return;
        }
        if (item.Key == "games:more")
        {
            _allGames = !_allGames;
            BuildSidebar();
            return;
        }
        _scopeKey = item.Key;
        foreach (var i in Sidebar.SelectMany(g => g.Items)) i.IsSelected = i.Key == _scopeKey;
        _scope = item;
        Refresh();
        if (Selected is null || !View.Contains(Selected)) Selected = View.Cast<WordEntry>().FirstOrDefault();
    }

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(Summary));
    }

    private void BuildSidebar()
    {
        var all = Items.ToList();
        var groups = new List<SidebarGroup>
        {
            new("СЛОВАРЬ",
            [
                new SidebarItem("all", "Все слова", all.Count, _ => true),
                new SidebarItem("today", "Сегодня", all.Count(w => w.LastSeenLocal.Date == DateTime.Today), w => w.LastSeenLocal.Date == DateTime.Today),
                new SidebarItem("pinned", "Не могу запомнить", all.Count(w => w.Pinned), w => w.Pinned, icon: SidebarItem.PinIcon),
            ]),
        };

        var collections = _collections.Select(c =>
        {
            Func<WordEntry, bool> matches = c.Filter is { } f ? w => f.Matches(w.Word, GameName) : w => w.Word.CollectionIds.Contains(c.Id);
            return new SidebarItem("col:" + c.Id, c.Name, all.Count(matches), matches,
                icon: c.IsSmart ? SidebarItem.SmartIcon : SidebarItem.ManualIcon);
        }).ToList();
        collections.Add(new SidebarItem("collection:new", "Новая коллекция", 0, _ => true, icon: SidebarItem.AddIcon));
        groups.Add(new SidebarGroup("КОЛЛЕКЦИИ", collections));

        var languages = all.GroupBy(w => w.Word.Language).OrderByDescending(g => g.Count())
            .Select(g => new SidebarItem("lang:" + g.Key, Languages.RussianName(g.Key) is { Length: > 0 } n ? Capitalize(n) : g.Key.ToUpperInvariant(),
                g.Count(), w => w.Word.Language == g.Key, code: g.Key.ToUpperInvariant()))
            .ToList();
        if (languages.Count > 1) groups.Add(new SidebarGroup("ЯЗЫКИ", languages));

        var games = all.SelectMany(w => w.Games.Select(g => (Game: g, Word: w))).GroupBy(x => x.Game)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        if (games.Count > 0)
        {
            var items = games.Take(_allGames ? int.MaxValue : GamesShown)
                .Select(g => new SidebarItem("game:" + g.Key, g.Key, g.Count(), w => w.Games.Contains(g.Key)))
                .ToList();
            if (games.Count > GamesShown)
            {
                var rest = games.Skip(GamesShown).ToList();
                items.Add(_allGames
                    ? new SidebarItem("games:more", "Свернуть", 0, _ => true)
                    : new SidebarItem("games:more", $"Ещё {rest.Count} {Plural(rest.Count, "игра", "игры", "игр")}", rest.Sum(g => g.Count()), _ => true));
            }
            groups.Add(new SidebarGroup("ИГРЫ", items));
        }

        Sidebar.Clear();
        foreach (var g in groups) Sidebar.Add(g);
        var scope = groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == _scopeKey) ?? groups[0].Items[0];
        _scopeKey = scope.Key;
        scope.IsSelected = true;
        _scope = scope;
        Refresh();
    }

    /// <summary>Free text across fields plus prefixes: app:, lang:, cefr: / level:.</summary>
    internal static bool Matches(WordEntry w, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            bool ok;
            if (t.StartsWith("app:")) ok = w.Games.Any(g => Has(g, t[4..])) || Has(w.Word.AppExe, t[4..]);
            else if (t.StartsWith("lang:")) ok = w.Word.Language.Equals(t[5..], StringComparison.OrdinalIgnoreCase);
            else if (t.StartsWith("cefr:") || t.StartsWith("level:")) ok = Has(w.Level, t[(t.IndexOf(':') + 1)..]);
            else ok = Has(w.Word.Word, t) || Has(w.Headword, t) || Has(w.Translation, t) || Has(w.Definition, t) || Has(w.Reading, t)
                      || w.Word.Contexts.Any(c => Has(c.Context, t) || Has(c.ContextTranslation, t) || Has(c.Translation, t));
            if (!ok) return false;
        }
        return true;

        static bool Has(string? field, string value) => field?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void LoadArticles()
    {
        if (Selected is not { } w)
        {
            Articles = [];
            return;
        }
        var ds = _services.Settings.Dictionaries;
        var candidates = new[] { w.Headword, w.Word.Word }.Distinct().ToList();
        var sections = _services.Dictionaries.Lookup(w.Word.Language, candidates, w.Reading, Math.Max(1, ds.EntriesPerDictionary));
        Articles = sections.Select(s => new DictSectionItem(s.Pack.Title,
            s.Entries.Select(e => new DictEntryItem(e.Headword, e.Reading == e.Headword ? null : e.Reading, e.Body)).ToList())).ToList();
    }

    // ---- collections ----

    /// <summary>Collections a word can be put into by hand (smart ones fill themselves).</summary>
    public IReadOnlyList<WordCollection> ManualCollections => _collections.Where(c => !c.IsSmart).ToList();

    /// <summary>The manual collections the selected word is in.</summary>
    public IReadOnlyList<WordCollection> SelectedCollections =>
        Selected is { } w ? _collections.Where(c => !c.IsSmart && w.Word.CollectionIds.Contains(c.Id)).ToList() : [];

    public WordCollection? CollectionOf(SidebarItem item) =>
        item.Key.StartsWith("col:", StringComparison.Ordinal) ? _collections.FirstOrDefault(c => c.Id == item.Key[4..]) : null;

    /// <summary>Games and languages the dictionary already has, for the smart collection's choices.</summary>
    public IReadOnlyList<string> KnownGames => Items.SelectMany(w => w.Games).Distinct().OrderBy(g => g).ToList();
    public IReadOnlyList<string> KnownLanguages => Items.Select(w => w.Word.Language).Distinct().OrderBy(l => l).ToList();

    public int CountMatching(SmartFilter filter) => Items.Count(w => filter.Matches(w.Word, GameName));

    /// <summary>A game's name as its profile has it (null without a profile: the entry falls back to title or program).</summary>
    private string? GameName(string? exe, string? title) => _services.Games is { } g && !string.IsNullOrWhiteSpace(exe) ? g.DisplayName(exe, title) : null;

    /// <summary>The catalogue scoped to one game (from its profile's «Открыть слова в словаре»).</summary>
    public void ShowGame(string game)
    {
        _allGames = true;
        _scopeKey = "game:" + game;
        Reload();
    }

    public string SaveCollection(string? id, string name, SmartFilter? filter)
    {
        if (id is null) id = _services.Library.CreateCollection(name, filter);
        else
        {
            _services.Library.RenameCollection(id, name);
            if (filter is not null) _services.Library.UpdateFilter(id, filter);
        }
        Reload();
        return id;
    }

    public void DeleteCollection(string id)
    {
        _services.Library.DeleteCollection(id);
        if (_scopeKey == "col:" + id) _scopeKey = "all";
        Reload();
    }

    public void AddToCollection(string collectionId, IEnumerable<WordEntry> entries)
    {
        foreach (var e in entries) _services.Library.AddToCollection(collectionId, e.Word.Id);
        Reload();
        Status = "Добавлено в коллекцию \"" + _collections.FirstOrDefault(c => c.Id == collectionId)?.Name + "\"";
    }

    public void RemoveFromCollection(string collectionId, WordEntry entry)
    {
        _services.Library.RemoveFromCollection(collectionId, entry.Word.Id);
        Reload();
    }

    // ---- «Новое слово» ----

    /// <summary>
    /// Adds a word typed in by hand: dictionaries at once, the AI card after. The word shows up selected, and goes
    /// into <paramref name="collectionId"/> when one is given.
    /// </summary>
    public async Task AddWordAsync(NewWord input, string? collectionId)
    {
        if (_services.AddWord is not { } add || string.IsNullOrWhiteSpace(input.Word)) return;
        Status = $"Добавляю \"{input.Word.Trim()}\"...";
        try
        {
            var id = await add(input, CancellationToken.None);
            if (collectionId is not null) _services.Library.AddToCollection(collectionId, id);
            Reload();
            Selected = Items.FirstOrDefault(i => i.Word.Id == id) ?? Selected;
            Status = $"\"{Selected?.Headword}\" в словаре";
        }
        catch (Exception ex)
        {
            _services.Log.Error("Add word failed", ex);
            Status = "Не удалось добавить: " + ex.Message;
        }
    }

    /// <summary>Articles for the word being typed in the «Новое слово» dialog.</summary>
    public IReadOnlyList<DictSectionItem> Preview(string word, string? language)
    {
        word = word.Trim();
        if (word.Length == 0) return [];
        var sections = _services.Dictionaries.Lookup(language ?? GuessLanguage(word), [word], null, 1);
        return sections.Select(s => new DictSectionItem(s.Pack.Title,
            s.Entries.Select(e => new DictEntryItem(e.Headword, e.Reading == e.Headword ? null : e.Reading, e.Body)).ToList())).ToList();
    }

    /// <summary>Kana means Japanese; Han alone follows the «иероглифы без каны» setting; anything else is English.</summary>
    public string GuessLanguage(string word)
    {
        if (word.Any(c => c is >= '\u3040' and <= '\u30FF')) return "ja";
        if (word.Any(c => c is >= '\u4E00' and <= '\u9FFF')) return _services.Settings.PreferredCjk;
        return "en";
    }

    // ---- actions ----

    public void BeginEdit() => Selected?.BeginEdit();

    public void CancelEdit()
    {
        if (Selected is { } w) w.IsEditing = false;
    }

    public void SaveEdit()
    {
        if (Selected is not { IsEditing: true } entry) return;
        var (word, context) = entry.Edited();
        _services.Library.Update(word, context);
        var stored = _services.Library.List().FirstOrDefault(w => w.Id == word.Id);
        if (stored is not null) entry.Accept(stored);
        LoadArticles();
        Status = "Изменения сохранены";
    }

    public void TogglePin()
    {
        if (Selected is not { } entry) return;
        _services.Library.SetPinned(entry.Word.Id, !entry.Pinned);
        if (_services.Library.List().FirstOrDefault(w => w.Id == entry.Word.Id) is { } stored) entry.Accept(stored);
        BuildSidebar();
    }

    public void Step(int delta) => Selected?.Step(delta);

    public void Delete(IReadOnlyList<WordEntry> entries)
    {
        foreach (var e in entries)
        {
            _services.Library.Delete(e.Word.Id);
            Items.Remove(e);
        }
        BuildSidebar();
        Selected = View.Cast<WordEntry>().FirstOrDefault();
        Status = entries.Count == 1 ? "Слово удалено" : $"Удалено слов: {entries.Count}";
    }

    public async Task SpeakAsync()
    {
        if (Selected is not { } w) return;
        if (!await _services.Speech.SpeakAsync(w.Headword, w.Word.Language))
            Status = $"Нет голоса для языка \"{Languages.RussianName(w.Word.Language)}\" - Параметры -> Время и язык -> Речь.";
    }

    private IReadOnlyList<SavedWord> Visible() => View.Cast<WordEntry>().Select(i => i.Word).ToList();

    private AnkiExportOptions AnkiOptions()
    {
        var a = _services.Settings.Anki;
        Func<SavedWord, byte[]?>? audio = a.IncludeAudio
            ? w => _services.Speech.WavAsync(w.Headword, w.Language).GetAwaiter().GetResult()
            : null;
        return new AnkiExportOptions(a.ReverseCards, a.IncludeImages, audio);
    }

    public async Task ExportApkg(string path)
    {
        var words = Visible();
        var options = AnkiOptions();
        await Run(async p =>
        {
            await Task.Run(() => ApkgWriter.Write(path, DataPaths.Root, words, options, p));
            return $"Колода сохранена: {words.Count} слов -> {path}. Откройте файл двойным щелчком, Anki импортирует его.";
        });
    }

    public Task ExportQuizlet(string path) => Run(_ =>
    {
        TextExport.WriteQuizlet(path, Visible());
        return Task.FromResult($"Набор для Quizlet сохранён: {path}. В Quizlet: \"Импорт\" -> вставьте текст файла.");
    });

    public Task ExportCsv(string path) => Run(_ =>
    {
        TextExport.WriteCsv(path, Visible());
        return Task.FromResult($"CSV сохранён: {path}");
    });

    public Task SyncAnki() => Run(async p =>
    {
        var sync = new AnkiConnectSync(_services.LocalHttp);
        if (!await sync.IsAvailableAsync(CancellationToken.None))
            return "Anki не отвечает. Запустите Anki и установите дополнение AnkiConnect (код 2055492159).";
        var words = _services.Library.List();
        var removed = _services.Library.ListDeleted();
        var options = AnkiOptions();
        var r = await Task.Run(() => sync.SyncAsync(DataPaths.Root, words, removed, options, p, CancellationToken.None));
        return $"Anki: добавлено {r.Added}, обновлено {r.Updated}, помечено удалёнными {r.Tagged}.";
    });

    private async Task Run(Func<IProgress<string>, Task<string>> work)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Status = await work(new Progress<string>(s => Status = s));
        }
        catch (Exception ex)
        {
            _services.Log.Error("Export failed", ex);
            Status = "Ошибка: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    private static string Plural(int n, string one, string few, string many) => (n % 10, n % 100) switch
    {
        (1, not 11) => one,
        (2 or 3 or 4, not (12 or 13 or 14)) => few,
        _ => many,
    };
}
