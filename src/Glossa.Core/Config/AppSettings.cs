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

    /// <summary>dictionary (Alt+Q: the word card, saved to the dictionary) or translate (Alt+Q: translation only, <see cref="TranslateMode"/>).</summary>
    public string Purpose { get; set; } = "dictionary";

    /// <summary>
    /// How «Только перевод» translates: line (the paragraph under the cursor, in a small card), screen (every paragraph
    /// of a still of the screen, laid over it) or live (new dialogue lines as subtitles while the game runs; Alt+Q
    /// switches it on and off).
    /// </summary>
    public string TranslateMode { get; set; } = "line";

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

    public DictionarySettings Dictionaries { get; set; } = new();

    public bool StartWithWindows { get; set; }

    /// <summary>The window's close button hides Glossa to the tray (true) or quits it.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>«Главная» shows the statistics (days in a row, lookups by day, languages, games).</summary>
    public bool HomeStats { get; set; } = true;

    /// <summary>Window theme: system (follow Windows), dark, light or disco.</summary>
    public string Theme { get; set; } = "system";

    public SpeechSettings Speech { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    /// <summary>Saves each OCR input image to the logs folder and logs lookup geometry.</summary>
    public bool DebugOcrDumps { get; set; }
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

/// <summary>Where Glossa keeps its data. Defaults to D:\GlossaData; GLOSSA_DATA overrides.</summary>
public static class DataPaths
{
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("GLOSSA_DATA") is { Length: > 0 } env ? env : @"D:\GlossaData";

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
    public static string Audio => System.IO.Path.Combine(Root, "audio");
    public static string Keys => System.IO.Path.Combine(Root, "keys.json");

    /// <summary>Downloaded llama.cpp builds, one folder each («b11243-cuda»).</summary>
    public static string Runtime => System.IO.Path.Combine(Root, "llama.cpp");

    /// <summary>Compiled CUDA kernels of llama-server, kept with the data rather than on the system drive.</summary>
    public static string CudaCache => System.IO.Path.Combine(Root, "cuda-cache");
}
