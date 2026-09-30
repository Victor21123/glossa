using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glossa.Core.Games;
using Glossa.Core.Llm;

namespace Glossa.Core.Config;

public sealed class AppSettings
{
    /// <summary>Hotkey for a lookup, e.g. "Alt+Q".</summary>
    public string Hotkey { get; set; } = "Alt+Q";

    /// <summary>Opens the dictionary window over everything, e.g. "Ctrl+Alt+G"; empty — none.</summary>
    public string WindowHotkey { get; set; } = "Ctrl+Alt+G";

    /// <summary>Mouse button that looks up the word under the cursor: none, x1 (button 4, «назад») or x2 (button 5, «вперёд»).</summary>
    public string MouseButton { get; set; } = "none";

    /// <summary>Gamepad combination that opens a still frame to choose a word on, e.g. "LB+RB"; empty — none.</summary>
    public string GamepadCombo { get; set; } = "";

    /// <summary>What happens to the game during a lookup: none, frame (a still of the screen) or pause (its process sleeps).</summary>
    public string DuringLookup { get; set; } = "none";

    /// <summary>On a still frame, words already in the dictionary get a thin frame, «Не могу запомнить» ones a bold one.</summary>
    public bool MarkKnownWords { get; set; } = true;

    /// <summary>
    /// A game the card takes out of exclusive full screen (<see cref="GameProfile.CardKnocksOut"/>) gets its cards on
    /// another monitor, where they leave it in full screen (decided 2026-09-30; with one monitor it changes nothing).
    /// </summary>
    public bool CardOnOtherMonitor { get; set; }

    /// <summary>dictionary (Alt+Q: the word card, saved to the dictionary) or translate (Alt+Q: translation only, <see cref="TranslateMode"/>).</summary>
    public string Purpose { get; set; } = "dictionary";

    /// <summary>
    /// How «Только перевод» translates: zone (Alt+Q stills the screen, the text drawn around with the mouse is translated
    /// in a small card; a click takes the paragraph under it), screen (every paragraph of a still of the screen, laid over
    /// it) or live (new dialogue lines as subtitles while the game runs; Alt+Q switches it on and off). «Зона» replaced
    /// «Реплика» (line) on 2026-09-29 at the user's word.
    /// </summary>
    public string TranslateMode { get; set; } = "zone";

    /// <summary>Настройки → Игры и профили: the programs words were looked up in, each created at its first lookup.</summary>
    public List<GameProfile> Games { get; set; } = [];

    /// <summary>The user's language: translations go here.</summary>
    public string NativeLanguage { get; set; } = "ru";

    /// <summary>Language of the text on screen: auto, en, ja, zh, ru. Picks the OCR model.</summary>
    public string ScreenLanguage { get; set; } = "auto";

    /// <summary>
    /// How to read Han-only text. Japanese sentences almost always contain kana, so text without any is
    /// treated as Chinese unless the user says otherwise.
    /// </summary>
    public string PreferredCjk { get; set; } = "zh";

    public bool AutoSaveWords { get; set; } = true;

    public LocalAiSettings LocalAi { get; set; } = new();

    /// <summary>User-supplied cloud or third-party endpoints. API keys are stored separately, encrypted.</summary>
    public List<CustomEndpoint> CustomEndpoints { get; set; } = [];

    /// <summary>"local" or the name of a custom endpoint.</summary>
    public string DictionaryEngine { get; set; } = "local";

    /// <summary>"local" or the name of a custom endpoint.</summary>
    public string TranslatorEngine { get; set; } = "local";

    public PopupSettings Popup { get; set; } = new();

    public AnkiSettings Anki { get; set; } = new();

    public QuoteSettings Quotes { get; set; } = new();

    public DictionarySettings Dictionaries { get; set; } = new();

    public bool StartWithWindows { get; set; }

