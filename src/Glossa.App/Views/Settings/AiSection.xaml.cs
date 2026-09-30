using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.App.Ai;
using Glossa.App.ViewModels;
using Glossa.Core.Config;
using Glossa.Core.Llm;

namespace Glossa.App.Views.Settings;

public partial class AiSection : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Providers offered under «Добавить»: kind, address, a model to start from, how JSON is enforced.</summary>
    private static readonly (string Name, LlmProviderKind Kind, string Url, string Model, StructuredOutputMode Mode)[] Presets =
    [
        ("Anthropic", LlmProviderKind.Anthropic, "https://api.anthropic.com", AnthropicLlmClient.SuggestedModels[0], StructuredOutputMode.JsonSchema),
        ("OpenAI", LlmProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "", StructuredOutputMode.JsonSchema),
        ("OpenRouter", LlmProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", "", StructuredOutputMode.JsonSchema),
        ("DeepSeek", LlmProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1", "deepseek-chat", StructuredOutputMode.JsonObject),
        ("Gemini", LlmProviderKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", "", StructuredOutputMode.JsonSchema),
        ("LM Studio", LlmProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1", "", StructuredOutputMode.JsonSchema),
        ("Ollama", LlmProviderKind.OpenAiCompatible, "http://127.0.0.1:11434/v1", "", StructuredOutputMode.JsonSchema),
    ];

    private readonly AppServices _services;
    private readonly SettingsViewModel _model;
    private readonly ObservableCollection<EndpointEditor> _endpoints = [];
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(3) };

    public AiSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        _tiles = ProfileTile.For(services);
        Profiles.ItemsSource = _tiles;
        foreach (var e in services.Settings.CustomEndpoints) _endpoints.Add(new EndpointEditor(e, services, model));
        EndpointList.ItemsSource = _endpoints;
        _endpoints.CollectionChanged += (_, _) => ShowEndpointState();
        ShowEndpointState();
        foreach (var (preset, i) in Presets.Select((p, i) => (p, i)))
        {
            var add = new Button { Content = preset.Name, Margin = new Thickness(0, 0, 8, 8), Tag = i };
            add.Click += OnAddEndpoint;
            Providers.Children.Add(add);
        }
        AddFileFields();
        ShowState();
        ShowRuntime();
        ShowEyes();

        _status.Tick += (_, _) =>
        {
            ShowState();
            ShowEyes();
        };
        Action<string> downloadChanged = key =>
        {
            if (key == ModelDownloads.Runtime) ShowRuntime();
            else if (key == ModelCatalog.EyesKey) ShowEyes();
            else _tiles.FirstOrDefault(t => t.Key == key)?.Refresh();
        };
        Loaded += (_, _) =>
        {
            ShowState();
            _status.Start();
            _model.PropertyChanged += OnModelChanged;
            _services.ModelDownloads.Changed += downloadChanged;
            foreach (var t in _tiles) t.Refresh();
            ShowRuntime();
            ShowEyes();
        };
        Unloaded += (_, _) =>
        {
            _status.Stop();
            _model.PropertyChanged -= OnModelChanged;
            _services.ModelDownloads.Changed -= downloadChanged;
        };
    }

    private readonly IReadOnlyList<ProfileTile> _tiles;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Gemma26bModel) or nameof(SettingsViewModel.Gemma12bModel)
            or nameof(SettingsViewModel.LightModel) or nameof(SettingsViewModel.CustomModel) or nameof(SettingsViewModel.ModelsFolder))
            foreach (var t in _tiles) t.Refresh();
        if (e.PropertyName is nameof(SettingsViewModel.AiProfile)) AddFileFields();
        if (e.PropertyName is nameof(SettingsViewModel.Runtime) or nameof(SettingsViewModel.LlamaServerPath)) ShowRuntime();
        if (e.PropertyName is nameof(SettingsViewModel.EyesDevice) or nameof(SettingsViewModel.AiProfile) or nameof(SettingsViewModel.ModelsFolder))
            ShowEyes();
    }

    /// <summary>
    /// Глаза: downloaded or not (and how far), where they read now, and a warning when the video card is chosen but the
    /// memory left beside the model will not hold them (the choice stays the user's).
    /// </summary>
    private void ShowEyes()
    {
        var s = _services.Settings;
        var ai = s.LocalAi;
        var downloads = _services.ModelDownloads;
        var running = downloads.IsRunning(ModelCatalog.EyesKey);
        var eyes = ModelCatalog.Eyes;
        string? ok = null, warn = null;
        if (ai.HasEyes())
        {
            var where = _services.Eyes?.Device switch
            {
                "gpu" => "сейчас на видеокарте",
                "cpu" => _services.Eyes!.ForcedToProcessor ? "сейчас на процессоре (на видеокарте не поместились)" : "сейчас на процессоре",
                _ => "загрузятся при первом трудном тексте",
            };
            ok = s.Eyes.Device == "off" ? "скачаны, выключены"
                : ModelCatalog.For(ai.Profile)?.ReadsStylized == true ? "скачаны; Gemma 4 26B читает такой текст сама, глаза не нужны"
                : "скачаны, " + where;
        }
        else if (!running)
            warn = downloads.ErrorOf(ModelCatalog.EyesKey)
                ?? string.Format(Russian, "не скачаны, {0:0.0} ГБ, {1}", eyes.TotalSize / 1e9, eyes.Page);
        if (warn is null && EyesVramLeft() is var left && EyesPolicy.VramShort(s.Eyes.Device, left))
            warn = string.Format(Russian, "Свободно видеопамяти около {0:0.0} ГБ, глазам нужно около {1:0.0} ГБ. Карточки станут медленнее " +
                "на 20-60%, игра может замирать. Выбор остаётся за тобой.", left / 1024.0, (EyesPolicy.NeedVramMb + EyesPolicy.VramMarginMb) / 1024.0);
        EyesOk.Content = ok;
        EyesOk.Visibility = ok is null ? Visibility.Collapsed : Visibility.Visible;
        EyesWarn.Content = warn;
        EyesWarn.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        var progress = downloads.ProgressOf(ModelCatalog.EyesKey) is not { } p ? null
            : p.Verifying ? "Проверяю файл..."
            : string.Format(Russian, "Загрузка {0:0.0} из {1:0.0} ГБ, {2:0}%", p.Done / 1e9, p.Total / 1e9, 100.0 * p.Done / Math.Max(1, p.Total));
        EyesProgress.Text = progress ?? "";
        EyesProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        EyesDownload.Visibility = !running && !ai.HasEyes() ? Visibility.Visible : Visibility.Collapsed;
        EyesCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Video memory the eyes would find beside the model, MB (the driver's figure, not CUDA's): what is free now, plus
    /// what the eyes on the card already hold, minus the model's file while it is not loaded yet; -1 when unknown.
    /// </summary>
    private int EyesVramLeft()
    {
        var free = AiRouter.FreeVramMb();
        if (free < 0) return -1;
        if (_services.Eyes?.Device == "gpu") free += EyesPolicy.NeedVramMb;
        var ai = _services.Settings.LocalAi;
        if (_services.Ai.Current is null && ai.SingleModel(ai.Profile) is { Length: > 0 } model && File.Exists(model))
            free -= (int)(new FileInfo(model).Length >> 20);
        return Math.Max(0, free);
    }

    /// <summary>For the snapshots (--render-main): the section scrolled to the eyes' row.</summary>
    internal void ScrollToEyes()
    {
        UpdateLayout();
        EyesRow.BringIntoView();
    }

    private void OnDownloadEyes(object sender, RoutedEventArgs e) =>
        _services.ModelDownloads.Start(ModelCatalog.Eyes, _services.Settings.LocalAi.ModelsFolderResolved());

    private void OnCancelEyes(object sender, RoutedEventArgs e) => _services.ModelDownloads.Cancel(ModelCatalog.EyesKey);

    /// <summary>Движок: one's own llama-server, the downloaded build, or what «Скачать» would fetch and how far it got.</summary>
    private void ShowRuntime()
    {
        var ai = _services.Settings.LocalAi;
        var downloads = _services.ModelDownloads;
        var running = downloads.IsRunning(ModelDownloads.Runtime);
        var entry = RuntimeCatalog.For(_model.Runtime) ?? RuntimeCatalog.Items[0];
        var installed = File.Exists(entry.Server(DataPaths.Runtime));
        string? ok = null, warn = null;
        if (ai.LlamaServerPath.Length > 0)
        {
            if (File.Exists(ai.LlamaServerPath)) ok = "работает свой llama-server из \"Файлов\"; очисти поле, чтобы работать на скачанном";
            else warn = $"нет файла {ai.LlamaServerPath} - исправь путь в \"Файлах\" или очисти поле";
        }
        else if (installed) ok = $"llama.cpp {entry.Release}, {entry.Title}, готов";
        else if (!running)
            warn = downloads.ErrorOf(ModelDownloads.Runtime)
                ?? string.Format(Russian, "не скачан, {0:0} МБ, github.com/ggml-org/llama.cpp, релиз {1}", entry.Size / 1048576.0, entry.Release);
        RuntimeOk.Content = ok;
        RuntimeOk.Visibility = ok is null ? Visibility.Collapsed : Visibility.Visible;
        RuntimeWarn.Content = warn;
        RuntimeWarn.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        var progress = downloads.ProgressOf(ModelDownloads.Runtime) is not { } p ? null
            : p.Verifying ? "Проверяю и распаковываю..."
            : string.Format(Russian, "Загрузка {0:0} из {1:0} МБ, {2:0}%", p.Done / 1048576.0, p.Total / 1048576.0, 100.0 * p.Done / Math.Max(1, p.Total));
        RuntimeProgress.Text = progress ?? "";
        RuntimeProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        RuntimeDownload.Visibility = !running && !installed ? Visibility.Visible : Visibility.Collapsed;
        RuntimeCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDownloadRuntime(object sender, RoutedEventArgs e)
    {
        if (RuntimeCatalog.For(_model.Runtime) is { } entry) _services.ModelDownloads.StartRuntime(entry, DataPaths.Runtime);
    }

    private void OnCancelRuntime(object sender, RoutedEventArgs e) => _services.ModelDownloads.Cancel(ModelDownloads.Runtime);

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileTile { Entry: { } entry }) return;
        // A model already there (maybe under one's own name) gets only its sight, beside it.
        var ai = _services.Settings.LocalAi;
        var model = ai.HasModel(entry.Profile) ? ai.SingleModel(entry.Profile) : null;
        _services.ModelDownloads.Start(entry, model is null ? ai.ModelsFolderResolved() : Path.GetDirectoryName(model)!, model);
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProfileTile tile) _services.ModelDownloads.Cancel(tile.Key);
    }

    /// <summary>«Своя модель»: any GGUF file of the user's.</summary>
    private void OnPickModel(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Модели GGUF (*.gguf)|*.gguf", Title = "Своя модель" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _model.CustomModel = dialog.FileName;
        _model.AiProfile = "custom";
    }

    /// <summary>The model file of the chosen profile and the download folder, then llama-server.</summary>
    private void AddFileFields()
    {
        while (Files.Children.Count > 2) Files.Children.RemoveAt(0);
        (string Caption, string Property)[] fields = _model.AiProfile switch
        {
            "gemma12b" => [("GEMMA 4 12B", nameof(SettingsViewModel.Gemma12bModel))],
            "light" => [("ЛЁГКАЯ - GEMMA 4 E4B", nameof(SettingsViewModel.LightModel))],
            "custom" => [("СВОЯ МОДЕЛЬ (GGUF)", nameof(SettingsViewModel.CustomModel))],
            _ => [("GEMMA 4 26B", nameof(SettingsViewModel.Gemma26bModel))],
        };
        fields = [.. fields, ("ПАПКА ДЛЯ СКАЧАННЫХ МОДЕЛЕЙ", nameof(SettingsViewModel.ModelsFolder))];
        var at = 0;
        foreach (var (caption, property) in fields)
        {
            Files.Children.Insert(at++, new TextBlock { Text = caption, Style = (Style)FindResource("FieldCaption") });
            var box = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(0, 0, 0, 14) };
            box.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(property) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus });
            Files.Children.Insert(at++, box);
        }
    }

    private void ShowEndpointState()
    {
        NoEndpoints.Visibility = _endpoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnginesRow.Visibility = _endpoints.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Loaded or not, free video memory, the last lookup's AI time.</summary>
    private void ShowState()
    {
        var s = _services.Settings;
        var current = _services.Ai.Current;
        var title = ProfileTile.Title(s.LocalAi.Profile);
        var profile = char.ToUpperInvariant(title[0]) + title[1..];
        StateText.Text = s.LocalAi.Mode == "off" && s.DictionaryEngine == "local" ? "ИИ выключен - только справочники"
            : s.DictionaryEngine != "local" ? $"Карточку делает \"{s.DictionaryEngine}\""
            : current?.Dictionary is not null ? $"{profile} загружена"
            : !s.LocalAi.HasModel(s.LocalAi.Profile) ? $"{profile} {ProfileTile.Absent(s.LocalAi.Profile)}"
            : !s.LocalAi.HasRuntime() ? "Движок llama.cpp не скачан"
            : $"{profile} выгружена";
        var (free, total) = AiRouter.Vram();
        VramText.Text = free >= 0 ? string.Format(Russian, "{0:0.0} из {1:0} ГБ", free / 1024.0, total / 1024.0) : "нет данных";
        LastText.Text = _services.RecentLookups.FirstOrDefault(r => r.AiSeconds is not null) is { } last
            ? string.Format(Russian, "ИИ {0:0.0} с", last.AiSeconds)
            : "поисков не было";
    }

    /// <summary>Loads the model the lookups use (or reaches the chosen API) and translates one line, timing it.</summary>
    private async void OnTestModel(object sender, RoutedEventArgs e)
    {
        _model.Flush();
        TestButton.IsEnabled = false;
        TestNote.Text = _services.Ai.Current is null ? "Загружаю модель..." : "Перевожу пробную реплику...";
        var sw = Stopwatch.StartNew();
        try
        {
            using var busy = _services.Ai.Use();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var clients = await _services.Ai.GetAsync(cts.Token);
            if (clients.Dictionary is null)
            {
                TestNote.Text = "ИИ выключен: режим \"Выключен\" или мало видеопамяти.";
                return;
            }
            var loaded = sw.Elapsed;
            var reply = await clients.Dictionary.CompleteAsync(new LlmRequest(
                [new LlmMessage("user", "Translate into Russian, reply with the translation only: You should reconsider your position, mortal.")],
                MaxTokens: 80), cts.Token);
            TestNote.Text = string.Format(Russian, "\"{0}\" - {1:0.0} с, из них загрузка {2:0.0} с.", reply.Trim(), sw.Elapsed.TotalSeconds,
                loaded.TotalSeconds);
        }
        catch (Exception ex)
        {
            TestNote.Text = "Не получилось: " + ex.Message;
            _services.Log.Error("AI check", ex);
        }
        finally
        {
            TestButton.IsEnabled = true;
            ShowState();
        }
    }

    private void OnAddEndpoint(object sender, RoutedEventArgs e)
    {
        var p = Presets[(int)((Button)sender).Tag];
        var name = p.Name;
        for (var n = 2; _services.Settings.CustomEndpoints.Any(x => x.Name == name); n++) name = $"{p.Name} {n}";
        var endpoint = new CustomEndpoint { Name = name, Kind = p.Kind, BaseUrl = p.Url, Model = p.Model, Structured = p.Mode };
        _services.Settings.CustomEndpoints.Add(endpoint);
        _endpoints.Add(new EndpointEditor(endpoint, _services, _model));
        _model.EndpointsChanged();
    }

    private void OnRemoveEndpoint(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EndpointEditor editor) return;
        _services.Settings.CustomEndpoints.Remove(editor.Endpoint);
        _services.Keys.Set(editor.Endpoint.Name, null);
        _endpoints.Remove(editor);
        _model.EndpointsChanged();
    }

    private void OnKeyTyped(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: EndpointEditor editor } box) editor.PendingKey = box.Password;
    }

    /// <summary>The key is stored (encrypted) when the field is left, and the field is emptied again.</summary>
    private void OnKeyDone(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { DataContext: EndpointEditor editor } box || box.Password.Length == 0) return;
        editor.SaveKey();
        box.Password = "";
    }

    private async void OnTestEndpoint(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointEditor editor) await editor.TestAsync();
    }
}

