using Glossa.App.Ai;
using Glossa.App.Lookup;
using Glossa.App.Speech;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Library;
using Glossa.Core.Logging;
using Glossa.Core.Pictures;

namespace Glossa.App;

/// <summary>Everything the windows need, created once in App.OnStartup.</summary>
public sealed class AppServices(
    Func<AppSettings> settings,
    Action<AppSettings> saveSettings,
    LibraryStore library,
    KeyStore keys,
    SpeechService speech,
    AiRouter ai,
    DictionaryService dictionaries,
    Func<LevelService?> levels,
    Action reloadDictionaries,
    HttpClient localHttp,
    HttpClient remoteHttp,
    HttpClient directHttp,
    ThemeManager theme,
    ILog log)
{
    private readonly List<LookupReport> _recent = [];

    public AppSettings Settings => settings();
    public void SaveSettings(AppSettings s) => saveSettings(s);
    public LibraryStore Library { get; } = library;
    public KeyStore Keys { get; } = keys;
    public SpeechService Speech { get; } = speech;
    public AiRouter Ai { get; } = ai;
    public DictionaryService Dictionaries { get; } = dictionaries;
    public LevelService? Levels => levels();
    public HttpClient LocalHttp { get; } = localHttp;
    public HttpClient RemoteHttp { get; } = remoteHttp;

    /// <summary>Downloads bypassing the system proxy, as a fallback when the proxy cannot reach a site.</summary>
    public HttpClient DirectHttp { get; } = directHttp;

    /// <summary>«Картинка значения»: searched directly first, through the system proxy when that fails.</summary>
    public MeaningPictures Pictures { get; } = new(directHttp, remoteHttp);

    public ILog Log { get; } = log;

    /// <summary>Window and card palettes (the card preview in the settings paints with it).</summary>
    public ThemeManager Theme { get; } = theme;

    /// <summary>
    /// Takes a new lookup hotkey: registers it and saves it, or keeps the old one when another program holds it.
    /// Set by the app, which owns the hotkeys.
    /// </summary>
    public Func<string, bool>? ChangeHotkey { get; set; }

    /// <summary>What Glossa and its llama-server cost the PC right now (Настройки → Нагрузка на ПК).</summary>
    public Func<Diagnostics.LoadSample>? SampleLoad { get; set; }

    /// <summary>Whether the lookup hotkey is registered now (false: another program holds it).</summary>
    public Func<bool>? HotkeyActive { get; set; }

    /// <summary>«Открыть Glossa»: takes a new combination ("" — none); false when another program holds it.</summary>
    public Func<string, bool>? ChangeWindowHotkey { get; set; }

    /// <summary>Whether «Открыть Glossa» is registered now (or switched off).</summary>
    public Func<bool>? WindowHotkeyActive { get; set; }

    /// <summary>«Записать» for the gamepad: the next buttons held together, or why none (set by the app).</summary>
    public Action<Action<string?, string?>>? RecordGamepad { get; set; }

    public Action? CancelGamepadRecording { get; set; }

    /// <summary>Настройки → Игры и профили: the programs looked up in (null only in design snapshots without games).</summary>
    public Games.GameRegistry? Games { get; set; }

    /// <summary>The eyes' server (null only in design snapshots): where they read now, for Настройки -> ИИ и модели.</summary>
    public Ai.EyesService? Eyes { get; set; }

    private ModelDownloads? _models;

    /// <summary>
    /// «Скачать» for the local models and llama.cpp. A finished model becomes its profile's model (a profile set to a
    /// file elsewhere is pointed at the new one); a finished llama.cpp build becomes the engine, replacing one's own
    /// llama-server. The AI restarts on either.
    /// </summary>
    public ModelDownloads ModelDownloads => _models ??= new ModelDownloads(RemoteHttp, DirectHttp, Log, (key, path) =>
    {
        var ai = settings().LocalAi;
        // The eyes are no profile: they are found in the models folder and read from the next lookup on.
        if (key == Glossa.Core.Llm.ModelCatalog.EyesKey)
        {
            Eyes?.Reset();
            NotifySettingsChanged();
            return;
        }
        if (key == ModelDownloads.Runtime)
        {
            if (Glossa.Core.Llm.RuntimeCatalog.Items.FirstOrDefault(e =>
                    string.Equals(e.Server(DataPaths.Runtime), path, StringComparison.OrdinalIgnoreCase)) is not { } build) return;
            ai.Runtime = build.Id;
            ai.LlamaServerPath = "";
        }
        else
        {
            // Only the sight came, for the model already in use: its server was started without it.
            if (string.Equals(ai.SingleModel(key), path, StringComparison.OrdinalIgnoreCase))
            {
                if (key == ai.Profile) Ai.Reset();
                return;
            }
            switch (key)
            {
                case "gemma26b": ai.Gemma26bModel = path; break;
                case "gemma12b": ai.Gemma12bModel = path; break;
                case "light": ai.LightModel = path; break;
                default: return;
            }
        }
        saveSettings(settings());
        NotifySettingsChanged();
    });

    /// <summary>The last lookups, newest first.</summary>
    public IReadOnlyList<LookupReport> RecentLookups
    {
        get { lock (_recent) return _recent.ToList(); }
    }

    public event Action<LookupReport>? LookupReported;

    public void Report(LookupReport report)
    {
        lock (_recent)
        {
            _recent.Insert(0, report);
            if (_recent.Count > 8) _recent.RemoveAt(_recent.Count - 1);
        }
        LookupReported?.Invoke(report);
    }

    /// <summary>Reopens dictionary packs and level lists after an install, import, removal or reorder.</summary>
    public void ReloadDictionaries() => reloadDictionaries();

    /// <summary>Raised after settings are saved so the app can re-register hotkeys and reset AI.</summary>
    public event Action? SettingsChanged;

    public void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>Raised when a setting changed outside Настройки (the tray menu), so an open settings page shows it.</summary>
    public event Action? ChangedElsewhere;

    public void NotifyChangedElsewhere() => ChangedElsewhere?.Invoke();

    /// <summary>Raised when the library changes outside the Words tab (a lookup saved a word).</summary>
    public event Action? LibraryChanged;

    public void NotifyLibraryChanged() => LibraryChanged?.Invoke();

    /// <summary>An action counted for the days with Glossa: the series and its calendar on «Главная» follow (any thread).</summary>
    public event Action? ActivityChanged;

    public void NotifyActivity() => ActivityChanged?.Invoke();

    /// <summary>
    /// «Новое слово»: the lookup pipeline without a screenshot (dictionaries at once, then the AI card). Set by the app
    /// once the lookup controller exists; returns the word's id.
    /// </summary>
    public Func<NewWord, CancellationToken, Task<string>>? AddWord { get; set; }
}

/// <summary>A word typed in by hand. <see cref="Language"/> null means detect it; the sentence and game are optional.</summary>
public sealed record NewWord(string Word, string? Language, string? Sentence, string? Game);
