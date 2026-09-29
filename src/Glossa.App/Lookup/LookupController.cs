using System.Diagnostics;
using Glossa.App.Ai;
using Glossa.App.Capture;
using Glossa.App.Interop;
using Glossa.App.Views;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Library;
using Glossa.Core.Llm;
using Glossa.Core.Logging;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.App.Lookup;

/// <summary>
/// One lookup, end to end: capture → OCR around the cursor → word + context → card skeleton →
/// AI fields streamed in → word saved. A new lookup cancels the one in flight.
/// </summary>
public sealed class LookupController(
    Func<AppSettings> settings,
    OcrEngine ocr,
    WordLookup words,
    AiRouter ai,
    LibraryStore library,
    LookupPopup popup,
    LookupViewModel vm,
    Speech.SpeechService speech,
    ILog log)
{
    // Region around the cursor that is recognized, in physical pixels.
    private const int RoiHalfWidth = 900;
    private const int RoiUp = 260;
    private const int RoiDown = 220;
    private static readonly System.Globalization.CultureInfo Russian = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    private readonly CardService _cards = new();
    private readonly TranslationService _translator = new();
    private CancellationTokenSource? _cts;
    private Pending? _pending;
    private Func<Task>? _deferred;
    private readonly CardCache _cache = new(300);

    /// <summary>The program the last word was looked up in (the AI unloads sooner while it stays in front).</summary>
    public string? LastAppExe { get; private set; }

    /// <summary>How each lookup ended, for the check list in Настройки → Вызов и клавиши.</summary>
    public event Action<LookupReport>? Reported;

    /// <summary>What the current card would save.</summary>
    private sealed class Pending
    {
        public required WordHit Hit { get; init; }
        public required CapturedFrame Frame { get; init; }
        public required string AppExe { get; init; }
        public required string WindowTitle { get; init; }
        public required WordCard Card { get; set; }
        public string? SavedId { get; set; }
    }

    /// <summary>
    /// A lookup at a point of a captured frame, errors reported to the card and the check list. <paramref name="page"/>
    /// is a still frame recognized once as a whole; without it the region around the point is recognized.
    /// </summary>
    public async Task LookupAsync(CapturedFrame frame, int x, int y, LookupContext context, Stopwatch sw, OcrPage? page = null)
    {
        try
        {
            await RunAsync(frame, x, y, context, sw, page);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("Lookup failed", ex);
            vm.Error = ex.Message;
            vm.IsBusy = false;
            Report(context.Trigger, "ошибка: " + ex.Message, ok: false);
        }
    }

    /// <summary>Stops the lookup in flight (a still frame closed under it).</summary>
    public void Cancel()
    {
        _deferred = null;
        _cts?.Cancel();
    }

    private void Report(string trigger, string result, bool ok, double? aiSeconds = null, string? model = null, LookupStages? stages = null) =>
        Reported?.Invoke(new LookupReport(DateTime.Now, trigger, result, ok, aiSeconds, model, stages));

    public void SaveCurrent()
    {
        if (_pending is { } p) Save(p);
    }

    /// <summary>Pronounces the headword of the current card.</summary>
    public async void Speak()
    {
        if (_pending is not { } p) return;
        var text = p.Card.DictionaryForm ?? p.Hit.Word;
        try
        {
            if (!await speech.SpeakAsync(text, p.Card.Language))
                vm.Status = $"Нет голоса для языка \"{Languages.RussianName(p.Card.Language)}\" - установите его: Параметры -> Время и язык -> Речь.";
        }
        catch (Exception ex)
        {
            log.Error("TTS failed", ex);
        }
    }

    /// <summary>
    /// The lookup on a frame already captured, with <paramref name="sw"/> started at the key press. Glossa.exe
    /// --selftest drives it with saved screenshots, so the whole path is checked without touching the screen.
    /// </summary>
    public Task RunAsync(CapturedFrame frame, int x, int y, string title, string exe, Stopwatch sw) =>
        RunAsync(frame, x, y, new LookupContext(settings().Hotkey, title, exe), sw, null);

    private async Task RunAsync(CapturedFrame frame, int x, int y, LookupContext context, Stopwatch sw, OcrPage? whole)
    {
        _deferred = null;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var (title, exe, trigger) = (context.Title, context.Exe, context.Trigger);
        var cursor = new Native.POINT { X = x, Y = y };
        var tCapture = sw.ElapsedMilliseconds;
        long tOcrAt = 0, tPlanAt = 0, tCardAt = 0;
        LookupStages Stages(long? first = null, long? ai = null) =>
            new(tCapture, tOcrAt, tPlanAt, tCardAt, first, ai);
        void Report(string result, bool ok, double? aiSeconds = null, string? model = null, LookupStages? stages = null) =>
            this.Report(trigger, result, ok, aiSeconds, model, stages);

        var s = settings();
        // The game's profile may read its text in one language (Игры и профили), else as in Языки.
        var screenLanguage = context.Choices?.Language ?? s.ScreenLanguage;
        OcrPage page;
        if (whole is not null)
        {
            page = whole; // a still frame: recognized once, every word on it is at hand
        }
        else
        {
            var roi = new PixelRect(cursor.X - RoiHalfWidth, cursor.Y - RoiUp, cursor.X + RoiHalfWidth, cursor.Y + RoiDown);
            var crop = frame.Crop(roi);
            var family = screenLanguage == "ru" ? OcrModelFamily.Cyrillic : OcrModelFamily.CjkLatin;
            page = await ocr.RecognizeAsync(crop.Bgra, crop.Width, crop.Height, crop.Stride, crop.Region, family, ct);
            if (s.DebugOcrDumps)
            {
                var dump = ShotStore.SaveJpeg(DataPaths.Logs, crop.Bgra, crop.Width, crop.Height, crop.Stride, quality: 92);
                log.Info($"debug: cursor=({cursor.X},{cursor.Y}) monitor={frame.Bounds} roi={crop.Region} " +
                         $"size={crop.Width}x{crop.Height} lines={page.Lines.Count} dump={dump}");
            }
        }
        ct.ThrowIfCancellationRequested();
        var tOcr = tOcrAt = sw.ElapsedMilliseconds;

        // A game always in one language (Языки, or its profile) skips the guessing: its text is read as that language.
        var forced = screenLanguage is "en" or "ja" or "zh" ? screenLanguage : null;
        var cjk = forced is "ja" or "zh" ? forced : s.PreferredCjk;
        var hit = words.Hit(page, cursor.X, cursor.Y, cjk);
        if (hit is null)
        {
            vm.ShowMessage("Под курсором не найден текст");
            popup.ShowNear(new PixelRect(cursor.X, cursor.Y, cursor.X + 1, cursor.Y + 1));
            log.Info($"lookup: no text (capture {tCapture} ms, ocr {tOcr} ms, {page.Lines.Count} lines)");
            Report("под курсором нет текста", ok: false, stages: Stages());
            return;
        }

        // Offline dictionaries answer in milliseconds, before the AI model is even loaded.
        var ds = s.Dictionaries;
        var plan = await Task.Run(() => words.Plan(hit, cjk, s.NativeLanguage, ds, forced), ct);
        // A newer lookup started meanwhile: do not show this card over it.
        ct.ThrowIfCancellationRequested();
        tPlanAt = sw.ElapsedMilliseconds;
        var (lang, target, seed, sections) = (plan.Language, plan.Target, plan.Seed, plan.Sections);

        vm.Begin(hit, seed, lang, library.Contains(lang, seed.DictionaryForm ?? hit.Word));
        if (ds.ShowInPopup) vm.Dictionaries = ToItems(sections);
        popup.ShowNear(hit.Box);
        var tSkeleton = tCardAt = sw.ElapsedMilliseconds;
        log.Info($"lookup '{hit.Word}' [{lang}] capture {tCapture} ms, ocr {tOcr} ms ({page.Elapsed.TotalMilliseconds:F0}), " +
                 $"card {tSkeleton} ms, {sections.Count} dictionaries");

        var pending = _pending = new Pending { Hit = hit, Frame = frame, AppExe = exe, WindowTitle = title, Card = seed };
        LastAppExe = exe;
        if (s.Popup.AutoPlayAudio) Speak();

        // The same word in the same line again (a re-read dialogue, a menu): the card is already known.
        var cacheKey = $"{lang}|{target}|{s.DictionaryEngine}|{s.LocalAi.Profile}|{hit.Word}|{hit.Context}";
        if (s.Performance.CacheCards && _cache.Get(cacheKey) is { } known)
        {
            vm.Apply(known);
            pending.Card = known;
            if (DictionaryCheck.Of(known.Translation, sections, target) is { } knownCheck) vm.SetDictionaryMark(knownCheck.Matches);
            vm.IsBusy = false;
            vm.Timing = string.Format(Russian, "готовая карточка, {0:0.0} с", sw.Elapsed.TotalSeconds);
            Report($"слово \"{hit.Word}\", готовая карточка", ok: true, stages: Stages(sw.ElapsedMilliseconds, sw.ElapsedMilliseconds));
            if (s.AutoSaveWords) Save(pending);
            return;
        }

        // Dictionaries first, the AI on Tab: the video card works only when the AI is really wanted.
        if (s.Performance.AiOnDemand && sections.Count > 0)
        {
            vm.IsBusy = false;
            vm.Status = "Tab - спросить ИИ";
            _deferred = CardAsync;
            Report($"слово \"{hit.Word}\", справочники, ИИ по Tab", ok: true, stages: Stages());
            return;
        }
        await CardAsync();

        async Task CardAsync()
        {
        // «ИИ в этой игре → Только справочники»: the video card stays the game's.
        if (context.Choices?.Ai == "off")
        {
            vm.Status = sections.Count > 0 ? "ИИ в этой игре выключен - показаны справочники" : "ИИ в этой игре выключен";
            vm.IsBusy = false;
            Report($"слово \"{hit.Word}\", только справочники (профиль игры)", ok: true, stages: Stages());
            return;
        }
        using var busy = ai.Use();
        AiClients clients;
        try
        {
            if (ai.Current is null) vm.Status = "Загружаю модель...";
            clients = await ai.GetAsync(ct, context.Choices?.Ai == "lowvram" ? "lowvram" : null);
            ct.ThrowIfCancellationRequested();
        }
        catch (LlmException ex)
        {
            vm.Error = ex.Message;
            vm.IsBusy = false;
            Report($"слово \"{hit.Word}\", ИИ: {ex.Message}", ok: false, stages: Stages());
            return;
        }
        if (clients.Dictionary is null)
        {
            vm.Status = sections.Count > 0
                ? "ИИ выключен (мало видеопамяти или режим \"выкл.\") - показаны словари"
                : "ИИ выключен - мало видеопамяти или режим \"выкл.\"";
            vm.IsBusy = false;
            Report($"слово \"{hit.Word}\", только справочники", ok: true, stages: Stages());
            return;
        }
        vm.Status = null;

        // The context line comes from its own translation request when the router says so (a dedicated translator,
        // or the local model with its translator prompt); it is queued first, so it shows up first.
        var dedicatedTranslator = clients.SeparateTranslation && clients.Translator is not null;
        var translation = dedicatedTranslator
            ? TranslateAsync(clients.Translator!, hit.Context, lang, target, ct)
            : Task.CompletedTask;

        var request = new CardRequest(hit, lang, target, title, seed, plan.Hint, WithContextTranslation: !dedicatedTranslator);
        long? tFirst = null;
        // The card is repainted at most every 60 ms while the model streams; the first translation shows at once.
        long lastPaint = -1000;
        WordCard? unpainted = null;
        await foreach (var card in _cards.StreamAsync(clients.Dictionary, request, ct))
        {
            pending.Card = card;
            var shown = dedicatedTranslator ? card with { ContextTranslation = null } : card;
            if (tFirst is null && !string.IsNullOrEmpty(card.Translation))
            {
                tFirst = sw.ElapsedMilliseconds;
                lastPaint = -1000;
            }
            if (sw.ElapsedMilliseconds - lastPaint >= 60)
            {
                vm.Apply(shown);
                lastPaint = sw.ElapsedMilliseconds;
                unpainted = null;
            }
            else
            {
                unpainted = shown;
            }
        }
        if (unpainted is not null) vm.Apply(unpainted);
        await translation;
        if (dedicatedTranslator) pending.Card = pending.Card with { ContextTranslation = vm.ContextTranslation };

        // Nothing offline for the form on screen, but the AI named the dictionary form: look that up instead.
        var checkedAgainst = sections;
        if (sections.Count == 0 && ds.ShowInPopup && pending.Card.DictionaryForm is { } lemma && lemma != hit.Word)
        {
            var late = await Task.Run(() => words.LookupLemma(lang, lemma, pending.Card.Reading, ds.EntriesPerDictionary), ct);
            ct.ThrowIfCancellationRequested();
            if (late.Count > 0) vm.Dictionaries = ToItems(late);
            checkedAgainst = late;
        }
        // Shown to the user only; the model never sees it (the context decides the sense).
        if (DictionaryCheck.Of(pending.Card.Translation, checkedAgainst, target) is { } check)
            vm.SetDictionaryMark(check.Matches);

        vm.IsBusy = false;
        // "ИИ 2,1 с · gemma26b": the local profile name or the user's endpoint name, as in the mockups.
        var endpoint = clients.Dictionary.Endpoint;
        vm.Timing = string.Format(Russian, "ИИ {0:0.0} с, {1}", sw.Elapsed.TotalSeconds, endpoint.IsLocal ? s.LocalAi.Profile : endpoint.Name);
        log.Info($"lookup '{hit.Word}' done in {sw.ElapsedMilliseconds} ms via {clients.Description}");
        var model = endpoint.IsLocal ? s.LocalAi.Profile : endpoint.Name;
        Report(string.Format(Russian, "слово \"{0}\", {1:0.0} с", hit.Word, sw.Elapsed.TotalSeconds), ok: true, sw.Elapsed.TotalSeconds, model,
            Stages(tFirst, sw.ElapsedMilliseconds));

        if (pending.Card.Error is null && !ct.IsCancellationRequested) _cache.Put(cacheKey, pending.Card);
        if (s.AutoSaveWords && pending.Card.Error is null && !ct.IsCancellationRequested) Save(pending);
        }
    }

    private readonly Dictionary<string, string> _translations = [];

    /// <summary>The last «Реплика» translation, original and result (Glossa.exe --selftest reports it).</summary>
    public (string Original, string Translation)? LastTranslation { get; private set; }

    /// <summary>
    /// Только перевод, «Реплика»: the paragraph under the point translated whole in a small card; no word card, nothing
    /// saved. Errors go to the card and the check list.
    /// </summary>
    public async Task TranslateLineAsync(CapturedFrame frame, int x, int y, LookupContext context, Stopwatch sw)
    {
        try
        {
            await RunTranslationAsync(frame, x, y, context, sw);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ex is not LlmException) log.Error("Translation failed", ex);
            vm.Error = ex.Message;
            vm.IsBusy = false;
            Report(context.Trigger, "перевод: " + ex.Message, ok: false);
        }
    }

    private async Task RunTranslationAsync(CapturedFrame frame, int x, int y, LookupContext context, Stopwatch sw)
    {
        _deferred = null;
        _pending = null; // S has nothing to save here
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var s = settings();
        var screenLanguage = context.Choices?.Language ?? s.ScreenLanguage;
        var roi = new PixelRect(x - RoiHalfWidth, y - RoiUp, x + RoiHalfWidth, y + RoiDown);
        var crop = frame.Crop(roi);
        var family = screenLanguage == "ru" ? OcrModelFamily.Cyrillic : OcrModelFamily.CjkLatin;
        var page = await ocr.RecognizeAsync(crop.Bgra, crop.Width, crop.Height, crop.Stride, crop.Region, family, ct);
        ct.ThrowIfCancellationRequested();
        var tOcr = sw.ElapsedMilliseconds;

        if (TextBlocks.At(page, x, y) is not { } block)
        {
            vm.ShowMessage("Под курсором не найден текст");
            popup.ShowNear(new PixelRect(x, y, x + 1, y + 1));
            Report(context.Trigger, "под курсором нет текста", ok: false, stages: new LookupStages(0, tOcr, 0, 0, null, null));
            return;
        }
        var forced = screenLanguage is "en" or "ja" or "zh" ? screenLanguage : null;
        var lang = forced ?? Languages.DetectText(block.Text, s.PreferredCjk);
        var target = Languages.TargetFor(lang, s.NativeLanguage);
        vm.BeginTranslation(block.Text, lang);
        popup.ShowNear(block.Box);
        var tCard = sw.ElapsedMilliseconds;
        LastAppExe = context.Exe;
        log.Info($"translate line [{lang}] {block.Text.Length} chars, ocr {tOcr} ms, card {tCard} ms");

        if (ai.Current is null && context.Choices?.Ai != "off") vm.Status = "Загружаю модель...";
        var (text, model) = await TranslateTextAsync(block.Text, lang, target, context.Choices?.Ai, t => vm.ContextTranslation = t, ct);
        vm.ContextTranslation = text;
        LastTranslation = (block.Text, text);
        vm.IsBusy = false;
        vm.Status = null;
        vm.Timing = string.Format(Russian, "ИИ {0:0.0} с, {1}", sw.Elapsed.TotalSeconds, model);
        Report(context.Trigger, string.Format(Russian, "перевод реплики, {0:0.0} с", sw.Elapsed.TotalSeconds), ok: true, sw.Elapsed.TotalSeconds,
            model, new LookupStages(0, tOcr, 0, tCard, null, sw.ElapsedMilliseconds));
    }

    /// <summary>
    /// A text translated by the translator model (with its prompt, loop cut-off and length limit), streamed to
    /// <paramref name="shown"/> on the caller's context; the same text again comes from memory at once. The three ways
    /// of «Только перевод» share it. Throws <see cref="LlmException"/> when the AI is off or not there.
    /// </summary>
    public async Task<(string Text, string Model)> TranslateTextAsync(string text, string lang, string target, string? gameAi,
        Action<string>? shown, CancellationToken ct)
    {
        var key = $"{target}|{TextBlocks.Key(text)}";
        lock (_translations)
        {
            if (_translations.TryGetValue(key, out var known))
            {
                shown?.Invoke(known);
                return (known, "готовый перевод");
            }
        }
        if (gameAi == "off") throw new LlmException("ИИ в этой игре выключен - Игры и профили.");
        using var busy = ai.Use();
        var clients = await ai.GetAsync(ct, gameAi == "lowvram" ? "lowvram" : null);
        if (clients.Translator is not { } llm) throw new LlmException("ИИ выключен: режим \"Выключен\" или мало видеопамяти.");
        var result = "";
        await foreach (var sofar in _translator.StreamAsync(llm, text, lang, target, ct))
        {
            result = sofar.Trim();
            shown?.Invoke(result);
        }
        lock (_translations)
        {
            if (_translations.Count >= 500) _translations.Clear();
            _translations[key] = result;
        }
        return (result, llm.Endpoint.IsLocal ? settings().LocalAi.Profile : llm.Endpoint.Name);
    }

    /// <summary>Tab in the card: asks the AI when the card waits for it (AI on Tab), otherwise folds or unfolds as usual.</summary>
    public async void OnDetails()
    {
        if (Interlocked.Exchange(ref _deferred, null) is not { } run)
        {
            popup.ToggleDetails();
            return;
        }
        vm.Status = null;
        vm.IsBusy = true;
        try
        {
            await run();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("Lookup failed", ex);
            vm.Error = ex.Message;
            vm.IsBusy = false;
        }
    }

    /// <summary>The program whose window is in front now ("game.exe"), or "".</summary>
    public static string ForegroundExe() => ProcessName(Native.GetForegroundWindow());

    private async Task TranslateAsync(ILlmClient llm, string text, string source, string target, CancellationToken ct)
    {
        await foreach (var sofar in _translator.StreamAsync(llm, text, source, target, ct))
            vm.ContextTranslation = sofar.Trim();
    }

    /// <summary>
    /// «Новое слово»: a word typed in by hand goes through the same dictionaries and AI card as a lookup in a game,
    /// just without a screenshot. It is recorded at once with what the dictionaries know, then completed by the AI.
    /// </summary>
    public async Task<string> AddWordAsync(NewWord input, CancellationToken ct)
    {
        var s = settings();
        var word = input.Word.Trim();
        var sentence = string.IsNullOrWhiteSpace(input.Sentence) ? null : input.Sentence.Trim();
        var context = sentence ?? word;
        var offset = Math.Max(0, context.IndexOf(word, StringComparison.OrdinalIgnoreCase));
        var hit = new WordHit(word, default, context, context, offset, Scripts.Dominant(word));
        var plan = await Task.Run(() => words.Plan(hit, s.PreferredCjk, s.NativeLanguage, s.Dictionaries, input.Language), ct);

        SavedWord Saved(WordCard card) => SavedWord.FromCard(card, sentence ?? "", sentence is null ? -1 : offset) with
        {
            Context = sentence,
            WindowTitle = input.Game,
        };
        var id = library.Record(Saved(plan.Seed), newLookup: true);

        var clients = await ai.GetAsync(ct);
        if (clients.Dictionary is null) return id;
        var request = new CardRequest(hit, plan.Language, plan.Target, input.Game, plan.Seed, plan.Hint, WithContextTranslation: sentence is not null);
        var card = plan.Seed;
        await foreach (var c in _cards.StreamAsync(clients.Dictionary, request, ct)) card = c;
        if (card.Error is not null) log.Warn($"add '{word}': {card.Error}");
        library.Record(Saved(card) with { Id = id }, newLookup: false);
        log.Info($"added '{word}' [{plan.Language}] by hand");
        return id;
    }

    private static IReadOnlyList<DictSectionItem> ToItems(IReadOnlyList<DictSection> sections) =>
        sections.Select(s => new DictSectionItem(s.Pack.Title,
            s.Entries.Select(e => new DictEntryItem(e.Headword, e.Reading == e.Headword ? null : e.Reading, e.Body)).ToList())).ToList();

    private void Save(Pending p)
    {
        try
        {
            var shot = ShotStore.SaveJpeg(DataPaths.Root, p.Frame.Bgra, p.Frame.Width, p.Frame.Height, p.Frame.Stride);
            var box = p.Hit.Box.Offset(-p.Frame.Bounds.Left, -p.Frame.Bounds.Top);
            var word = SavedWord.FromCard(p.Card, p.Hit.Context, p.Hit.ContextOffset) with
            {
                AppExe = p.AppExe,
                WindowTitle = p.WindowTitle,
                ShotFile = shot,
                WordBox = box,
            };
            // The first save of a lookup counts it; saving the same lookup again (S after autosave) only refreshes it.
            p.SavedId = library.Record(p.SavedId is null ? word : word with { Id = p.SavedId }, newLookup: p.SavedId is null);
            vm.IsSaved = true;
        }
        catch (Exception ex)
        {
            log.Error("Save failed", ex);
            vm.Error = "Не удалось сохранить слово: " + ex.Message;
        }
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return "";
        }
    }
}

/// <summary>
/// Where a lookup came from: the keys or button that started it ("Alt+Q", "Кнопка 5", "LB+RB"), the game's window title
/// and program ("P5R.exe", stored with the word), and what the game's profile chose (language, AI), if any.
/// </summary>
public sealed record LookupContext(string Trigger, string Title, string Exe, Glossa.Core.Games.GameChoices? Choices = null);

/// <summary>One lookup as the user saw it: when, which keys, what came of it; the AI time when there was a card.</summary>
public sealed record LookupReport(DateTime At, string Keys, string Result, bool Ok, double? AiSeconds = null, string? Model = null,
    LookupStages? Stages = null);

/// <summary>When each stage finished, in ms from the key press: screenshot, text recognition, dictionaries, card on
/// screen, first AI field (the translation), whole AI card.</summary>
public sealed record LookupStages(long CaptureMs, long OcrMs, long DictionariesMs, long CardMs, long? FirstFieldMs, long? AiMs);
