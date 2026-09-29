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
        ? "в «Диско» свой, янтарный"
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
        ? $"вне игр — как в «ИИ и моделях», через {S.LocalAi.IdleUnloadMinutes} мин"
        : "вне игр модель не выгружается («ИИ и модели»)";

    public bool AiOnDemand { get => S.Performance.AiOnDemand; set => Set(() => S.Performance.AiOnDemand = value); }

    public bool CacheCards { get => S.Performance.CacheCards; set => Set(() => S.Performance.CacheCards = value); }

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
