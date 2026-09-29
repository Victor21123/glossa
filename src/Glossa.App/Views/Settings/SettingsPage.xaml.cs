using System.Windows;
using System.Windows.Controls;
using Glossa.App.ViewModels;

namespace Glossa.App.Views.Settings;

public partial class SettingsPage : UserControl
{
    /// <summary>The sections in the mockups' order; each is built the first time it is opened.</summary>
    private static readonly (string Key, string Name)[] Sections =
    [
        ("card", "Карточка слова"), ("keys", "Вызов и клавиши"), ("languages", "Языки"), ("ai", "ИИ и модели"),
        ("sources", "Справочники"), ("library", "Словарь и Anki"), ("speech", "Озвучка"), ("games", "Игры и профили"),
        ("load", "Нагрузка на ПК"), ("app", "Приложение"),
    ];

    private readonly Dictionary<string, FrameworkElement> _built = [];
    private readonly Dictionary<string, RadioButton> _tabs = [];
    private AppServices? _services;
    private SettingsViewModel? _model;
    private LibraryViewModel? _library;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public string Current { get; private set; } = "card";

    /// <summary>The settings model, shared with «Главная» so a choice made there shows here.</summary>
    public SettingsViewModel? Model => _model;

    /// <summary>The «Словарь» model comes along: exports in «Словарь и Anki» take what is filtered there.</summary>
    public void Attach(AppServices services, LibraryViewModel library)
    {
        _services = services;
        _library = library;
        _model = new SettingsViewModel(services);
        foreach (var (key, name) in Sections)
        {
            var tab = new RadioButton { Content = name, GroupName = "SettingsSections", Style = (Style)FindResource("SectionTab") };
            tab.Checked += (_, _) => Show(key);
            _tabs[key] = tab;
            Nav.Children.Add(tab);
        }
        Show("card");
    }

    public void Show(string key)
    {
        if (_services is null || _model is null || !_tabs.ContainsKey(key)) return;
        Current = key;
        _tabs[key].IsChecked = true;
        if (!_built.TryGetValue(key, out var section)) _built[key] = section = Build(key, _services, _model, _library!);
        if (Host.Content == section) return;
        Host.Content = section;
        Scroller.ScrollToTop();
    }

    /// <summary>The section's control once built (snapshots and tests reach into it).</summary>
    public FrameworkElement? Section(string key) => _built.GetValueOrDefault(key);

    /// <summary>True while Вызов и клавиши waits for a new combination: the window keeps its shortcuts to itself.</summary>
    public bool CapturingKeys => _built.GetValueOrDefault("keys") is KeysSection { Capturing: true };

    /// <summary>Writes pending changes (the window is closing).</summary>
    public void Flush() => _model?.Flush();

    /// <summary>The window closed: the settings model stops listening to the app.</summary>
    public void Detach() => _model?.Detach();

    private static FrameworkElement Build(string key, AppServices services, SettingsViewModel model, LibraryViewModel library) => key switch
    {
        "card" => new CardSection(services, model),
        "keys" => new KeysSection(services, model),
        "languages" => new LanguagesSection(services, model),
        "ai" => new AiSection(services, model),
        "sources" => new SourcesSection(services),
        "library" => new LibrarySection(services, model, library),
        "speech" => new SpeechSection(services, model),
        "games" => new GamesSection(services),
        "load" => new LoadSection(services, model),
        _ => new AppSection(services, model),
    };
}
