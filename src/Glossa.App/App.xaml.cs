using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Glossa.App.Ai;
using Glossa.App.Capture;
using Glossa.App.Input;
using Glossa.App.Lookup;
using Glossa.App.Theme;
using Glossa.App.Views;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Library;
using Glossa.Core.Logging;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Glossa.App;

public partial class App : Application
{
    private const int HotkeyLookup = 1;
    private const int HotkeyClose = 2;
    private const int HotkeySave = 3;
    private const int HotkeySpeak = 4;
    private const int HotkeyDetails = 5;
    private const int HotkeyReveal = 6;
    private const int HotkeyWindow = 7;

    private readonly ThemeManager _theme = new();
    private KeyStore? _keys;
    private Speech.SpeechService? _speech;
    private AppServices? _services;
    private MainWindow? _main;

    private Mutex? _instance;
    private TrayMenu? _trayMenu;
    private bool _lookupOn = true;
    private string? _aiState;
    private EventWaitHandle? _activate;
    private EventWaitHandle? _exit;
    private DictionaryService? _dictionaries;
    private LevelService? _levels;
    private HttpClient? _directHttp;
    private FileLogger? _log;
    private SettingsStore? _store;
    private AppSettings _settings = new();
    private HttpClient? _localHttp;
    private HttpClient? _remoteHttp;
    private LibraryStore? _library;
    private OcrEngine? _ocr;
    private JapaneseAnalyzer? _japanese;
    private ChineseDictionary? _chinese;
    private ScreenCapture? _capture;
    private LlamaServerHost? _llama;
    private AiRouter? _router;
    private HotkeyManager? _hotkeys;
    private LookupPopup? _popup;
    private LookupController? _controller;
    private Games.GameRegistry? _games;
    private Games.ResumeGuardClient? _guard;
    private InputThread? _input;
    private GamepadHub? _pad;
    private LookupSessions? _sessions;
    private WinForms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var render = Array.IndexOf(e.Args, "--render-card");
        if (render >= 0 && render + 1 < e.Args.Length)
        {
            _theme.Install(this, "dark");
            CardSnapshots.Render(e.Args[render + 1], _theme);
            Shutdown();
            return;
        }
        var renderMain = Array.IndexOf(e.Args, "--render-main");
        if (renderMain >= 0 && renderMain + 1 < e.Args.Length)
        {
            _theme.Install(this, "dark");
            CardSnapshots.RenderMain(e.Args[renderMain + 1], renderMain + 2 < e.Args.Length ? e.Args[renderMain + 2] : null, _theme);
            Shutdown();
            return;
        }
        // --selftest <cases.json> <out folder> [count]: runs beside the everyday copy and touches nothing of the user's.
        var selftestAt = Array.IndexOf(e.Args, "--selftest");
        (string Cases, string Out, int Count)? selftest = selftestAt >= 0 && selftestAt + 2 < e.Args.Length
            ? (e.Args[selftestAt + 1], e.Args[selftestAt + 2],
               selftestAt + 3 < e.Args.Length && int.TryParse(e.Args[selftestAt + 3], out var n) ? n : 25)
            : null;
        if (selftest is null && !StartSingleInstance()) return;

