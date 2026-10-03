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
using Glossa.Core.Notices;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using Glossa.Core.Updates;
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
    private const int HotkeyCorrect = 8;

    /// <summary>Live translation's «в цитаты» (Alt+S): held only while live translation runs.</summary>
    private const int HotkeyQuote = 9;

    private readonly ThemeManager _theme = new();
    private KeyStore? _keys;
    private Speech.SpeechService? _speech;
    private AppServices? _services;
    private MainWindow? _main;

    private Mutex? _instance;
    private TrayMenu? _trayMenu;
    private bool _lookupOn = true;

    /// <summary>Counts recordings, so a late resume of the mouse and pad triggers cannot cut a newer recording short.</summary>
    private int _padSuspendGeneration;
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
    private EyesService? _eyes;
    private string? _eyesState;
    private HotkeyManager? _hotkeys;
    private LookupPopup? _popup;
    private LookupController? _controller;
    private Games.GameRegistry? _games;
    private Games.ResumeGuardClient? _guard;
    private InputThread? _input;
    private GamepadHub? _pad;
    private LookupSessions? _sessions;
    private WinForms.NotifyIcon? _tray;
    private UpdateService? _updates;
    private CancellationTokenSource? _updatesStop;
    private HttpClient? _updateProxied;
    private HttpClient? _updateDirect;
    private bool _updatesWereOn;

    /// <summary>What a click on the tray notice that is on screen opens; every notice goes through <see cref="ShowBalloon"/>.</summary>
    private readonly NoticeTarget _notice = new();

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
#if DEBUG
        // Snapshots of the home page and of every companion: a debug build only. In a release they would draw the
        // companions out of the encrypted pack for anyone - spoilers (2026-10-03).
        var renderMain = Array.IndexOf(e.Args, "--render-main");
        if (renderMain >= 0 && renderMain + 1 < e.Args.Length)
        {
            _theme.Install(this, "dark");
            CardSnapshots.RenderMain(e.Args[renderMain + 1], _theme);
            Shutdown();
            return;
        }
        var renderCompanions = Array.IndexOf(e.Args, "--render-companions");
        if (renderCompanions >= 0 && renderCompanions + 1 < e.Args.Length)
        {
            _theme.Install(this, "dark");
            CardSnapshots.RenderCompanions(e.Args[renderCompanions + 1], _theme);
            Shutdown();
            return;
        }
