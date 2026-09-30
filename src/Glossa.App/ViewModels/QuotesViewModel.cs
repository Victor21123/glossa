using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;

namespace Glossa.App.ViewModels;

/// <summary>A quote as «Словарь» → «Цитаты» shows it.</summary>
public sealed class QuoteEntry(Quote quote, string? game) : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public Quote Quote { get; private set; } = quote;
    public string Text => Quote.Text;
    public string? Translation => Quote.Translation;
    public string LanguageCode => Quote.Language.ToUpperInvariant();
    public FontFamily TextFont => UiFonts.For(Quote.Language);
    public string? Game { get; private set; } = game;

    /// <summary>The same quote as the library has it now (seen again, its frame cleared): the entry, and its selection, stay.</summary>
    public void Update(Quote quote, string? game)
    {
        if (quote == Quote && game == Game) return;
        Quote = quote;
        Game = game;
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(DayGroup)); // live grouping listens for the property by name
    }

    public string? ShotPath => Quote.ShotFile is { } f ? Path.Combine(DataPaths.Root, f) : null;
    public bool HasShot => Quote.ShotFile is not null;
    public PixelRect? Box => Quote.Box;

    public DateTime SeenLocal => Quote.SeenUtc.ToLocalTime();

    /// <summary>List group: «СЕГОДНЯ», «ВЧЕРА» or the date, as the words'.</summary>
    public string DayGroup
    {
        get
        {
            var d = SeenLocal.Date;
            if (d == DateTime.Today) return "СЕГОДНЯ";
            if (d == DateTime.Today.AddDays(-1)) return "ВЧЕРА";
            return d.ToString(d.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Ru).ToUpper(Ru);
        }
    }

    public string SourceName => Quote.Source switch
    {
        QuoteSource.Zone => "Зона",
        QuoteSource.Screen => "Весь экран",
        QuoteSource.Live => "Живой перевод",
        _ => "Реплика",
    };

    /// <summary>"Night Harbor, сегодня, 14:32" on the frame.</summary>
    public string SeenText
    {
        get
        {
            var at = SeenLocal;
            var day = at.Date == DateTime.Today ? "сегодня"
                : at.Date == DateTime.Today.AddDays(-1) ? "вчера"
                : at.ToString(at.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Ru);
            return (Game is null ? "" : Game + ", ") + day + at.ToString(", HH:mm", Ru);
        }
    }

    /// <summary>"Реплика, встречалась 3 раза с 28 сентября".</summary>
    public string Details
    {
        get
        {
            var n = Quote.Seen;
            var times = n == 1 ? "один раз" : n + " " + ((n % 10, n % 100) switch
            {
                (2 or 3 or 4, not (12 or 13 or 14)) => "раза",
                _ => "раз",
            });
            var since = n > 1 ? " с " + Quote.CreatedUtc.ToLocalTime().ToString("d MMMM", Ru) : "";
            return $"{SourceName}, встречалась {times}{since}";
        }
    }
}

/// <summary>One entry of the quotes' catalogue: the properties the catalogue buttons show, as <see cref="SidebarItem"/>.</summary>
public sealed class QuoteScope(string key, string name, int count, Func<QuoteEntry, bool> matches, string? code = null) : ObservableObject
{
    private bool _isSelected;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Count { get; } = count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "";
    public string? Code { get; } = code;
    public string? Icon => null;
    public Func<QuoteEntry, bool> Matches { get; } = matches;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record QuoteScopeGroup(string Title, IReadOnlyList<QuoteScope> Items);

/// <summary>
/// «Словарь» → «Цитаты» (decided 2026-09-30): the lines translated in «Только перевод», with their frames when kept;
/// frames can go for the chosen quotes or all of them, the quotes stay.
/// </summary>
public sealed class QuotesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _search = "";
    private QuoteEntry? _selected;
    private QuoteScope? _scope;
    private string _scopeKey = "all";
    private string? _status;