    /// <summary>The window's close button hides Glossa to the tray (true) or quits it.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>«Главная» shows the statistics (days in a row, lookups by day, languages, games).</summary>
    public bool HomeStats { get; set; } = true;

    /// <summary>
    /// A full day with Glossa: this many actions (words looked up, study answers, lines translated). Fewer still keep
    /// the series and show paler (decided 2026-09-30).
    /// </summary>
    public int DayGoal { get; set; } = 10;

    /// <summary>Window theme: system (follow Windows), dark, light or disco.</summary>
    public string Theme { get; set; } = "system";

    public SpeechSettings Speech { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public StudySettings Study { get; set; } = new();

    /// <summary>Настройки -> ИИ и модели -> Глаза: the small model that reads stylized text for the models that cannot.</summary>
    public EyesSettings Eyes { get; set; } = new();

    /// <summary>Saves each OCR input image to the logs folder and logs lookup geometry.</summary>
    public bool DebugOcrDumps { get; set; }
}

/// <summary>
/// The eyes (<see cref="Llm.ModelCatalog.Eyes"/>): read the text by the picture where the recognizer found none or a
/// scrap, for the models whose own sight misreads stylized text (Gemma 4 12B, E4B, one's own model).
/// </summary>
public sealed class EyesSettings
{
    /// <summary>
    /// cpu (default: the video memory stays the model's and the game's; ~11 s a reading on an i5-11400), gpu (~1 s,
    /// but 4.2 GB of video memory: on a 12 GB card every card of the 12B 19-57% slower and the game may freeze - the user
    /// is warned and chooses) or off (the model's own sight reads, poorly).
    /// </summary>
    public string Device { get; set; } = "cpu";

    /// <summary>Minutes without a reading before the eyes leave memory; 0 - never.</summary>
    public int IdleUnloadMinutes { get; set; } = 15;

    public void Normalize()
    {
        if (Device is not ("cpu" or "gpu" or "off")) Device = "cpu";
        if (IdleUnloadMinutes < 0) IdleUnloadMinutes = 0;
    }
}

/// <summary>Настройки → Нагрузка на ПК: keeping the game smooth while Glossa runs beside it.</summary>
public sealed class PerformanceSettings
{
    /// <summary>low (the game gets the processor first) or normal: Glossa, its recognition and llama-server.</summary>
    public string Priority { get; set; } = "low";

    /// <summary>Processor threads for text recognition; more is faster per lookup but loads the game's cores.</summary>
    public int OcrThreads { get; set; } = 4;

    /// <summary>Watch free video memory while a model is loaded and unload it when a game needs the room.</summary>
    public bool YieldVram { get; set; } = true;

    /// <summary>Minutes without lookups before unloading while the game of the last lookup is in front; 0 — as outside games.</summary>
    public int GameIdleUnloadMinutes { get; set; } = 3;

    /// <summary>Dictionaries first; the AI card only on Tab (at once when the dictionaries have nothing).</summary>
    public bool AiOnDemand { get; set; }

    /// <summary>The same word in the same line opens from memory, without the AI.</summary>
    public bool CacheCards { get; set; } = true;

    /// <summary>
    /// A word no dictionary knows, or one the recognizer was unsure of, is read again by the local model from the
    /// picture (Gemma 26B with its sight file). Where the sight runs, selftest on Persona 5 frames, RTX 5060 Ti 16 GB,
    /// 2026-09-29 (whole AI card, median): off - 3.3 s, 2.8 s of processor per lookup; cpu - 3.6 s, a word read again
    /// 6.8-7.7 s and ~31 s of processor; gpu - the sight's 1.2 GB push part of the model to RAM: 4.6 s and ~33 s of
    /// processor on every lookup, a reading 0.9 s.
    /// </summary>
    public string VisionReading { get; set; } = "cpu";
}

/// <summary>Настройки → Учёба: the session around Anki's scheduler and what the card shows (Anki's defaults).</summary>
public sealed class StudySettings
{
    /// <summary>Cards in one session; pinned words take up to <see cref="PinnedShare"/> of it.</summary>
    public int SessionSize { get; set; } = 20;

