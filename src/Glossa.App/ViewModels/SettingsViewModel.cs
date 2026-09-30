using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.App.Speech;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Lookup;
using Microsoft.Win32;

namespace Glossa.App.ViewModels;

/// <summary>
/// The plain settings of Настройки, written straight into <see cref="AppSettings"/> as they change. There is no
/// «Сохранить»: the file is written and the app told a moment later, so a slider drag makes one write, not fifty.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly AppServices _services;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _save.Tick += (_, _) => Flush();
        services.ChangedElsewhere += OnChangedElsewhere;
        Voices = new[] { "en", "ja", "zh" }.Select(l => new VoiceRow(l, services, () => Changed(nameof(Voices)))).ToList();
    }

    private AppSettings S => _services.Settings;

    // ---- Карточка слова ----

    public string Preset { get => S.Popup.Preset; set => Set(() => S.Popup.Preset = value, also: nameof(IsCustom)); }

    /// <summary>«Свой»: its own rows (layout, width, transparency, parts) are shown.</summary>
    public bool IsCustom => S.Popup.Preset == "custom";

    /// <summary>less, standard or more: the layout «Свой» starts from.</summary>
    public string CustomBase { get => S.Popup.Custom.Base; set => Set(() => S.Popup.Custom.Base = value); }

    /// <summary>360–800 px in steps of 20.</summary>
    public double CustomWidth
    {
        get => S.Popup.Custom.Width;
        set => Set(() => S.Popup.Custom.Width = (int)(Math.Round(Math.Clamp(value, 360, 800) / 20) * 20), also: nameof(CustomWidthText));
    }

    public string CustomWidthText => $"{S.Popup.Custom.Width} пикс.";

    /// <summary>0–60 % in steps of 5: how much of the game shows through the card.</summary>
    public double CustomTransparency
    {
        get => S.Popup.Custom.Transparency;
        set => Set(() => S.Popup.Custom.Transparency = (int)(Math.Round(Math.Clamp(value, 0, 60) / 5) * 5), also: nameof(CustomTransparencyText));
    }

    public string CustomTransparencyText => S.Popup.Custom.Transparency == 0 ? "нет" : $"{S.Popup.Custom.Transparency}%";

    public bool IsPartShown(string part) => !S.Popup.Custom.Hidden.Contains(part);

    public void SetPartShown(string part, bool shown) => Set(() =>
    {
        S.Popup.Custom.Hidden.Remove(part);
        if (!shown) S.Popup.Custom.Hidden.Add(part);
    }, name: "CustomParts");

    /// <summary>The own accent's OKLCH hue, 0–359.</summary>
    public double AccentHue { get => S.Popup.AccentHue; set => Set(() => S.Popup.AccentHue = (int)Math.Round(value) % 360); }

    public bool IsCustomAccent => S.Popup.Accent == "custom";

    /// <summary>A tint of the card's background instead of the theme's neutral grey.</summary>
    public bool Tint { get => S.Popup.Tint; set => Set(() => S.Popup.Tint = value); }

    public double TintHue { get => S.Popup.TintHue; set => Set(() => S.Popup.TintHue = (int)Math.Round(value) % 360); }

    /// <summary>app (as the window), dark, light, disco.</summary>
    public string CardTheme
    {
        get => S.Popup.Theme;
        set => Set(() => S.Popup.Theme = value, also: nameof(AccentHint));
    }

    public string Accent { get => S.Popup.Accent; set => Set(() => S.Popup.Accent = value, also: nameof(IsCustomAccent)); }

    /// <summary>85–130 %, as the slider shows it.</summary>
    public double FontScale
    {
        get => Math.Round(S.Popup.FontScale * 100);
        set => Set(() => S.Popup.FontScale = Math.Round(Math.Clamp(value, 85, 130) / 5) * 5 / 100, also: nameof(FontScaleText));
    }

    public string FontScaleText => $"{FontScale:0}%";

    public bool HideTranslation { get => S.Popup.HideTranslation; set => Set(() => S.Popup.HideTranslation = value); }

    public bool HideFromCapture { get => S.Popup.HideFromCapture; set => Set(() => S.Popup.HideFromCapture = value); }

    public string AccentHint => _services.Theme.CardKind(CardTheme) == ThemeKind.Disco
        ? "в \"Диско\" свой, янтарный"
        : "подсветка слова и отметки";

    // ---- Вызов и клавиши ----

    public string Hotkey => S.Hotkey;

    public bool HotkeyActive => _services.HotkeyActive?.Invoke() ?? true;

    /// <summary>Registers the combination; false when another program holds it (the old one stays).</summary>
    public bool ChangeHotkey(string spec)
    {
        var ok = _services.ChangeHotkey?.Invoke(spec) ?? false;
        OnPropertyChanged(nameof(Hotkey));
        OnPropertyChanged(nameof(HotkeyActive));
        return ok;
    }

    /// <summary>«Открыть Glossa»; empty — none.</summary>
    public string WindowHotkey => S.WindowHotkey;

    public bool WindowHotkeyActive => _services.WindowHotkeyActive?.Invoke() ?? true;

    public bool ChangeWindowHotkey(string spec)
    {
        var ok = _services.ChangeWindowHotkey?.Invoke(spec) ?? false;
        OnPropertyChanged(nameof(WindowHotkey));
        OnPropertyChanged(nameof(WindowHotkeyActive));
        return ok;
    }

    /// <summary>none, x1 (button 4) or x2 (button 5).</summary>
    public string MouseButton { get => S.MouseButton; set => Set(() => S.MouseButton = value); }

    /// <summary>"LB+RB", or empty.</summary>
    public string GamepadCombo
    {
        get => S.GamepadCombo;
        set => Set(() => S.GamepadCombo = value ?? "");
    }

    /// <summary>none, frame or pause: what a lookup does to the game (a game's profile may choose otherwise).</summary>
    public string DuringLookup { get => S.DuringLookup; set => Set(() => S.DuringLookup = value); }

    /// <summary>dictionary or translate: what the lookup keys do.</summary>
    public string Purpose
    {
        get => S.Purpose;
        set => Set(() => S.Purpose = value, also: nameof(IsTranslatePurpose));
    }

    public bool IsTranslatePurpose => S.Purpose == "translate";

    public string PurposeNote => $"Словарь и учёба: {S.Hotkey} открывает карточку слова, слово попадает в словарь. Только перевод: " +
                                 "Glossa переводит текст из игры и ничего не сохраняет - для тех, кому нужен просто перевод.";

    /// <summary>zone, screen or live: how «Только перевод» translates.</summary>
    public string TranslateMode
    {
        get => S.TranslateMode;
        set => Set(() => S.TranslateMode = value, also: nameof(TranslateModeNote));
    }

    public string TranslateModeNote => S.TranslateMode switch
    {
        "screen" => $"Весь экран: {S.Hotkey} останавливает кадр, и поверх каждого абзаца ложится его перевод; щелчок по переводу " +
                    "показывает оригинал, Esc - назад в игру. Геймпад в этом режиме всегда переводит весь экран. Переводы идут по " +
                    "одному, ~1 с на абзац.",
        "live" => $"Живой перевод: {S.Hotkey} включает его в игре, повторное нажатие выключает. Glossa следит за экраном и " +
                  "переводит каждую новую реплику субтитром над ней, пока игра впереди. Не включается в играх с античитом и не " +
                  "виден в эксклюзивном полноэкранном режиме. Нагрузка: ~0,4 с распознавания при смене текста и ~1 с перевода на реплику.",
        _ => $"Зона: нажми {S.Hotkey} - кадр остановится; обведи мышью текст, и рядом появится перевод всего, что внутри, ~1,5 с " +
             "(трудные слова модель перечитывает, до 3,6 с на каждое). Щелчок без рамки - абзац под курсором, Esc - назад в игру. " +
             "Геймпад в этом режиме переводит весь экран.",
    };

    /// <summary>Frames on the still frame around words already in the dictionary.</summary>
    public bool MarkKnownWords { get => S.MarkKnownWords; set => Set(() => S.MarkKnownWords = value); }

    // ---- ИИ и модели ----

    /// <summary>gemma26b, gemma12b, light or custom.</summary>
    public string AiProfile { get => S.LocalAi.Profile; set => Set(() => S.LocalAi.Profile = value); }

    /// <summary>auto, lowvram or off.</summary>
    public string AiMode
    {
        get => S.LocalAi.Mode;
        set => Set(() => S.LocalAi.Mode = value, also: nameof(AiModeNote));
    }

    public string AiModeNote => string.Format(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
        "Авто: модель занимает свободную видеопамять, оставляя {0:0.#} ГБ системе и игре. Минимум: около 3 ГБ, карточка в 3 раза медленнее. " +
        "Выключен: только справочники.", S.LocalAi.VramReserveMb / 1024.0);

    /// <summary>Minutes without lookups before the model leaves video memory; 0 keeps it loaded.</summary>
    public int IdleUnloadMinutes { get => S.LocalAi.IdleUnloadMinutes; set => Set(() => S.LocalAi.IdleUnloadMinutes = value); }

    public int VramReserveMb { get => S.LocalAi.VramReserveMb; set => Set(() => S.LocalAi.VramReserveMb = value, also: nameof(AiModeNote)); }

    public string Gemma26bModel { get => S.LocalAi.Gemma26bModel; set => Set(() => S.LocalAi.Gemma26bModel = value.Trim()); }

    public string Gemma12bModel { get => S.LocalAi.Gemma12bModel; set => Set(() => S.LocalAi.Gemma12bModel = value.Trim()); }

    public string LightModel { get => S.LocalAi.LightModel; set => Set(() => S.LocalAi.LightModel = value.Trim()); }

    public string CustomModel { get => S.LocalAi.CustomModel; set => Set(() => S.LocalAi.CustomModel = value.Trim()); }

    /// <summary>Where «Скачать» puts models; shown resolved (empty in settings means «beside the main model»).</summary>
    public string ModelsFolder
    {
        get => S.LocalAi.ModelsFolderResolved();
        set => Set(() => S.LocalAi.ModelsFolder = value.Trim());
    }

    public string LlamaServerPath { get => S.LocalAi.LlamaServerPath; set => Set(() => S.LocalAi.LlamaServerPath = value.Trim()); }

    private static readonly Lazy<string> RecommendedRuntime = new(() => Ai.AiRouter.RecommendedRuntime().Id);

    /// <summary>cuda or vulkan: the official llama.cpp build (until chosen — the downloaded one, else the one for this card).</summary>
    public string Runtime
    {
        get => S.LocalAi.RuntimeResolved(RecommendedRuntime.Value);
        set => Set(() => S.LocalAi.Runtime = value);
    }

    /// <summary>"local" or a user endpoint's name: who writes the word card.</summary>
    public string DictionaryEngine { get => S.DictionaryEngine; set => Set(() => S.DictionaryEngine = value ?? "local"); }

    /// <summary>"local" or a user endpoint's name: who translates the line.</summary>
    public string TranslatorEngine { get => S.TranslatorEngine; set => Set(() => S.TranslatorEngine = value ?? "local"); }

    public IReadOnlyList<EngineChoice> Engines =>
        [new("local", "Локальная модель"), .. S.CustomEndpoints.Where(e => e.Name.Length > 0).Select(e => new EngineChoice(e.Name, e.Name))];

    /// <summary>
    /// The endpoints list changed (added, removed, renamed): engines that pointed at a missing one fall back to the
    /// local model, and the change is saved like any other.
    /// </summary>
    public void EndpointsChanged()
    {
        var names = S.CustomEndpoints.Select(e => e.Name).ToHashSet();
        if (S.DictionaryEngine != "local" && !names.Contains(S.DictionaryEngine)) S.DictionaryEngine = "local";
        if (S.TranslatorEngine != "local" && !names.Contains(S.TranslatorEngine)) S.TranslatorEngine = "local";
        OnPropertyChanged(nameof(Engines));
        OnPropertyChanged(nameof(DictionaryEngine));
        OnPropertyChanged(nameof(TranslatorEngine));
        Changed(null);
    }

    // ---- Нагрузка на ПК ----

    /// <summary>low or normal.</summary>
    public string Priority { get => S.Performance.Priority; set => Set(() => S.Performance.Priority = value); }

    public int OcrThreads { get => S.Performance.OcrThreads; set => Set(() => S.Performance.OcrThreads = value); }

    public bool YieldVram { get => S.Performance.YieldVram; set => Set(() => S.Performance.YieldVram = value); }

    /// <summary>0 — as outside games.</summary>
    public int GameIdleUnloadMinutes
    {
        get => S.Performance.GameIdleUnloadMinutes;
        set => Set(() => S.Performance.GameIdleUnloadMinutes = value);
    }

    public string GameIdleNote => S.LocalAi.IdleUnloadMinutes > 0
        ? $"вне игр - как в \"ИИ и моделях\", через {S.LocalAi.IdleUnloadMinutes} мин"
        : "вне игр модель не выгружается (\"ИИ и модели\")";

    public bool AiOnDemand { get => S.Performance.AiOnDemand; set => Set(() => S.Performance.AiOnDemand = value); }

    public bool CacheCards { get => S.Performance.CacheCards; set => Set(() => S.Performance.CacheCards = value); }

    public string VisionReading { get => S.Performance.VisionReading; set => Set(() => S.Performance.VisionReading = value); }

    // ---- Языки ----

    public string NativeLanguage { get => S.NativeLanguage; set => Set(() => S.NativeLanguage = value); }

    public string ScreenLanguage { get => S.ScreenLanguage; set => Set(() => S.ScreenLanguage = value); }

    public string PreferredCjk { get => S.PreferredCjk; set => Set(() => S.PreferredCjk = value); }

    // ---- Словарь и Anki ----

    public bool AutoSaveWords { get => S.AutoSaveWords; set => Set(() => S.AutoSaveWords = value); }

    public bool AnkiImages { get => S.Anki.IncludeImages; set => Set(() => S.Anki.IncludeImages = value); }

    public bool AnkiAudio { get => S.Anki.IncludeAudio; set => Set(() => S.Anki.IncludeAudio = value); }

    public bool AnkiReverse { get => S.Anki.ReverseCards; set => Set(() => S.Anki.ReverseCards = value); }

    // ---- Озвучка ----

    public IReadOnlyList<VoiceRow> Voices { get; }

    public bool AutoPlayAudio { get => S.Popup.AutoPlayAudio; set => Set(() => S.Popup.AutoPlayAudio = value); }

    /// <summary>50–200 %.</summary>
    public double SpeechRate
    {
        get => Math.Round(S.Speech.Rate * 100);
        set => Set(() => S.Speech.Rate = Math.Round(Math.Clamp(value, 50, 200) / 10) * 10 / 100, also: nameof(SpeechRateText));
    }

    public string SpeechRateText => SpeechRate switch { 100 => "обычная", var r => $"{r:0}%" };

    /// <summary>0–100 %.</summary>
    public double SpeechVolume
    {
        get => Math.Round(S.Speech.Volume * 100);
        set => Set(() => S.Speech.Volume = Math.Round(Math.Clamp(value, 0, 100) / 5) * 5 / 100, also: nameof(SpeechVolumeText));
    }

    public string SpeechVolumeText => $"{SpeechVolume:0}%";

    // ---- Учёба ----

    public int StudySessionSize { get => S.Study.SessionSize; set => Set(() => S.Study.SessionSize = value); }

    public int StudyNewPerDay { get => S.Study.NewPerDay; set => Set(() => S.Study.NewPerDay = value, also: nameof(StudyDirectionNote)); }

    public int StudyReviewsPerDay { get => S.Study.ReviewsPerDay; set => Set(() => S.Study.ReviewsPerDay = value); }

    /// <summary>Steps as the list's presets name them: minutes separated by spaces ("1 10").</summary>
    public string StudyLearnSteps
    {
        get => Steps(S.Study.LearnSteps);
        set => Set(() => S.Study.LearnSteps = ParseSteps(value, S.Study.LearnSteps), also: nameof(StudyLearnStepsNote));
    }

    public string StudyRelearnSteps { get => Steps(S.Study.RelearnSteps); set => Set(() => S.Study.RelearnSteps = ParseSteps(value, S.Study.RelearnSteps)); }

    /// <summary>What "Нормально" does to a new word with these steps (the mockup's note, from the real values).</summary>
    public string StudyLearnStepsNote
    {
        get
        {
            var steps = S.Study.LearnSteps.Where(s => s > 0).ToList();
            var next = steps.Count > 1 ? $"через {Minutes(steps[1])}" : "сразу выученным";
            var days = S.Study.GraduatingInterval;
            return $"новое слово: \"Нормально\" - {next}, после последнего шага - через {Days(days)}";
        }
    }

    public int StudyGraduatingInterval
    {
        get => S.Study.GraduatingInterval;
        set => Set(() => S.Study.GraduatingInterval = value, also: nameof(StudyLearnStepsNote));
    }

    public int StudyEasyInterval { get => S.Study.EasyInterval; set => Set(() => S.Study.EasyInterval = value); }

    /// <summary>0-100 %, in steps of 10.</summary>
    public double StudyPinnedPercent
    {
        get => Math.Round(S.Study.PinnedShare * 100);
        set => Set(() => S.Study.PinnedShare = Math.Round(Math.Clamp(value, 0, 100) / 10) / 10, also: nameof(StudyPinnedText));
    }

    public string StudyPinnedText => StudyPinnedPercent switch
    {
        0 => "не брать отдельно",
        50 => "до половины сессии",
        100 => "вся сессия, если хватит",
        var p => $"до {p:0}% сессии",
    };

    public bool StudySuggestUnpin { get => S.Study.SuggestUnpin; set => Set(() => S.Study.SuggestUnpin = value); }

    public string StudyNewOrder { get => S.Study.NewOrder; set => Set(() => S.Study.NewOrder = value); }

    public string StudyDirection { get => S.Study.Direction; set => Set(() => S.Study.Direction = value, also: nameof(StudyDirectionNote)); }

    public string StudyDirectionNote => S.Study.Direction switch
    {
        "reverse" => "на лице перевод и контекст сцены, вспомнить нужно слово",
        "both" => $"у слова две карточки со своим расписанием, в одну сессию попадает одна; {S.Study.NewPerDay} новых карточек в день - это около {Math.Max(1, S.Study.NewPerDay / 2)} слов",
        _ => "на лице слово, кадр и реплика, вспомнить нужно перевод",
    };

    public bool StudyFrontShot { get => S.Study.FrontShot; set => Set(() => S.Study.FrontShot = value); }
    public bool StudyFrontLine { get => S.Study.FrontLine; set => Set(() => S.Study.FrontLine = value); }
    public bool StudyFrontReading { get => S.Study.FrontReading; set => Set(() => S.Study.FrontReading = value); }
    public bool StudyBackPicture { get => S.Study.BackPicture; set => Set(() => S.Study.BackPicture = value); }
    public bool StudyBackLineTranslation { get => S.Study.BackLineTranslation; set => Set(() => S.Study.BackLineTranslation = value); }
    public bool StudySpeakOnFlip { get => S.Study.SpeakOnFlip; set => Set(() => S.Study.SpeakOnFlip = value); }

    public bool StudyReminders { get => S.Study.Reminders; set => Set(() => S.Study.Reminders = value); }

    /// <summary>The two evening times ("18:00", "20:00"), as the lists name them.</summary>
    public string StudyReminderFirst { get => ReminderAt(0, "18:00"); set => Set(() => SetReminderAt(0, value)); }
    public string StudyReminderSecond { get => ReminderAt(1, "20:00"); set => Set(() => SetReminderAt(1, value)); }

    private string ReminderAt(int i, string fallback) => S.Study.ReminderTimes is { } t && t.Count > i ? t[i] : fallback;

    private void SetReminderAt(int i, string value)
    {
        var times = new List<string> { ReminderAt(0, "18:00"), ReminderAt(1, "20:00") };
        times[i] = value;
        S.Study.ReminderTimes = times;
    }

    // «Дополнительно»: rounded on the way out, so a float setting finds its double preset in the list (2.3f is not 2.3).
    public double StudyStartingEase { get => Math.Round(S.Study.StartingEase, 2); set => Set(() => S.Study.StartingEase = (float)value); }
    public double StudyEasyBonus { get => Math.Round(S.Study.EasyBonus, 2); set => Set(() => S.Study.EasyBonus = (float)value); }
    public double StudyHardMultiplier { get => Math.Round(S.Study.HardMultiplier, 2); set => Set(() => S.Study.HardMultiplier = (float)value); }
    public double StudyIntervalModifier { get => Math.Round(S.Study.IntervalModifier, 2); set => Set(() => S.Study.IntervalModifier = (float)value); }
    public int StudyMaximumInterval { get => S.Study.MaximumInterval; set => Set(() => S.Study.MaximumInterval = value); }
    public bool StudyBurySiblings { get => S.Study.BurySiblings; set => Set(() => S.Study.BurySiblings = value); }

    private static string Steps(IEnumerable<float> steps) =>
        string.Join(" ", steps.Select(s => s.ToString("0.##", CultureInfo.InvariantCulture)));

    private static List<float> ParseSteps(string text, List<float> keep)
    {
        var parsed = new List<float>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0) return keep;
            parsed.Add(minutes);
        }
        return parsed.Count > 0 ? parsed : keep;
    }

    private static string Minutes(float minutes) => minutes switch
    {
        >= 1440 => Days((int)(minutes / 1440)),
        >= 60 => $"{minutes / 60:0.#} ч",
        _ => $"{minutes:0.#} мин",
    };

    private static string Days(int days) => days switch
    {
        1 => "день",
        _ when days % 10 is >= 2 and <= 4 && days % 100 is < 12 or > 14 => $"{days} дня",
        _ => $"{days} дней",
    };

    // ---- Приложение ----

    public string Theme { get => S.Theme; set => Set(() => S.Theme = value, also: nameof(AccentHint)); }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Set(() =>
        {
            S.StartWithWindows = value;
            ApplyAutostart(value);
        });
    }

    public bool CloseToTray { get => S.CloseToTray; set => Set(() => S.CloseToTray = value); }

    /// <summary>Statistics on «Главная».</summary>
    public bool HomeStats { get => S.HomeStats; set => Set(() => S.HomeStats = value); }

    public bool DebugOcrDumps { get => S.DebugOcrDumps; set => Set(() => S.DebugOcrDumps = value); }

    /// <summary>The window is closing: stop listening to the app (it outlives this model).</summary>
    public void Detach() => _services.ChangedElsewhere -= OnChangedElsewhere;

    private void OnChangedElsewhere()
    {
        OnPropertyChanged(nameof(AiMode));
        OnPropertyChanged(nameof(AiModeNote));
    }

    /// <summary>Writes pending changes now (the window is closing, or the app needs them at once).</summary>
    public void Flush()
    {
        if (!_save.IsEnabled) return;
        _save.Stop();
        _services.SaveSettings(S);
        _services.NotifySettingsChanged();
    }

    private void Set(Action write, [CallerMemberName] string? name = null, string? also = null)
    {
        write();
        OnPropertyChanged(name);
        if (also is not null) OnPropertyChanged(also);
        Changed(null);
    }

    private void Changed(string? name)
    {
        if (name is not null) OnPropertyChanged(name);
        _save.Stop();
        _save.Start();
    }

    private void ApplyAutostart(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run is null) return;
            if (on) run.SetValue("Glossa", $"\"{Environment.ProcessPath}\" --tray");
            else run.DeleteValue("Glossa", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("Autostart", ex);
        }
    }
}

