using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.App.Views;
using Glossa.Core.Lookup;

namespace Glossa.App.ViewModels;

/// <summary>An installed pack in card order.</summary>
public sealed class PackItem : ObservableObject
{
    private readonly Action<PackItem> _toggled;
    private bool _enabled;
    private int _number;

    public PackItem(PackInfo info, bool enabled, Action<PackItem> toggled)
    {
        Info = info;
        _enabled = enabled;
        _toggled = toggled;
    }

    public PackInfo Info { get; }
    public string Title => Info.Title;
    public int Number { get => _number; set => SetProperty(ref _number, value); }
    public string Pair => $"{Info.SourceLanguage} -> {Info.TargetLanguage}";
    public string Summary => $"{Pair}, {DictionariesViewModel.Count(Info.Entries)} статей, {DictionariesViewModel.Size(Info.SizeBytes)}";
    public bool IsImported => Info.Id.StartsWith("user-", StringComparison.Ordinal);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value)) _toggled(this);
        }
    }
}

/// <summary>A dictionary of the download catalog with its state: not installed, installed (on or off), downloading.</summary>
public sealed class CatalogEntry : ObservableObject
{
    private bool _installed;
    private bool _enabled = true;
    private bool _busy;
    private double _progress;
    private string? _status;

    public CatalogEntry(CatalogItem item) => Item = item;

    public CatalogItem Item { get; }
    public string Title => Item.Title;
    public string Description => Item.Description;
    public string Group => Item.IsLevels ? "УРОВНИ" : Languages.RussianName(Item.Language).ToUpperInvariant();
    public string Pair => Item.IsLevels ? "все языки" : Item.Language + " -> " + (Item.Id is "jmdict" ? "ru, en" : Item.Id is "cedict" or "wiktionary-en" or "wiktionary-ja" or "wiktionary-zh" ? "en" : "ru");
    public string Download => $"~{Item.DownloadMb} МБ";

    public bool Installed
    {
        get => _installed;
        set
        {
            if (SetProperty(ref _installed, value)) Changed();
        }
    }

    /// <summary>False when installed but switched off in «Установленные».</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value)) Changed();
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value)) Changed();
        }
    }

    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public string? Status { get => _status; set => SetProperty(ref _status, value); }

    public bool CanInstall => !Installed && !Busy;
    public bool ShowInstalled => Installed && !Busy;
    public string InstalledText => Enabled ? "установлен" : "установлен, выключен";
    public string ActionText => Installed ? $"Обновить ({Item.DownloadMb} МБ)" : $"Установить ({Item.DownloadMb} МБ)";

    private void Changed()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(InstalledText));
        OnPropertyChanged(nameof(ActionText));
    }
}

/// <summary>What the chosen dictionary file will be built into; the user may correct the name and languages.</summary>
public sealed class ImportDraft : ObservableObject
{
    private string _title;
    private string _source;
    private string _target;

    public ImportDraft(string path, PackMeta meta)
    {
        Path = path;
        Meta = meta;
        _title = meta.Title;
        _source = meta.SourceLanguage == "?" ? "" : meta.SourceLanguage;
        _target = meta.TargetLanguage == "?" ? "" : meta.TargetLanguage;
    }

    public string Path { get; }
    public PackMeta Meta { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeText => DictionariesViewModel.Size(new FileInfo(Path).Length);

    public string Format
    {
        get
        {
            var name = FileName.ToLowerInvariant();
            if (name.EndsWith(".dsl") || name.EndsWith(".dsl.dz") || name.EndsWith(".dsl.gz")) return "DSL (ABBYY Lingvo)";
            if (name.EndsWith(".ifo")) return "StarDict";
            if (name.EndsWith(".mdx")) return "MDX (MDict)";
            return name.EndsWith(".zip") ? "архив: Yomitan или словари внутри" : "неизвестный";
        }
    }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Source { get => _source; set => SetProperty(ref _source, value.Trim().ToLowerInvariant()); }
    public string Target { get => _target; set => SetProperty(ref _target, value.Trim().ToLowerInvariant()); }

    public PackMeta Edited => Meta with
    {
        Title = Title.Trim().Length > 0 ? Title.Trim() : Meta.Title,
        SourceLanguage = Source.Length > 0 ? Source : "?",
        TargetLanguage = Target.Length > 0 ? Target : "?",
    };
}

/// <summary>Настройки → Справочники: installed packs (order, on/off, removal), the download catalog and import of the user's files.</summary>
public sealed class DictionariesViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private string? _status;
    private bool _busy;
    private PackItem? _selectedPack;
    private CatalogEntry? _selectedEntry;
    private string _filter = "all";
    private string _search = "";
    private ImportDraft? _draft;
    private string? _importStatus;