    /// <summary>New cards a day, as Anki counts them: with both directions each word has two.</summary>
    public int NewPerDay { get; set; } = 20;

    public int ReviewsPerDay { get; set; } = 200;

    /// <summary>The share of a session "Не могу запомнить" words may take, 0..1 (a slider, 0-100% in steps of 10).</summary>
    public double PinnedShare { get; set; } = 0.5;

    /// <summary>
    /// forward (слово -> перевод), reverse (перевод -> слово) or both: two cards a word, each on its own schedule. The
    /// cards of a direction switched off keep their schedule and resume when it is switched back on.
    /// </summary>
    public string Direction { get; set; } = "forward";

    /// <summary>With both directions: one card of a word per session, the other waits for a later one (Anki's "bury siblings").</summary>
    public bool BurySiblings { get; set; } = true;

    /// <summary>Learning steps in minutes (Anki: 1 and 10).</summary>
    public List<float> LearnSteps { get; set; } = [1, 10];

    /// <summary>Steps after "Снова" on a learned word, in minutes.</summary>
    public List<float> RelearnSteps { get; set; } = [10];

    /// <summary>Days after the last learning step with "Нормально" and at once with "Легко".</summary>
    public int GraduatingInterval { get; set; } = 1;

    public int EasyInterval { get; set; } = 4;

    /// <summary>"Дополнительно", Anki's advanced options with Anki's ranges: the ease a word graduates with (1.31-5).</summary>
    public float StartingEase { get; set; } = 2.5f;

    /// <summary>The extra of "Легко" over "Нормально" (1-5).</summary>
    public float EasyBonus { get; set; } = 1.3f;

    /// <summary>The interval "Сложно" gives, as a multiple of the last one (0.5-1.3).</summary>
    public float HardMultiplier { get; set; } = 1.2f;

    /// <summary>All review intervals are multiplied by it (0.5-2).</summary>
    public float IntervalModifier { get; set; } = 1f;

    /// <summary>The longest review interval in days (1-36500).</summary>
    public int MaximumInterval { get; set; } = 36500;

    /// <summary>lookups (looked up most often first), recent or random: which new words come first.</summary>
    public string NewOrder { get; set; } = "lookups";

    /// <summary>Offer to unpin a word after right answers on three different days.</summary>
    public bool SuggestUnpin { get; set; } = true;

    /// <summary>The front of the card: the game frame around the word, the line, the reading.</summary>
    public bool FrontShot { get; set; } = true;

    public bool FrontLine { get; set; } = true;
    public bool FrontReading { get; set; } = true;

    /// <summary>The back: the meaning picture, the line's translation, speaking the word as it opens.</summary>
    public bool BackPicture { get; set; } = true;

    public bool BackLineTranslation { get; set; } = true;
    public bool SpeakOnFlip { get; set; }

    /// <summary>Evening reminders by the computer's clock, only on a day with nothing studied yet.</summary>
    public bool Reminders { get; set; } = true;

    public List<string> ReminderTimes { get; set; } = ["18:00", "20:00"];

    /// <summary>The scheduler's options, held to the ranges Anki's deck options allow.</summary>
    public Study.StudyConfig Config() => new()
    {
        LearnSteps = Steps(LearnSteps, [1, 10]),
        RelearnSteps = Steps(RelearnSteps, [10]),
        GraduatingInterval = Math.Max(GraduatingInterval, 1),
        EasyInterval = Math.Max(EasyInterval, 1),
        StartingEase = Range(StartingEase, 1.31f, 5f, 2.5f),
        EasyBonus = Range(EasyBonus, 1f, 5f, 1.3f),
        HardMultiplier = Range(HardMultiplier, 0.5f, 1.3f, 1.2f),
        IntervalModifier = Range(IntervalModifier, 0.5f, 2f, 1f),
        MaximumInterval = Math.Clamp(MaximumInterval, 1, 36500),
    };