        Directory.CreateDirectory(DataPaths.Root);
        var log = _log = new FileLogger(DataPaths.Logs);
        DispatcherUnhandledException += (_, ev) => { log.Error("UI unhandled", ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => log.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { log.Error("Task unhandled", ev.Exception); ev.SetObserved(); };
        log.Info("Glossa starting");

        _store = new SettingsStore(DataPaths.Settings);
        _settings = _store.Load();
        if (selftest is null) _store.Save(_settings); // writes defaults so the file can be edited by hand
        else
        {
            // The test must not save words, speak, or collide with the everyday copy's llama-server.
            _settings.AutoSaveWords = false;
            _settings.Popup.AutoPlayAudio = false;
            _settings.LocalAi.BasePort += 100;
            if (Environment.GetEnvironmentVariable("GLOSSA_PRIORITY") is { Length: > 0 } priority) _settings.Performance.Priority = priority;
        }
        _theme.Install(this, _settings.Theme);

        if (!OcrEngine.ModelsPresent(DataPaths.OcrModels))
        {
            MessageBox.Show($"Не найдены модели OCR в {DataPaths.OcrModels}.\nЗапустите tools\\export_ocr_models.py.",
                "Glossa", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Local servers must bypass the system SOCKS proxy; cloud endpoints go through it.
        _localHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };
        _remoteHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _directHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };

        _library = new LibraryStore(DataPaths.Library);
        _dictionaries = new DictionaryService(DataPaths.Packs);
        // Recognition gives back its buffers after 2 idle minutes. GLOSSA_OCR_MEMORY=Arena|NoPatterns|None and
        // GLOSSA_OCR_TRIM=<seconds> are for measurements only.
        _ocr = new OcrEngine(DataPaths.OcrModels, _settings.Performance.OcrThreads,
            Enum.TryParse<OcrMemory>(Environment.GetEnvironmentVariable("GLOSSA_OCR_MEMORY"), out var memory) ? memory : OcrMemory.Arena,
            TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("GLOSSA_OCR_TRIM"), out var trim) ? trim : 120));
        _ocr.Trimmed += () => log.Info("OCR idle — buffers released");
        ApplyPriority();
        _capture = new ScreenCapture(log);
        _llama = new LlamaServerHost(log, _localHttp);
        _keys = new KeyStore(DataPaths.Keys);
        _speech = new Speech.SpeechService(DataPaths.Audio, () => _settings.Speech);
        _router = new AiRouter(() => _settings, _llama, _localHttp, _remoteHttp, name => _keys.Get(name), log);
        _services = new AppServices(() => _settings, s => _store!.Save(s), _library, _keys, _speech, _router,
            _dictionaries, () => _levels, ReloadDictionaries, _localHttp, _remoteHttp, _directHttp, _theme, log);
        _games = _services.Games = new Games.GameRegistry(() => _settings, s => _store!.Save(s), log);