/// <summary>
/// A local model as the tiles show it, with «Скачать» for the catalog ones and «Выбрать файл…» for the user's own.
/// Timings and video memory are from the 62-line comparison of 2026-09-29 on an RTX 5060 Ti.
/// </summary>
public sealed class ProfileTile(string key, string name, string note, string speed, string vram, AppServices services) : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Note { get; } = note;
    public string Speed { get; } = speed;
    public string Vram { get; } = vram;
    public ModelEntry? Entry { get; } = ModelCatalog.For(key);

    private string? File => services.Settings.LocalAi.SingleModel(Key);
    private bool Installed => services.Settings.LocalAi.HasModel(Key);
    public bool Downloading => services.ModelDownloads.IsRunning(Key);

    /// <summary>The download in progress: received so far, then the check of the file.</summary>
    public string? Progress => services.ModelDownloads.ProgressOf(Key) is not { } p ? null
        : p.Verifying ? "Проверяю файл..."
        : string.Format(Russian, "Загрузка {0:0.0} из {1:0.0} ГБ, {2:0}%", p.Done / 1e9, p.Total / 1e9, 100.0 * p.Done / Math.Max(1, p.Total));

    public bool HasProgress => Progress is not null;

    /// <summary>What is missing: not downloaded (size, source), a failed download, the user's file.</summary>
    public string? Missing
    {
        get
        {
            if (Downloading) return null;
            var lacksVision = services.Settings.LocalAi.LacksVision(Key);
            if (services.ModelDownloads.ErrorOf(Key) is { } error && (!Installed || lacksVision)) return error;
            if (Installed)
                return Entry?.Vision is { } v && lacksVision
                    ? string.Format(Russian, "нет файла зрения, {0:0.0} ГБ: без него трудные слова не перечитываются", v.Size / 1e9)
                    : null;
            if (Key == "custom") return File is { Length: > 0 } f ? $"нет файла {Path.GetFileName(f)}" : "выбери GGUF-файл своей модели";
            return Entry is { } e ? string.Format(Russian, "не скачана, {0:0.0} ГБ, {1}", e.TotalSize / 1e9, e.Page) : "нет файла";
        }
    }

    public bool HasMissing => Missing is not null;
    public bool CanDownload => Entry is not null && (!Installed || services.Settings.LocalAi.LacksVision(Key)) && !Downloading;
    public bool IsCustom => Key == "custom";

    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string Title(string key) => key switch
    {
        "gemma12b" => "Gemma 4 12B",
        "light" => "Gemma 4 E4B",
        "custom" => "своя модель",
        _ => "Gemma 4 26B",
    };

    /// <summary>A profile without its model file: «не скачана» for a catalog one, «не выбрана» for the user's own.</summary>
    public static string Absent(string key) => key == "custom" ? "не выбрана" : "не скачана";

    public static IReadOnlyList<ProfileTile> For(AppServices services) =>
    [
        new("gemma26b", "Gemma 4 26B", "по умолчанию; лучшая по тесту на 62 репликах (93%), без цензуры", "2,5 с", "около 13 ГБ", services),
        new("gemma12b", "Gemma 4 12B heretic", "меньше видеопамяти, та же точность (94%), но медленнее", "4,0 с", "около 7 ГБ", services),
        new("light", "Лёгкая - Gemma 4 E4B", "для слабых ПК: видеокарта от 4 ГБ; проще и чаще ошибается (75%), без цензуры", "2,1 с", "около 3 ГБ",
            services),
        new("custom", "Своя модель", "любой GGUF-файл: карточку и перевод делает твоя модель", "-", "-", services),
    ];
}