    public Study.StudyLimits Limits() => new(
        Math.Clamp(SessionSize, 1, 500), Math.Max(NewPerDay, 0), Math.Max(ReviewsPerDay, 0),
        double.IsFinite(PinnedShare) ? Math.Clamp(PinnedShare, 0, 1) : 0.5,
        NewOrder switch { "recent" => Study.NewWordOrder.Recent, "random" => Study.NewWordOrder.Random, _ => Study.NewWordOrder.Lookups },
        Direction switch { "reverse" => Study.StudyDirection.Reverse, "both" => Study.StudyDirection.Both, _ => Study.StudyDirection.Forward },
        BurySiblings);

    /// <summary>When to remind: the times that read as "HH:mm", earliest first; none with reminders off.</summary>
    public IReadOnlyList<TimeOnly> ReminderSchedule() => !Reminders ? [] : (ReminderTimes ?? [])
        .Select(t => TimeOnly.TryParseExact(t?.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : (TimeOnly?)null)
        .OfType<TimeOnly>()
        .Distinct()
        .Order()
        .ToList();

    // Anki allows no steps at all (every answer graduates), so an empty list stays empty; steps that are all unusable
    // (zero, negative, not a number) are more likely a slip, and fall back to Anki's.
    private static List<float> Steps(List<float>? minutes, float[] fallback)
    {
        if (minutes is null) return [.. fallback];
        var usable = minutes.Where(m => float.IsFinite(m) && m > 0).ToList();
        return usable.Count > 0 || minutes.Count == 0 ? usable : [.. fallback];
    }

    private static float Range(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class SpeechSettings
{
    /// <summary>Chosen Windows voice per language (voice id); a language without one takes its first installed voice.</summary>
    public Dictionary<string, string> Voices { get; set; } = [];

    /// <summary>Speaking rate, 0.5–2; 1 is normal.</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>0–1.</summary>
    public double Volume { get; set; } = 1.0;
}

public sealed class AnkiSettings
{
    public bool ReverseCards { get; set; }
    public bool IncludeImages { get; set; } = true;
    public bool IncludeAudio { get; set; } = true;

    /// <summary>«Картинка значения» on the back of the note, with its author and license.</summary>
    public bool IncludeMeaningPictures { get; set; } = true;
}

/// <summary>«Цитаты»: lines translated in «Только перевод» kept in «Словарь» (decided 2026-09-30).</summary>
public sealed class QuoteSettings
{
    /// <summary>«Реплика», «Зона» and «Весь экран» keep what they translated; live subtitles only by <see cref="LiveHotkey"/>.</summary>
    public bool Save { get; set; } = true;

    /// <summary>The frame with each quote, downscaled to 1280 px (~90 KB); on unless switched off.</summary>
    public bool SaveFrames { get; set; } = true;

    /// <summary>While live translation is on: the subtitle on screen goes to the quotes. Registered only then.</summary>
    public string LiveHotkey { get; set; } = "Alt+S";
}

public sealed class DictionarySettings
{
    /// <summary>Pack ids in the order they are shown and searched; packs not listed follow by priority.</summary>
    public List<string> Order { get; set; } = [];

    public List<string> Disabled { get; set; } = [];

    /// <summary>Show offline dictionary articles under the AI card.</summary>
    public bool ShowInPopup { get; set; } = true;

    public int EntriesPerDictionary { get; set; } = 2;

    /// <summary>Pass dictionary senses to the AI so it picks a real meaning and a real translation.</summary>
    public bool HintAi { get; set; } = true;

    /// <summary>Keep downloaded source files after a dictionary is built (to rebuild without downloading).</summary>
    public bool KeepSources { get; set; }
}

public sealed class LocalAiSettings
{
    /// <summary>
    /// Which local model serves lookups (card and line translation): gemma26b (MoE, default — best in the comparisons
    /// of 2026-09-28/29), gemma12b (dense, less video memory), light (Gemma 4 E4B — for weak PCs) or custom (any GGUF
    /// file the user picks).
    /// </summary>
    public string Profile { get; set; } = "gemma26b";

    /// <summary>A model file set by hand; empty — the catalog file in <see cref="ModelsFolder"/> (downloaded by a button).</summary>
    public string Gemma26bModel { get; set; } = "";

    public string Gemma12bModel { get; set; } = "";

    /// <summary>«Лёгкая»: Gemma 4 E4B without censorship, for weak PCs (~3 GB of video memory).</summary>
    public string LightModel { get; set; } = "";

    /// <summary>«Своя модель»: any GGUF file of the user's.</summary>
    public string CustomModel { get; set; } = "";

    /// <summary>Where catalog models are downloaded; empty — beside the main model if it is there, else the data folder.</summary>
    public string ModelsFolder { get; set; } = "";

    /// <summary>The folder catalog models go to and are looked for in.</summary>
    public string ModelsFolderResolved()
    {
        if (ModelsFolder.Length > 0) return ModelsFolder;
        foreach (var set in new[] { Gemma26bModel, Gemma12bModel, LightModel })
            if (Path.IsPathRooted(set) && Path.GetDirectoryName(set) is { } dir && Directory.Exists(dir)) return dir;
        return Path.Combine(DataPaths.Root, "models", "llm");
    }

    /// <summary>The model file of a profile: the one set by hand, else the catalog's in the models folder; null for an unknown one.</summary>
    public string? SingleModel(string profile)
    {
        var set = profile switch
        {
            "gemma26b" => Gemma26bModel,
            "gemma12b" => Gemma12bModel,
            "light" => LightModel,
            "custom" => CustomModel,
            _ => null,
        };
        if (set is null) return null;
        if (set.Length > 0 || profile == "custom") return set;
        return Llm.ModelCatalog.For(profile) is { } entry ? Path.Combine(ModelsFolderResolved(), entry.File) : null;
    }

    /// <summary>Whether the profile's model is on disk: a catalog model not downloaded yet, or no file of one's own, is not.</summary>
    public bool HasModel(string profile) => SingleModel(profile) is { Length: > 0 } file && File.Exists(file);

    /// <summary>Where the profile's sight (vision projector) is or goes: beside its model; null for a model without one.</summary>
    public string? VisionFile(string profile) =>
        Llm.ModelCatalog.For(profile)?.Vision is { } part && SingleModel(profile) is { Length: > 0 } model
            ? Path.Combine(Path.GetDirectoryName(model) ?? ModelsFolderResolved(), part.LocalName)
            : null;

    /// <summary>The profile's model is there but its sight is not (a model downloaded before sight was added).</summary>
    public bool LacksVision(string profile) => HasModel(profile) && VisionFile(profile) is { } file && !File.Exists(file);

    /// <summary>The eyes' model and projector: in the models folder, beside the profiles' models.</summary>
    public string EyesModel() => Path.Combine(ModelsFolderResolved(), Llm.ModelCatalog.Eyes.File);

    public string EyesVisionFile() => Path.Combine(ModelsFolderResolved(), Llm.ModelCatalog.Eyes.Vision!.LocalName);

    /// <summary>Both files of the eyes are there (half a download is no eyes).</summary>
    public bool HasEyes() => File.Exists(EyesModel()) && File.Exists(EyesVisionFile());

    /// <summary>The eyes' llama-server port, next to the main model's.</summary>
    public int EyesPort => BasePort + 1;

    /// <summary>
    /// Whether the eyes read for the current profile: they are on and downloaded, and the profile's own sight is not
    /// trusted with stylized text (the 26B reads it itself; one's own model has no catalog sight at all).
    /// </summary>
    public bool UsesEyes(EyesSettings eyes) =>
        eyes.Device != "off" && Llm.ModelCatalog.For(Profile)?.ReadsStylized != true && HasEyes();

    /// <summary>Settings from before 2026-09-29: the retired Qwen + Hy-MT2 pair and its tiers become the default model and modes.</summary>
    public void Normalize()
    {
        if (SingleModel(Profile) is null) Profile = "gemma26b";
        Mode = Mode switch { "quality" or "game" => "auto", "light" => "lowvram", _ => Mode };
    }

    /// <summary>Video memory left free when a model is fitted to the card (for the desktop and a game).</summary>
    public int VramReserveMb { get; set; } = 1536;

    /// <summary>One's own llama-server.exe; empty — the official build downloaded by «Движок» → «Скачать».</summary>
    public string LlamaServerPath { get; set; } = "";

    /// <summary>The official build to run: cuda or vulkan; empty — whichever is downloaded (CUDA first).</summary>
    public string Runtime { get; set; } = "";

    /// <summary>The llama-server that runs the model: one's own if set, else the downloaded build (or where it would go).</summary>
    public string LlamaServerResolved()
    {
        if (LlamaServerPath.Length > 0) return LlamaServerPath;
        IReadOnlyList<Llm.RuntimeEntry> kinds = Llm.RuntimeCatalog.For(Runtime) is { } chosen ? [chosen] : Llm.RuntimeCatalog.Items;
        foreach (var kind in kinds)
            if (File.Exists(kind.Server(DataPaths.Runtime))) return kind.Server(DataPaths.Runtime);
        return kinds[0].Server(DataPaths.Runtime);
    }

    public bool HasRuntime() => File.Exists(LlamaServerResolved());

    /// <summary>The official build in use or to download: the chosen one, else a downloaded one, else <paramref name="recommended"/>.</summary>
    public string RuntimeResolved(string recommended)
    {
        if (Llm.RuntimeCatalog.For(Runtime) is not null) return Runtime;
        foreach (var kind in Llm.RuntimeCatalog.Items)
            if (File.Exists(kind.Server(DataPaths.Runtime))) return kind.Id;
        return recommended;
    }

    public int BasePort { get; set; } = 18091;

    /// <summary>auto (fit to free VRAM), lowvram (keep the model mostly in RAM — for games), off.</summary>
    public string Mode { get; set; } = "auto";

    public int IdleUnloadMinutes { get; set; } = 15;
    public int ContextSize { get; set; } = 4096;
}

public sealed class CustomEndpoint
{
    public string Name { get; set; } = "";
    public LlmProviderKind Kind { get; set; } = LlmProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public StructuredOutputMode Structured { get; set; } = StructuredOutputMode.JsonSchema;
}

public sealed class PopupSettings
{
    /// <summary>less, standard, more (how much of the card is open at once) or custom (<see cref="Custom"/>).</summary>
    public string Preset { get; set; } = "standard";

    /// <summary>app (same as the window), dark, light or disco.</summary>
    public string Theme { get; set; } = "app";

    /// <summary>white (default, inverse highlight), jade, amber, sun, lilac or custom (<see cref="AccentHue"/>); «Диско» keeps its own amber.</summary>
    public string Accent { get; set; } = "white";

    /// <summary>The own accent («Свой»): an OKLCH hue, 0–359.</summary>
    public int AccentHue { get; set; } = 200;

    /// <summary>A tint of the card's background (<see cref="TintHue"/>) instead of the theme's neutral grey; not in «Диско».</summary>
    public bool Tint { get; set; }

    public int TintHue { get; set; } = 250;

    /// <summary>The user's own card, used when <see cref="Preset"/> is custom.</summary>
    public CustomCard Custom { get; set; } = new();

    public double FontScale { get; set; } = 1.0;

    /// <summary>Training mode: the translation stays hidden until Space.</summary>
    public bool HideTranslation { get; set; }

    /// <summary>
    /// Keeps Glossa's windows out of screen capture so they never feed their own OCR. Side effect: they are
    /// also invisible in recordings (ShadowPlay, OBS) and screenshots — turn off to record them.
    /// </summary>
    public bool HideFromCapture { get; set; } = true;

    /// <summary>Pronounce the word as soon as the card opens.</summary>
    public bool AutoPlayAudio { get; set; }
}

/// <summary>
/// «Свой» card: one of the preset layouts made as wide, as see-through and as full as the user wants.
/// </summary>
public sealed class CustomCard
{
    /// <summary>The layout it starts from: less, standard or more.</summary>
    public string Base { get; set; } = "standard";

    /// <summary>Width in pixels, before the text size is applied.</summary>
    public int Width { get; set; } = 560;

    /// <summary>How much of the game shows through the card's background, percent (0 — solid).</summary>
    public int Transparency { get; set; }

    /// <summary>
    /// Parts not shown: reading, pos, level, scene, definition, line, lineTranslation, components, forms, synonyms,
    /// dictionaries, footer.
    /// </summary>
    public List<string> Hidden { get; set; } = [];
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) is { } loaded)
            {
                loaded.LocalAi.Normalize();
                loaded.Eyes.Normalize();
                if (loaded.TranslateMode is not ("screen" or "live")) loaded.TranslateMode = "zone";
                return loaded;
            }
        }
        catch (JsonException)
        {
            // A broken file must not stop the app; keep a copy for the user and start from defaults.
            File.Copy(Path, Path + ".broken", overwrite: true);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>
/// Where Glossa keeps its data: GLOSSA_DATA if set; else a GlossaData folder beside the program (the portable
/// archive ships one with the OCR models, UniDic and CC-CEDICT); else D:\GlossaData if it exists; else a new
/// GlossaData beside the program (a PC without D:).
/// </summary>
public static class DataPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable("GLOSSA_DATA") is { Length: > 0 } env) return env;
        var portable = System.IO.Path.Combine(AppContext.BaseDirectory, "GlossaData");
        if (Directory.Exists(portable)) return portable;
        const string classic = @"D:\GlossaData";
        return Directory.Exists(classic) ? classic : portable;
    }