        var vm = new LookupViewModel();
        _popup = new LookupPopup(vm);
        new WindowInteropHelper(_popup).EnsureHandle();
        _popup.SetHiddenFromCapture(_settings.Popup.HideFromCapture);
        _popup.ApplyLook(_settings.Popup, _theme);
        _theme.Changed += () => _popup.ApplyLook(_settings.Popup, _theme); // the card may follow the window theme
        var words = new WordLookup(() => _japanese, () => _chinese, _dictionaries, () => _levels);
        _controller = new LookupController(() => _settings, _ocr, words, _router, _library, _popup, vm, _speech, log);
        _services.AddWord = _controller.AddWordAsync;
        // «In a game»: the program of the last lookup is still in front (not Glossa itself).
        _router.InGame = () => _controller.LastAppExe is { Length: > 0 } last
                               && !last.Equals("Glossa.exe", StringComparison.OrdinalIgnoreCase)
                               && LookupController.ForegroundExe().Equals(last, StringComparison.OrdinalIgnoreCase);
        _controller.Reported += _services.Report;
        _popup.SaveRequested += _controller.SaveCurrent;
        _popup.SpeakRequested += _controller.Speak;
        _popup.IsVisibleChanged += (_, _) => OnPopupVisibility(_popup.IsVisible);
        vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(LookupViewModel.IsSaved) && vm.IsSaved) _services.NotifyLibraryChanged();
        };
        // Settings apply as they change; the AI is restarted only when something it runs on changed.
        _aiState = AiState(_settings);
        _services.SettingsChanged += () =>
        {
            var ai = AiState(_settings);
            if (ai != _aiState)
            {
                _aiState = ai;
                _router.Reset();
            }
            _popup.SetHiddenFromCapture(_settings.Popup.HideFromCapture);
            _theme.Apply(_settings.Theme);
            _popup.ApplyLook(_settings.Popup, _theme);
            _ocr.Threads = _settings.Performance.OcrThreads;
            ApplyPriority();
            _pad?.Apply();
        };
        _services.ChangeHotkey = spec =>
        {
            if (!_hotkeys!.Register(HotkeyLookup, spec))
            {
                _hotkeys.Register(HotkeyLookup, _settings.Hotkey); // taken by another program: keep the old one
                return false;
            }
            _settings.Hotkey = spec;
            _store!.Save(_settings);
            _lookupOn = true;
            return true;
        };
        // Paused from the tray is not «taken by another program».
        _services.HotkeyActive = () => !_lookupOn || _hotkeys?.IsRegistered(HotkeyLookup) == true;
        _services.SampleLoad = () => Diagnostics.LoadMeter.Sample(_llama);

        if (selftest is { } st)
        {
            _popup.Offscreen = true;
            _ = RunSelfTestAsync(st.Cases, st.Out, st.Count, log);
            return;
        }

        // P4: game profiles, the mouse button and the gamepad, the still frame and the pause of a game.
        _guard = new Games.ResumeGuardClient(log);
        _input = new InputThread(log);
        _pad = new GamepadHub(_input, () => _settings, Dispatcher);
        _sessions = new LookupSessions(() => _settings, _games, _capture, _ocr, words, _controller, _popup, _guard, _pad, log);
        _pad.ComboPressed += _sessions.PadCombo;
        _pad.MousePressed += _sessions.Pointer;
        _sessions.Notice += text => _tray?.ShowBalloonTip(6000, "Glossa", text, WinForms.ToolTipIcon.Warning);
        _services.RecordGamepad = _pad.Record;
        _services.CancelGamepadRecording = _pad.CancelRecording;

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
        _tray = CreateTray();
        if (!_hotkeys.Register(HotkeyLookup, _settings.Hotkey))
            _tray.ShowBalloonTip(5000, "Glossa", $"Не удалось занять {_settings.Hotkey} — клавиша занята другой программой. Смените её в настройках.", WinForms.ToolTipIcon.Warning);
        else
            _tray.ShowBalloonTip(3000, "Glossa", $"Наведите курсор на слово и нажмите {_settings.Hotkey}.", WinForms.ToolTipIcon.Info);
        if (_settings.WindowHotkey.Length > 0 && !_hotkeys.Register(HotkeyWindow, _settings.WindowHotkey))
            log.Warn($"window hotkey {_settings.WindowHotkey} is taken by another program");
        _services.ChangeWindowHotkey = spec =>
        {
            if (spec.Length == 0)
            {
                _hotkeys.Unregister(HotkeyWindow);
            }
            else if (!_hotkeys.Register(HotkeyWindow, spec))
            {
                if (_settings.WindowHotkey.Length > 0) _hotkeys.Register(HotkeyWindow, _settings.WindowHotkey);
                return false;
            }
            _settings.WindowHotkey = spec;
            _store!.Save(_settings);
            return true;
        };
        _services.WindowHotkeyActive = () => _settings.WindowHotkey.Length == 0 || _hotkeys.IsRegistered(HotkeyWindow);

        _ = Task.Run(() => Warm(log));

        // Started by hand: show the window. Autostart passes --tray and stays quiet.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMain(0);
    }

    /// <summary>
    /// One Glossa per system: a second start brings the running one's window up and quits. tools\publish.ps1 signals
    /// Exit to close this copy cleanly before replacing its files.
    /// </summary>
    private bool StartSingleInstance()
    {
        _instance = new Mutex(true, @"Global\Glossa.SingleInstance", out var createdNew);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\Glossa.Activate");
        if (!createdNew)
        {
            // Already running: bring its window up instead of starting twice.
            _activate.Set();
            Shutdown();
            return false;
        }
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\Glossa.Exit");
        var signals = new WaitHandle[] { _activate, _exit };
        new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny(signals);
                try { Dispatcher.Invoke(() => { if (which == 0) ShowMain(0); else Shutdown(); }); }
                catch (TaskCanceledException) { return; }
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "Glossa activation" }.Start();
        return true;
    }

    /// <summary>What the AI runs on: models, mode, endpoints, engines. A change restarts it (unloads the model).</summary>
    private static string AiState(AppSettings s) =>
        System.Text.Json.JsonSerializer.Serialize(new { s.LocalAi, s.CustomEndpoints, s.DictionaryEngine, s.TranslatorEngine, s.Performance.Priority });

    /// <summary>Настройки → Нагрузка на ПК → Приоритет: low lets the game have the processor first.</summary>
    private void ApplyPriority()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = _settings.Performance.Priority == "normal" ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            _log?.Error("Priority", ex);
        }
    }

    private async Task RunSelfTestAsync(string cases, string outDir, int count, ILog log)
    {
        try
        {
            await Task.Run(() => Warm(log));
            await Diagnostics.SelfTest.RunAsync(cases, outDir, count, _controller!, _llama!, log);
        }
        catch (Exception ex)
        {
            log.Error("selftest", ex);
        }
        Shutdown();
    }

    /// <summary>
    /// Loads OCR and the language data in the background. The AI model is not loaded here: its size is chosen
    /// from free VRAM at the first lookup, when a game is typically already running and using the GPU.
    /// </summary>
    private Task Warm(ILog log)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _ocr!.Warm(_settings.ScreenLanguage == "ru" ? OcrModelFamily.Cyrillic : OcrModelFamily.CjkLatin);
            if (Directory.Exists(DataPaths.UniDic)) _japanese = new JapaneseAnalyzer(DataPaths.UniDic);
            if (File.Exists(DataPaths.Cedict)) _chinese = new ChineseDictionary(DataPaths.Cedict);
            ReloadDictionaries();
            log.Info($"OCR, UniDic, CC-CEDICT ({_chinese?.Count ?? 0} keys) and {_dictionaries!.Installed.Count} dictionaries ready in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            log.Error("Warm-up failed", ex);
        }
        return Task.CompletedTask;
    }

    private void ReloadDictionaries()
    {
        var d = _settings.Dictionaries;
        foreach (var error in _dictionaries!.Reload(d.Order, d.Disabled)) _log?.Warn("Dictionary skipped: " + error);
        var old = _levels;
        _levels = new LevelService(DataPaths.Levels);
        old?.Dispose();
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyLookup: _sessions?.Pointer(_settings.Hotkey); break;
            case HotkeyClose: _sessions?.Escape(); break;
            case HotkeyWindow: ShowMain(0); break;
            case HotkeySave: _controller?.SaveCurrent(); break;
            case HotkeySpeak: _controller?.Speak(); break;
            case HotkeyDetails: _controller?.OnDetails(); break;
            case HotkeyReveal: _popup?.RevealTranslation(); break;
        }
    }

    /// <summary>Opens (or brings back) the main window on the given tab: 0 words, 1 dictionaries, 2 settings.</summary>
    private void ShowMain(int tab = 0)
    {
        if (_main is null)
        {
            _main = new MainWindow(_services!);
            _main.Closed += (_, _) => _main = null;
        }
        _main.ShowTab(tab);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>Esc, S, P, Tab (and Space in training mode) belong to the card only while it is on screen.</summary>
    private void OnPopupVisibility(bool visible)
    {
        if (_hotkeys is null) return;
        if (visible)
        {
            _hotkeys.Register(HotkeyClose, "Escape");
            _hotkeys.Register(HotkeySave, "S");
            _hotkeys.Register(HotkeySpeak, "P");
            _hotkeys.Register(HotkeyDetails, "Tab");
            if (_settings.Popup.HideTranslation) _hotkeys.Register(HotkeyReveal, "Space");
        }
        else
        {
            _hotkeys.Unregister(HotkeyClose);
            _hotkeys.Unregister(HotkeySave);
            _hotkeys.Unregister(HotkeySpeak);
            _hotkeys.Unregister(HotkeyDetails);
            _hotkeys.Unregister(HotkeyReveal);
        }
    }

    /// <summary>The icon itself stays a Windows tray icon; its menu is Glossa's own window (mockup «Меню в трее»).</summary>
    private WinForms.NotifyIcon CreateTray()
    {
        var tray = new WinForms.NotifyIcon { Icon = TrayIcon(), Text = "Glossa — экранный словарь", Visible = true };
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) ShowTrayMenu();
        };
        tray.DoubleClick += (_, _) => ShowMain(0);
        return tray;
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is null)
        {
            var menu = _trayMenu = new TrayMenu();
            menu.OpenRequested += () => ShowMain(0);
            menu.SettingsRequested += () => ShowMain(2);
            menu.ExitRequested += Shutdown;
            menu.LookupToggled += SetLookup;
            menu.ModeChanged += mode =>
            {
                _settings.LocalAi.Mode = mode;
                _store!.Save(_settings);
                _services!.NotifySettingsChanged();
                _services.NotifyChangedElsewhere();
            };
        }
        var at = WinForms.Cursor.Position;
        _trayMenu.ShowAt(at.X, at.Y, TrayState());
    }

    private TrayState TrayState()
    {
        var s = _settings;
        var loaded = _router?.Current?.Dictionary is not null;
        var status = s.DictionaryEngine != "local" ? $"ИИ: «{s.DictionaryEngine}»"
            : s.LocalAi.Mode == "off" ? "ИИ выключен · только справочники"
            : loaded ? $"ИИ готова · {Views.Settings.ProfileTile.Title(s.LocalAi.Profile)}"
            : !s.LocalAi.HasModel(s.LocalAi.Profile)
                ? $"ИИ {Views.Settings.ProfileTile.Absent(s.LocalAi.Profile)} · {Views.Settings.ProfileTile.Title(s.LocalAi.Profile)}"
            : !s.LocalAi.HasRuntime() ? "ИИ: движок llama.cpp не скачан"
            : "ИИ выгружена · загрузится при поиске";
        var mode = s.LocalAi.Mode;
        return new TrayState(status, loaded, mode, s.Hotkey, _lookupOn);
    }

    /// <summary>«Поиск по Alt+Q» in the tray: hands the combination back to a game that needs it, until switched on again.</summary>
    private void SetLookup(bool on)
    {
        _lookupOn = on;
        if (!on)
        {
            _hotkeys!.Unregister(HotkeyLookup);
            return;
        }
        if (!_hotkeys!.Register(HotkeyLookup, _settings.Hotkey))
            _tray?.ShowBalloonTip(4000, "Glossa", $"{_settings.Hotkey} занято другой программой.", WinForms.ToolTipIcon.Warning);
    }

    /// <summary>The exe's icon (tools\make-icon.ps1 draws every size) at the tray's size.</summary>
    private static Drawing.Icon TrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/glossa.ico")).Stream;
        return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("Glossa exiting");
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _hotkeys?.Dispose();
        _guard?.Dispose(); // a paused game wakes before Glossa goes
        _input?.Dispose();
        _router?.Dispose();
        _llama?.Dispose();
        _capture?.Dispose();
        _ocr?.Dispose();
        _japanese?.Dispose();
        _dictionaries?.Dispose();
        _levels?.Dispose();
        _library?.Dispose();
        _localHttp?.Dispose();
        _remoteHttp?.Dispose();
        _directHttp?.Dispose();
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        _activate?.Dispose();
        _exit?.Dispose();
        base.OnExit(e);
    }
}