/// <summary>One of the user's endpoints, edited in place: fields write straight into the settings entry.</summary>
public sealed class EndpointEditor : ObservableObject
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _model;
    private string? _status;

    public EndpointEditor(CustomEndpoint endpoint, AppServices services, SettingsViewModel model)
    {
        Endpoint = endpoint;
        _services = services;
        _model = model;
    }

    public CustomEndpoint Endpoint { get; }

    /// <summary>Typed but not yet stored.</summary>
    public string PendingKey { get; set; } = "";

    public string Name
    {
        get => Endpoint.Name;
        set
        {
            var name = value.Trim();
            if (name.Length == 0 || name == Endpoint.Name || _services.Settings.CustomEndpoints.Any(e => e.Name == name)) return;
            _services.Keys.Rename(Endpoint.Name, name);
            var s = _services.Settings;
            if (s.DictionaryEngine == Endpoint.Name) s.DictionaryEngine = name;
            if (s.TranslatorEngine == Endpoint.Name) s.TranslatorEngine = name;
            Endpoint.Name = name;
            OnPropertyChanged();
            _model.EndpointsChanged();
        }
    }

    public string KindKey
    {
        get => Endpoint.Kind.ToString();
        set
        {
            if (!Enum.TryParse<LlmProviderKind>(value, out var kind) || kind == Endpoint.Kind) return;
            Endpoint.Kind = kind;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelHint));
            _model.EndpointsChanged();
        }
    }

    public string BaseUrl
    {
        get => Endpoint.BaseUrl;
        set
        {
            Endpoint.BaseUrl = value.Trim();
            OnPropertyChanged();
            _model.EndpointsChanged();
        }
    }

    public string Model
    {
        get => Endpoint.Model;
        set
        {
            Endpoint.Model = value.Trim();
            OnPropertyChanged();
            _model.EndpointsChanged();
        }
    }

    public string ModelHint => Endpoint.Kind == LlmProviderKind.Anthropic
        ? "например, " + AnthropicLlmClient.SuggestedModels[0]
        : "как в документации сервиса";

    public string StructuredKey
    {
        get => Endpoint.Structured.ToString();
        set
        {
            if (!Enum.TryParse<StructuredOutputMode>(value, out var mode) || mode == Endpoint.Structured) return;
            Endpoint.Structured = mode;
            OnPropertyChanged();
            _model.EndpointsChanged();
        }
    }

    public bool HasKey => _services.Keys.Has(Endpoint.Name);

    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    public void SaveKey()
    {
        if (PendingKey.Length == 0) return;
        _services.Keys.Set(Endpoint.Name, PendingKey);
        PendingKey = "";
        OnPropertyChanged(nameof(HasKey));
        _services.Ai.Reset(); // clients made with the old key must not be reused
        Status = "Ключ сохранён.";
    }

    /// <summary>Sends a tiny request and reports the answer or the error.</summary>
    public async Task TestAsync()
    {
        SaveKey();
        var e = Endpoint;
        var ep = new LlmEndpoint(e.Name, e.Kind, e.BaseUrl, e.Model, _services.Keys.Get(e.Name), e.Structured);
        ILlmClient client = e.Kind == LlmProviderKind.Anthropic
            ? new AnthropicLlmClient(ep)
            : new OpenAiCompatibleClient(ep.IsLocal ? _services.LocalHttp : _services.RemoteHttp, ep);
        Status = "Проверяю...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var sw = Stopwatch.StartNew();
            var reply = await client.CompleteAsync(new LlmRequest(
                [new LlmMessage("user", "Translate into Russian, reply with the translation only: Good morning")], MaxTokens: 50), cts.Token);
            Status = string.Format(CultureInfo.GetCultureInfo("ru-RU"), "Работает: \"{0}\" за {1:0.0} с.", reply.Trim(), sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Status = "Ошибка: " + ex.Message;
        }
    }
}