#endif
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
            // The test must not save words or quotes, speak, or collide with the everyday copy's llama-server.
            _settings.AutoSaveWords = false;
            _settings.Quotes.Save = false;
            _settings.Popup.AutoPlayAudio = false;
            _settings.LocalAi.BasePort += 100;
            if (Environment.GetEnvironmentVariable("GLOSSA_PRIORITY") is { Length: > 0 } priority) _settings.Performance.Priority = priority;
            if (Environment.GetEnvironmentVariable("GLOSSA_VISION") is { Length: > 0 } vision) _settings.Performance.VisionReading = vision;
            if (Environment.GetEnvironmentVariable("GLOSSA_EYES") is { Length: > 0 } eyes) _settings.Eyes.Device = eyes;
            if (Environment.GetEnvironmentVariable("GLOSSA_PROFILE") is { Length: > 0 } profile) _settings.LocalAi.Profile = profile;
        }
        _theme.Install(this, _settings.Theme);

        if (!OcrEngine.ModelsPresent(DataPaths.OcrModels))
        {
            MessageBox.Show($"Не найдены модели OCR в {DataPaths.OcrModels}.\nЗапустите tools\\export_ocr_models.py.",
                "Glossa", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Local servers must bypass the system SOCKS proxy; cloud endpoints go through it. An answer left early (a loop
        // cut, the eyes' first look stopped at the point) closes its connection at once: drained for reuse, llama-server
        // would go on generating the rest and keep the next request waiting (B-33).
        _localHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxResponseDrainSize = 0 }) { Timeout = TimeSpan.FromMinutes(5) };
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
        _ocr.ColumnReadFailed += why => log.Warn("OCR: a column of vertical text could not be read, the library's reading stays: " + why);
        _ocr.VerticalFailed += why => log.Warn("OCR: the reader of vertical text could not start, columns stay unread until the next idle trim: " + why);
        ApplyPriority();
        _capture = new ScreenCapture(log);
        _llama = new LlamaServerHost(log, _localHttp, AiRouter.FreeVramMb, Interop.Native.FreeRamMb);
        _keys = new KeyStore(DataPaths.Keys);
        _speech = new Speech.SpeechService(DataPaths.Audio, () => _settings.Speech);
        _router = new AiRouter(() => _settings, _llama, _localHttp, _remoteHttp, name => _keys.Get(name), log);
        // A model loaded by hand left for a game short of video memory: said once, it loads again at the next lookup.
        _router.PinnedUnloaded += _ => Dispatcher.BeginInvoke(() => ShowBalloon(NoticeKind.Nothing, null, 8000, "Glossa",
            "Модель выгружена: игре не хватает видеопамяти. Загрузится при следующем поиске.", WinForms.ToolTipIcon.Info));
        _eyes = new EyesService(() => _settings, _llama, _localHttp, Interop.Native.FreeRamMb, AiRouter.FreeVramMb, log);
        _router.Eyes = _eyes;
        _eyes.MovedOnCard += _router.Rebaseline;
        _eyes.Notice += text => ShowBalloon(NoticeKind.Nothing, null, 8000, "Glossa", text, WinForms.ToolTipIcon.Warning);
        _services = new AppServices(() => _settings, s => _store!.Save(s), _library, _keys, _speech, _router,
            _dictionaries, () => _levels, ReloadDictionaries, _localHttp, _remoteHttp, _directHttp, _theme, log);
        _games = _services.Games = new Games.GameRegistry(() => _settings, s => _store!.Save(s), log);
        _services.Eyes = _eyes;
        // The companions: only the encrypted pack beside the program, opened with this build's key (no folder of
        // pictures is read, so none can be swapped in). Not in the selftest - it runs on the user's library and must
        // not write to it, a first roll included.
        if (selftest is null)
            _services.Companions = AppServices.NewKeeper(_library, log);

        var vm = new LookupViewModel();
        _popup = new LookupPopup(vm);
        new WindowInteropHelper(_popup).EnsureHandle();
        _popup.SetHiddenFromCapture(_settings.Popup.HideFromCapture);
        _popup.ApplyLook(_settings.Popup, _theme);
        _theme.Changed += () => _popup.ApplyLook(_settings.Popup, _theme); // the card may follow the window theme
        var words = new WordLookup(() => _japanese, () => _chinese, _dictionaries, () => _levels);
        _controller = new LookupController(() => _settings, _ocr, words, _router, _eyes, _library, _popup, vm, _speech, log);
        _services.AddWord = _controller.AddWordAsync;
        // «In a game»: the program of the last lookup is still in front (not Glossa itself).
        _router.InGame = () => _controller.LastAppExe is { Length: > 0 } last
                               && !last.Equals("Glossa.exe", StringComparison.OrdinalIgnoreCase)
                               && LookupController.ForegroundExe().Equals(last, StringComparison.OrdinalIgnoreCase);
        _controller.Reported += _services.Report;
        _popup.SaveRequested += _controller.SaveCurrent;
        _popup.SpeakRequested += _controller.Speak;
        _popup.IsVisibleChanged += (_, _) => OnPopupVisibility(_popup.IsVisible);
        _popup.CorrectRequested += StartCorrection;
        _popup.CorrectionSubmitted += word => _ = _controller.CorrectAsync(word);
        // While the word is typed in, S, P, Tab, Space and Esc are letters and keys of the input, not the card's.
        _popup.CorrectingChanged += correcting => OnPopupVisibility(_popup.IsVisible && !correcting);
        vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(LookupViewModel.IsSaved) && vm.IsSaved) _services.NotifyLibraryChanged();
        };
        _controller.QuoteKept += _services.NotifyLibraryChanged;
        _controller.ActivityCounted += _services.NotifyActivity;
        // Settings apply as they change; the AI is restarted only when something it runs on changed.
        _aiState = AiState(_settings);
        _eyesState = _settings.Eyes.Device;
        _services.SettingsChanged += () =>
        {
            var ai = AiState(_settings);
            if (ai != _aiState)
            {
                _aiState = ai;
                _router.Reset(); // the eyes with it
            }
            else if (_settings.Eyes.Device != _eyesState)
            {
                _eyes.Reset(); // only the eyes move: the model stays loaded
            }
            _eyesState = _settings.Eyes.Device;
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
            _controller.Counting = false;
            _ = RunSelfTestAsync(st.Cases, st.Out, st.Count, log);
            return;
        }

        // P4: game profiles, the mouse button and the gamepad, the still frame and the pause of a game.
        _guard = new Games.ResumeGuardClient(log);
        _input = new InputThread(log);
        _pad = new GamepadHub(_input, () => _settings, Dispatcher);
        _sessions = new LookupSessions(() => _settings, _games, _capture, _ocr, words, _controller, _popup, _guard, _pad, log, () => _library!.List());
        _pad.ComboPressed += _sessions.PadCombo;
        _pad.MousePressed += _sessions.Pointer;
        _sessions.Notice += text => ShowBalloon(NoticeKind.Nothing, null, 6000, "Glossa", text, WinForms.ToolTipIcon.Warning);
        _sessions.LiveChanged += ApplyQuoteKey;
        _services.SettingsChanged += () => ApplyQuoteKey(_sessions.LiveRunning); // «Сохранять цитаты» switched while live runs
        _services.RecordGamepad = _pad.Record;
        _services.CancelGamepadRecording = _pad.CancelRecording;

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
        _tray = CreateTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var (kind, target) = _notice.TakeClick();
            if (kind == NoticeKind.Study) ShowMain(MainTab.Study);
            else if (kind == NoticeKind.Update) _services!.OpenReleasePage(target);
        };
        _tray.BalloonTipClosed += (_, _) => _notice.Closed();
        _reminders.Tick += (_, _) => CheckReminders();
        _reminders.Start();
        StartUpdateCheck(log);
        if (!_hotkeys.Register(HotkeyLookup, _settings.Hotkey))
            ShowBalloon(NoticeKind.Nothing, null, 5000, "Glossa", $"Не удалось занять {_settings.Hotkey} - клавиша занята другой программой. Смените её в настройках.", WinForms.ToolTipIcon.Warning);
        else
            ShowBalloon(NoticeKind.Nothing, null, 3000, "Glossa", $"Наведите курсор на слово и нажмите {_settings.Hotkey}.", WinForms.ToolTipIcon.Info);
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
        // While a key is being recorded RegisterHotKey would swallow the current combination before the recorder sees it.
        _services.SuspendHotkeys = suspend =>
        {
            if (suspend)
            {
                _hotkeys.Unregister(HotkeyLookup);
                _hotkeys.Unregister(HotkeyWindow);
                _padSuspendGeneration++;
                _pad.Suspended = true;
                return;
            }
            if (_lookupOn && !_hotkeys.Register(HotkeyLookup, _settings.Hotkey))
                log.Warn($"lookup hotkey {_settings.Hotkey} could not be registered again after recording");
            if (_settings.WindowHotkey.Length > 0 && !_hotkeys.Register(HotkeyWindow, _settings.WindowHotkey))
                log.Warn($"window hotkey {_settings.WindowHotkey} could not be registered again after recording");
            // The mouse button that ended the recording reaches the Raw Input path a moment later: keep it silent a little longer.
            var generation = _padSuspendGeneration;
            _ = Task.Delay(400).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (generation == _padSuspendGeneration) _pad.Suspended = false;
            }), TaskScheduler.Default);
        };

        _ = Task.Run(() => Warm(log));

        // Started by hand: show the window. Autostart passes --tray and stays quiet.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMain();
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
                try { Dispatcher.Invoke(() => { if (which == 0) ShowMain(); else Shutdown(); }); }
                catch (TaskCanceledException) { return; }
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "Glossa activation" }.Start();
        return true;
    }

    /// <summary>What the AI runs on: models, mode, endpoints, engines. A change restarts it (unloads the model).</summary>
    private static string AiState(AppSettings s) =>
        System.Text.Json.JsonSerializer.Serialize(new { s.LocalAi, s.CustomEndpoints, s.DictionaryEngine, s.TranslatorEngine, s.Performance.Priority, s.Performance.VisionReading });

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

    /// <summary>Live translation's «в цитаты» key: held while it runs and quotes are saved, let go otherwise.</summary>
    private void ApplyQuoteKey(bool live)
    {
        if (_hotkeys is not { } hotkeys) return;
        hotkeys.Unregister(HotkeyQuote);
        var key = _settings.Quotes.LiveHotkey;
        if (live && _settings.Quotes.Save && key.Length > 0 && !hotkeys.Register(HotkeyQuote, key))
            _log?.Warn($"live quote key {key} is taken by another program");
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyLookup: _sessions?.Pointer(_settings.Hotkey); break;
            case HotkeyClose: _sessions?.Escape(); break;
            case HotkeyWindow: ShowMain(); break;
            case HotkeySave: _controller?.SaveCurrent(); break;
            case HotkeySpeak: _controller?.Speak(); break;
            case HotkeyDetails: _controller?.OnDetails(); break;
            case HotkeyReveal: _popup?.RevealTranslation(); break;
            case HotkeyCorrect: StartCorrection(); break;
            case HotkeyQuote: _sessions?.KeepLiveQuote(); break;
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _reminders = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime? _reminderShown;

    /// <summary>What a click on the tray notice that is on screen does.</summary>
    /// <summary>
    /// The one way to show a tray notice: it says what a click on it opens (plain notices: nothing), so a click can never
    /// open what an earlier notice meant. Safe from any thread.
    /// </summary>
    private void ShowBalloon(NoticeKind kind, string? target, int milliseconds, string title, string text, WinForms.ToolTipIcon icon)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowBalloon(kind, target, milliseconds, title, text, icon));
            return;
        }
        if (_tray is null) return;
        _notice.Show(kind, target, TimeSpan.FromMilliseconds(milliseconds));
        _tray.ShowBalloonTip(milliseconds, title, text, icon);
    }

    /// <summary>
    /// The once-a-day look at GitHub for a newer version (Настройки -> Приложение -> Обновления). The first check waits a
    /// minute so the start and the first lookup are not slowed; the answer is applied on the UI thread; a newer
    /// version gives one tray notice, a click opens its release page. Nothing is downloaded or installed.
    /// </summary>
    private void StartUpdateCheck(ILog log)
    {
        _updatesStop = new CancellationTokenSource();
        // Clients of their own (no cookies, no redirects, no compression): the shared ones carry the AI's and the downloads' settings.
        // The order is the dictionaries': the system proxy first, then direct.
        _updateProxied = UpdateChecker.CreateClient(useSystemProxy: true);
        _updateDirect = UpdateChecker.CreateClient(useSystemProxy: false);
        _updates = _services!.Updates = new UpdateService(() => _settings, () => _store!.Save(_settings),
            new UpdateChecker(_updateProxied, _updateDirect), AppVersion.Current, log, post: work => Dispatcher.InvokeAsync(work).Task);
        // A newer version is told in the window, not the tray (the user, 2026-10-04): now if it is open, else when it opens.
        _updates.Announce += _ => _main?.TellUpdate();
        // The switch turned on: a due check runs now, not at the next hourly wake-up.
        _updatesWereOn = _settings.Updates.CheckForUpdates;
        _services.SettingsChanged += () =>
        {
            var on = _settings.Updates.CheckForUpdates;
            if (on && !_updatesWereOn) _ = _updates.CheckSoon();
            _updatesWereOn = on;
        };
        log.Info($"Glossa {AppVersion.Current}, update check {(_settings.Updates.CheckForUpdates ? "on" : "off")}");
        _ = _updates.RunAsync(UpdateCheck.FirstCheckDelay, UpdateCheck.PollInterval, _updatesStop.Token);
    }

    /// <summary>
    /// The evening reminders (Настройки → Учёба): a tray notice near 18:00 and 20:00 on a day with nothing studied and
    /// cards waiting; a click on it opens «Учёба». Not in «Только перевод», where there is no study.
    /// </summary>
    private void CheckReminders()
    {
        try
        {
            if (_tray is null || _library is null || _settings.Purpose == "translate") return;
            var times = _settings.Study.ReminderSchedule();
            var local = DateTime.Now;
            // Outside every reminder's window nothing is read from the library.
            if (!times.Any(t => local >= local.Date + t.ToTimeSpan() && local < local.Date + t.ToTimeSpan() + Glossa.Core.Study.StudyReminders.Window))
                return;
            var now = DateTime.UtcNow;
            var clock = Glossa.Core.Study.StudyClock.Local;
            var today = clock.Day(now);
            var answered = _library.Answers(now.AddDays(-2)).Where(a => clock.Day(a.AnsweredUtc) == today).ToList();
            var study = _settings.Study;
            var plan = Glossa.Core.Study.SessionBuilder.Build(_library.List(), _library.ReviewStates(), answered, study.Limits(), study.Config(), clock, now);
            if (Glossa.Core.Study.StudyReminders.Due(local, times, _reminderShown, answered.Count > 0, plan.Cards.Count) is null) return;
            _reminderShown = local;
            // In the companion's voice when there is one (user, 2026-10-03): its name over its line. Only the companion
            // already met - a reminder never rolls one.
            var cards = plan.Cards.Count;
            var companion = _services?.Companions?.Active(now) is { } bond ? _services.Companions.Catalog.Find(bond.CompanionId) : null;
            var line = companion is null ? null
                : Glossa.Core.Companions.CompanionSpeech.Say(companion, Glossa.Core.Companions.SpeechEvents.StudyDue, Random.Shared, null, cards);
            ShowBalloon(NoticeKind.Study, null, 8000, line is null ? "Glossa" : companion!.Name,
                line ?? Glossa.Core.Study.StudyReminders.Text(cards), WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log?.Warn($"reminder: {ex.Message}");
        }
    }

    /// <summary>F2 or a click on the word in the card: the word becomes an input, the spellings to pick follow.</summary>
    private async void StartCorrection()
    {
        if (_popup is null || _controller is null || _controller.CurrentWord is not { } word) return;
        var session = _popup.BeginCorrection(word);
        if (session == 0) return;
        try
        {
            var controller = _controller;
            _popup.SetCorrectionChoices(session, await Task.Run(controller.CorrectionChoices));
        }
        catch (Exception ex)
        {
            _log?.Warn($"correction choices: {ex.Message}");
        }
    }

    /// <summary>Opens (or brings back) the main window on the given page, «Главная» unless another is asked for.</summary>
    private void ShowMain(MainTab tab = MainTab.Home)
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

    /// <summary>
    /// Esc, S, P, Tab, F2 (and Space in training mode) belong to the card only while it is on screen and its word is not
    /// being typed in.
    /// </summary>
    private void OnPopupVisibility(bool visible)
    {
        if (_hotkeys is null) return;
        if (visible && _popup?.IsCorrecting != true)
        {
            foreach (var key in Glossa.Core.Input.KeySpec.CardKeys)
            {
                if (key == "Space" && !_settings.Popup.HideTranslation) continue;
                _hotkeys.Register(CardKeyId(key), key);
            }
        }
        else
        {
            _hotkeys.Unregister(HotkeyClose);
            _hotkeys.Unregister(HotkeySave);
            _hotkeys.Unregister(HotkeySpeak);
            _hotkeys.Unregister(HotkeyDetails);
            _hotkeys.Unregister(HotkeyCorrect);
            _hotkeys.Unregister(HotkeyReveal);
        }
    }

    /// <summary>The hotkey id of one of <c>KeySpec.CardKeys</c>.</summary>
    private static int CardKeyId(string key) => key switch
    {
        "Escape" => HotkeyClose,
        "S" => HotkeySave,
        "P" => HotkeySpeak,
        "Tab" => HotkeyDetails,
        "F2" => HotkeyCorrect,
        "Space" => HotkeyReveal,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a card key"),
    };

    /// <summary>The icon itself stays a Windows tray icon; its menu is Glossa's own window (mockup «Меню в трее»).</summary>
    private WinForms.NotifyIcon CreateTray()
    {
        var tray = new WinForms.NotifyIcon { Icon = TrayIcon(), Text = "Glossa - экранный словарь", Visible = true };
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) ShowTrayMenu();
        };
        tray.DoubleClick += (_, _) => ShowMain();
        return tray;
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is null)
        {
            var menu = _trayMenu = new TrayMenu();
            menu.OpenRequested += () => ShowMain(MainTab.Words); // «Открыть словарь» («Главная» in «Только перевод»)
            menu.StudyRequested += () => ShowMain(MainTab.Study);
            menu.SettingsRequested += () => ShowMain(MainTab.Settings);
            menu.UpdateRequested += () => _services!.OpenReleasePage(_updates?.Pending?.Url);
            menu.ExitRequested += Shutdown;
            menu.LookupToggled += SetLookup;
            menu.AiToggled += ToggleModel;
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
        var status = s.DictionaryEngine != "local" ? $"ИИ: \"{s.DictionaryEngine}\""
            : s.LocalAi.Mode == "off" ? "ИИ выключен, только справочники"
            : loaded ? $"ИИ готова, {Views.Settings.ProfileTile.Title(s.LocalAi.Profile)}"
            : !s.LocalAi.HasModel(s.LocalAi.Profile)
                ? $"ИИ {Views.Settings.ProfileTile.Absent(s.LocalAi.Profile)}, {Views.Settings.ProfileTile.Title(s.LocalAi.Profile)}"
            : !s.LocalAi.HasRuntime() ? "ИИ: движок llama.cpp не скачан"
            : "ИИ выгружена, загрузится при поиске";
        var mode = s.LocalAi.Mode;
        var canLoad = s.DictionaryEngine == "local" && mode != "off" && s.LocalAi.HasModel(s.LocalAi.Profile) && s.LocalAi.HasRuntime();
        return new TrayState(status, loaded, mode, s.Hotkey, _lookupOn, s.Purpose == "translate",
            _updates?.Pending is { } pending ? UpdateTexts.TrayItem(pending.Version) : null, canLoad,
            _router?.Loading == true || _router?.UnloadPending == true);
    }

    /// <summary>The tray's "Загрузить модель в фон" / "Выгрузить модель" (2026-10-03); the outcome in a notice.</summary>
    private async void ToggleModel()
    {
        if (_router is not { } router || router.Loading || router.UnloadPending) return;
        var unload = router.Current?.Dictionary is not null;
        string text;
        var icon = WinForms.ToolTipIcon.Info;
        try
        {
            if (unload)
            {
                if (router.UnloadNow()) return;
                text = "Модель занята поиском - выгрузится, как только он закончится.";
            }
            else
            {
                await router.PreloadAsync(CancellationToken.None);
                text = "Модель загружена и держится в фоне - Alt+Q не ждёт загрузки.";
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"AI: {(unload ? "unload" : "load")} by hand failed: {ex.Message}");
            text = ex is Glossa.Core.Llm.LlmException ? ex.Message
                : unload ? "Модель не выгрузилась - подробности в журнале." : "Модель не загрузилась - подробности в журнале.";
            icon = WinForms.ToolTipIcon.Warning;
        }
        try
        {
            ShowBalloon(NoticeKind.Nothing, null, icon == WinForms.ToolTipIcon.Info ? 5000 : 8000, "Glossa", text, icon);
        }
        catch (Exception ex)
        {
            _log?.Error("AI notice", ex);
        }
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
            ShowBalloon(NoticeKind.Nothing, null, 4000, "Glossa", $"{_settings.Hotkey} занято другой программой.", WinForms.ToolTipIcon.Warning);
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
        _updates?.Stop();
        _updatesStop?.Cancel();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _hotkeys?.Dispose();
        _guard?.Dispose(); // a paused game wakes before Glossa goes
        _input?.Dispose();
        _router?.Dispose();
        _eyes?.Dispose();
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
        _updateProxied?.Dispose();
        _updateDirect?.Dispose();
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        _activate?.Dispose();
        _exit?.Dispose();
        base.OnExit(e);
    }
}