    public QuotesViewModel(AppServices services)
    {
        _services = services;
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(QuoteEntry.DayGroup)));
        View.Filter = o => o is QuoteEntry q && (_scope?.Matches(q) ?? true) && Matches(q, _search);
        // A quote seen again today moves to «Сегодня» without a refresh (a refresh would drop a selection of several).
        if (View is ICollectionViewLiveShaping live)
        {
            live.IsLiveGrouping = true;
            live.LiveGroupingProperties.Add(nameof(QuoteEntry.DayGroup));
        }
        Reload();
    }

    public ObservableCollection<QuoteEntry> Items { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<QuoteScopeGroup> Sidebar { get; } = [];

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Refresh();
        }
    }

    public QuoteEntry? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public string? Status { get => _status; set => SetProperty(ref _status, value); }

    public string Summary => $"{View.Cast<object>().Count()} из {Items.Count}";

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Everything from the library as it is now, merged into the list in place: quotes kept beside a translation arrive
    /// while the user may have several chosen, and clearing the list would drop that choice (the next «Удалить» would
    /// then take the wrong quotes). Not under View.DeferRefresh: a grouped view refuses changes while it is deferred.
    /// </summary>
    public void Reload()
    {
        var games = _services.Games;
        var fresh = _services.Library.ListQuotes();
        var ids = fresh.Select(q => q.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!ids.Contains(Items[i].Quote.Id)) Items.RemoveAt(i);
        var known = Items.ToDictionary(e => e.Quote.Id);
        for (var i = 0; i < fresh.Count; i++)
        {
            var q = fresh[i];
            var game = (games is not null && !string.IsNullOrWhiteSpace(q.AppExe) ? games.DisplayName(q.AppExe, q.WindowTitle) : null)
                       ?? Blank(q.WindowTitle) ?? Blank(q.AppExe);
            if (known.TryGetValue(q.Id, out var entry))
            {
                entry.Update(q, game);
                var at = Items.IndexOf(entry);
                if (at != i) Items.Move(at, i);
            }
            else Items.Insert(i, new QuoteEntry(q, game));
        }
        BuildSidebar();
        OnPropertyChanged(nameof(Summary));
        if (Selected is null || !Items.Contains(Selected)) Selected = View.Cast<QuoteEntry>().FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void SelectScope(QuoteScope scope)
    {
        _scopeKey = scope.Key;
        foreach (var s in Sidebar.SelectMany(g => g.Items)) s.IsSelected = s == scope;
        _scope = scope;
        Refresh();
        Selected = View.Cast<QuoteEntry>().FirstOrDefault();
    }

    private void BuildSidebar()
    {
        Sidebar.Clear();
        var today = DateTime.Today;
        var main = new List<QuoteScope>
        {
            new("all", "Все цитаты", Items.Count, _ => true),
            new("today", "Сегодня", Items.Count(q => q.SeenLocal.Date == today), q => q.SeenLocal.Date == today),
            new("shots", "С кадром", Items.Count(q => q.HasShot), q => q.HasShot),
        };
        Sidebar.Add(new QuoteScopeGroup("ЦИТАТЫ", main));
        var languages = Items.GroupBy(q => q.Quote.Language).OrderByDescending(g => g.Count())
            .Select(g => new QuoteScope("lang:" + g.Key, char.ToUpper(Languages.RussianName(g.Key)[0]) + Languages.RussianName(g.Key)[1..], g.Count(),
                q => q.Quote.Language == g.Key, g.Key.ToUpperInvariant()))
            .ToList();
        if (languages.Count > 1) Sidebar.Add(new QuoteScopeGroup("ЯЗЫКИ", languages));
        var games = Items.Where(q => q.Game is not null).GroupBy(q => q.Game!).OrderByDescending(g => g.Count())
            .Select(g => new QuoteScope("game:" + g.Key, g.Key, g.Count(), q => q.Game == g.Key))
            .ToList();
        if (games.Count > 0) Sidebar.Add(new QuoteScopeGroup("ИГРЫ", games));
        _scope = Sidebar.SelectMany(g => g.Items).FirstOrDefault(s => s.Key == _scopeKey) ?? main[0];
        _scopeKey = _scope.Key;
        _scope.IsSelected = true;
    }

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The line, its translation or the game: every word of the search somewhere in them.</summary>
    private static bool Matches(QuoteEntry q, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var hay = string.Join('\n', q.Text, q.Translation, q.Game);
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => hay.Contains(t, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Removes the quotes; the library deletes the frames no other quote shows.</summary>
    public void Delete(IReadOnlyList<QuoteEntry> entries)
    {
        if (entries.Count == 0) return;
        _services.Library.DeleteQuotes(entries.Select(e => e.Quote.Id));
        Reload();
        Status = entries.Count == 1 ? "Цитата удалена" : $"Удалено цитат: {entries.Count}";
    }

    /// <summary>«Убрать кадр»: the frames of these quotes go (from the disk once no quote shows them); the quotes stay.</summary>
    public void ClearFrames(IReadOnlyList<QuoteEntry> entries)
    {
        var withShot = entries.Where(e => e.HasShot).ToList();
        if (withShot.Count == 0) return;
        _services.Library.ClearQuoteShots(withShot.Select(e => e.Quote.Id));
        Reload();
        Status = withShot.Count == 1 ? "Кадр цитаты убран" : $"Убрано кадров: {withShot.Count}";
    }

    public void ExportCsv(string path, IReadOnlyList<QuoteEntry> entries)
    {
        TextExport.WriteQuotesCsv(path, entries.Select(e => e.Quote));
        Status = $"Цитат выгружено: {entries.Count}";
    }

    public IReadOnlyList<QuoteEntry> Visible() => View.Cast<QuoteEntry>().ToList();

    public async Task SpeakAsync()
    {
        if (Selected is not { } q) return;
        if (!await _services.Speech.SpeakAsync(q.Text, q.Quote.Language))
            Status = $"Нет голоса для языка \"{Languages.RussianName(q.Quote.Language)}\" - Параметры -> Время и язык -> Речь.";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