    public static string Settings => System.IO.Path.Combine(Root, "settings.json");
    public static string Logs => System.IO.Path.Combine(Root, "logs");
    public static string OcrModels => System.IO.Path.Combine(Root, "models", "ocr");
    public static string UniDic => System.IO.Path.Combine(Root, "dict", "unidic-lite");
    public static string Cedict => System.IO.Path.Combine(Root, "dict", "cedict_ts.u8");
    public static string Packs => System.IO.Path.Combine(Root, "dict", "packs");
    public static string Levels => System.IO.Path.Combine(Root, "dict", "levels.db");
    public static string Sources => System.IO.Path.Combine(Root, "sources");
    public static string Work => System.IO.Path.Combine(Root, "tmp");
    public static string Library => System.IO.Path.Combine(Root, "library.db");
    public static string Shots => System.IO.Path.Combine(Root, "shots");

    /// <summary>Frames of the quotes (downscaled), apart from the words' so they can be measured and cleared.</summary>
    public static string QuoteShots => System.IO.Path.Combine(Root, Glossa.Core.Library.ShotStore.QuotesFolder);
    public static string Audio => System.IO.Path.Combine(Root, "audio");
    public static string Keys => System.IO.Path.Combine(Root, "keys.json");

    /// <summary>Downloaded llama.cpp builds, one folder each («b11243-cuda»).</summary>
    public static string Runtime => System.IO.Path.Combine(Root, "llama.cpp");

    /// <summary>Compiled CUDA kernels of llama-server, kept with the data rather than on the system drive.</summary>
    public static string CudaCache => System.IO.Path.Combine(Root, "cuda-cache");
}