    public DictionariesViewModel(AppServices services)
    {
        _services = services;
        foreach (var item in DictionaryCatalog.Items) Catalog.Add(new CatalogEntry(item));
        CatalogView = new ListCollectionView(Catalog);
        CatalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogEntry.Group)));
        CatalogView.Filter = o => o is CatalogEntry e && Matches(e);
        Refresh();
        SelectedPack = Packs.FirstOrDefault();
        SelectedEntry = Catalog.FirstOrDefault(c => !c.Installed) ?? Catalog.FirstOrDefault();
    }

    public ObservableCollection<PackItem> Packs { get; } = [];
    public ObservableCollection<CatalogEntry> Catalog { get; } = [];
    public ListCollectionView CatalogView { get; }
    public IEnumerable<PackItem> Imported => Packs.Where(p => p.IsImported);
    public bool HasImported => Packs.Any(p => p.IsImported);
    public bool HasPacks => Packs.Count > 0;

    public string? Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public PackItem? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (SetProperty(ref _selectedPack, value)) OnPropertyChanged(nameof(Details));
        }
    }

    /// <summary>Right-hand panel of «Установленные».</summary>
    public PackDetails? Details => _selectedPack is { } p ? new PackDetails(p.Info, DictionaryCatalog.Items.FirstOrDefault(i => i.Id == p.Info.Id)) : null;

    public CatalogEntry? SelectedEntry { get => _selectedEntry; set => SetProperty(ref _selectedEntry, value); }

    /// <summary>all, en, ja, zh or levels.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) CatalogView.Refresh();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) CatalogView.Refresh();
        }
    }

    public ImportDraft? Draft
    {
        get => _draft;
        private set
        {
            if (SetProperty(ref _draft, value)) OnPropertyChanged(nameof(HasDraft));
        }
    }

    public bool HasDraft => _draft is not null;
    public string? ImportStatus { get => _importStatus; set => SetProperty(ref _importStatus, value); }

    public string InstalledSummary =>
        $"{Packs.Count} {Plural(Packs.Count, "словарь", "словаря", "словарей")}, {Size(Packs.Sum(p => p.Info.SizeBytes))} на диске";

    public bool HintAi
    {
        get => _services.Settings.Dictionaries.HintAi;
        set { _services.Settings.Dictionaries.HintAi = value; Save(); }
    }

    public int EntriesPerDictionary
    {
        get => _services.Settings.Dictionaries.EntriesPerDictionary;
        set { _services.Settings.Dictionaries.EntriesPerDictionary = Math.Clamp(value, 1, 6); Save(); }
    }

    public bool KeepSources
    {
        get => _services.Settings.Dictionaries.KeepSources;
        set { _services.Settings.Dictionaries.KeepSources = value; Save(); }
    }

    public void Refresh()
    {
        var disabled = _services.Settings.Dictionaries.Disabled;
        var selected = _selectedPack?.Info.Id;
        Packs.Clear();
        foreach (var info in _services.Dictionaries.Installed)
            Packs.Add(new PackItem(info, !disabled.Contains(info.Id), OnToggled) { Number = Packs.Count + 1 });
        foreach (var c in Catalog)
        {
            c.Installed = DictionaryCatalog.IsInstalled(c.Item, DataPaths.Packs, DataPaths.Levels);
            c.Enabled = !disabled.Contains(c.Item.Id);
        }
        SelectedPack = Packs.FirstOrDefault(p => p.Info.Id == selected) ?? Packs.FirstOrDefault();
        OnPropertyChanged(nameof(InstalledSummary));
        OnPropertyChanged(nameof(Imported));
        OnPropertyChanged(nameof(HasImported));
        OnPropertyChanged(nameof(HasPacks));
        OnPropertyChanged(nameof(Details));
    }

    /// <summary>The selected pack's own articles for a word, as the card would show them.</summary>
    public IReadOnlyList<DictEntryItem> Check(string word)
    {
        if (_selectedPack is not { } p || string.IsNullOrWhiteSpace(word)) return [];
        var sections = _services.Dictionaries.Lookup(p.Info.SourceLanguage, [word.Trim()], null, 3);
        return sections.Where(s => s.Pack.Id == p.Info.Id)
            .SelectMany(s => s.Entries.Select(e => new DictEntryItem(e.Headword, e.Reading == e.Headword ? null : e.Reading, e.Body)))
            .ToList();
    }

    public async Task InstallAsync(CatalogEntry entry)
    {
        if (Busy) return;
        Busy = entry.Busy = true;
        var cts = _cts = new CancellationTokenSource();
        entry.Status = "Подготовка...";
        entry.Progress = 0;
        // Report on the UI thread; builders report from worker threads.
        var progress = new Progress<ImportProgress>(p =>
        {
            entry.Status = p.Stage;
            entry.Progress = p.Fraction * 100;
        });
        try
        {
            // A rebuilt pack replaces the file in use: close it first.
            if (!entry.Item.IsLevels) _services.Dictionaries.Close(entry.Item.Id);
            var ctx = new CatalogContext(_services.RemoteHttp, _services.DirectHttp, DataPaths.Sources, DataPaths.Packs,
                DataPaths.Work, DataPaths.Levels, DataPaths.Cedict, progress, cts.Token);
            await Task.Run(() => DictionaryCatalog.InstallAsync(entry.Item, ctx, KeepSources), cts.Token);
            entry.Status = null;
            Status = $"\"{entry.Title}\" установлен.";
            _services.Log.Info($"Dictionary installed: {entry.Item.Id}");
        }
        catch (OperationCanceledException)
        {
            entry.Status = "Отменено";
        }
        catch (Exception ex)
        {
            entry.Status = "Ошибка: " + ex.Message;
            _services.Log.Error($"Dictionary install failed: {entry.Item.Id}", ex);
        }
        finally
        {
            Busy = entry.Busy = false;
            _services.ReloadDictionaries();
            Refresh();
        }
    }

    /// <summary>Reads the file's header: format, title and languages, for the user to check before building.</summary>
    public void Prepare(string path)
    {
        ImportStatus = null;
        var meta = DictionaryImport.DefaultMeta(path);
        if (meta is null)
        {
            Draft = null;
            ImportStatus = "Формат не поддерживается: нужен DSL, StarDict (.ifo), MDX, Yomitan (.zip) или архив с ними.";
            return;
        }
        Draft = new ImportDraft(path, meta);
    }

    public void Discard()
    {
        Draft = null;
        ImportStatus = null;
    }

    public async Task ImportAsync()
    {
        if (Busy || _draft is not { } draft) return;
        Busy = true;
        var cts = _cts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p => ImportStatus = string.Format(Russian, "{0}: {1:P0}", p.Stage, p.Fraction));
        try
        {
            var meta = draft.Edited;
            _services.Dictionaries.Close(meta.Id);
            var info = await Task.Run(() => DictionaryImport.Import(draft.Path, DataPaths.Packs, DataPaths.Work, meta, progress, cts.Token), cts.Token);
            ImportStatus = $"Собран \"{info.Title}\": {Count(info.Entries)} статей, {info.SourceLanguage} -> {info.TargetLanguage}. Он уже в \"Установленных\".";
            Draft = null;
            _services.Log.Info($"Dictionary imported: {info.Id} from {draft.Path}");
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "Сборка отменена.";
        }
        catch (Exception ex)
        {
            ImportStatus = "Не удалось собрать: " + ex.Message;
            _services.Log.Error("Dictionary import failed: " + draft.Path, ex);
        }
        finally
        {
            Busy = false;
            _services.ReloadDictionaries();
            Refresh();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Move(PackItem item, int delta)
    {
        var i = Packs.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Packs.Count) return;
        Packs.Move(i, j);
        for (var k = 0; k < Packs.Count; k++) Packs[k].Number = k + 1;
        _services.Settings.Dictionaries.Order = Packs.Select(p => p.Info.Id).ToList();
        Save();
        _services.ReloadDictionaries();
    }

    public void Remove(PackItem item)
    {
        _services.Dictionaries.Remove(item.Info.Id);
        var d = _services.Settings.Dictionaries;
        d.Order.Remove(item.Info.Id);
        d.Disabled.Remove(item.Info.Id);
        Save();
        _services.ReloadDictionaries();
        Refresh();
        Status = $"\"{item.Title}\" удалён.";
    }

    private bool Matches(CatalogEntry e)
    {
        var byFilter = _filter switch
        {
            "levels" => e.Item.IsLevels,
            "en" or "ja" or "zh" => !e.Item.IsLevels && e.Item.Language == _filter,
            _ => true,
        };
        var q = _search.Trim();
        return byFilter && (q.Length == 0 || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnToggled(PackItem item)
    {
        var disabled = _services.Settings.Dictionaries.Disabled;
        disabled.Remove(item.Info.Id);
        if (!item.Enabled) disabled.Add(item.Info.Id);
        Save();
        _services.ReloadDictionaries();
        foreach (var c in Catalog.Where(c => c.Item.Id == item.Info.Id)) c.Enabled = item.Enabled;
    }

    private void Save() => _services.SaveSettings(_services.Settings);

    internal static string Count(long n) => n.ToString("N0", Russian);

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Russian, "{0:0.0} ГБ", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(Russian, "{0:0} МБ", bytes / (double)(1L << 20)),
        _ => string.Format(Russian, "{0:0} КБ", bytes / 1024.0),
    };

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
}

/// <summary>The selected installed pack as the detail panel shows it.</summary>
public sealed record PackDetails(PackInfo Info, CatalogItem? Source)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public string Title => Info.Title;
    public string Description => Source?.Description ?? Info.Description ?? "Словарь из твоего файла.";
    public string Entries => DictionariesViewModel.Count(Info.Entries);
    public string Size => DictionariesViewModel.Size(Info.SizeBytes);
    public string Pair => $"{Info.SourceLanguage} -> {Info.TargetLanguage}";
    public string Origin => Info.Origin is { Length: > 0 } o ? o.Replace("https://", "").Replace("http://", "").TrimEnd('/') : "-";
    public string Built => File.Exists(Info.Path) ? File.GetLastWriteTime(Info.Path).ToString("d MMMM yyyy", Russian) : "-";
    public string Folder => System.IO.Path.GetDirectoryName(Info.Path) ?? "";
    public string? License => Info.License;
    public bool CanUpdate => Source is not null;
}