/// <summary>Who does the AI work: the local model or one of the user's endpoints.</summary>
public sealed record EngineChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A Windows voice as the picker shows it.</summary>
public sealed record VoiceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One language in Озвучка: its installed voices and the chosen one.</summary>
public sealed class VoiceRow : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action _changed;

    public VoiceRow(string language, AppServices services, Action changed)
    {
        Language = language;
        _services = services;
        _changed = changed;
        Refresh();
    }

    public string Language { get; }
    public string Code => Language.ToUpperInvariant();
    public string Name => Languages.RussianName(Language) is { Length: > 0 } n ? char.ToUpperInvariant(n[0]) + n[1..] : Language;
    public IReadOnlyList<VoiceChoice> Choices { get; private set; } = [];
    public bool HasVoice => Choices.Count > 0;

    public string? VoiceId
    {
        get => _services.Speech.VoiceFor(Language)?.Id;
        set
        {
            if (value is null || value == VoiceId) return;
            _services.Settings.Speech.Voices[Language] = value;
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>Re-reads the installed voices (after the user adds one in Windows).</summary>
    public void Refresh()
    {
        Choices = SpeechService.VoicesFor(Language).Select(v => new VoiceChoice(v.Id, v.DisplayName)).ToList();
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(HasVoice));
        OnPropertyChanged(nameof(VoiceId));
    }
}
