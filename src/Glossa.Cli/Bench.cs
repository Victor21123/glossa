using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using SkiaSharp;

/// <summary>One test: a word on a screenshot, or a word in a line of game text.</summary>
public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string? Lang { get; set; }
    public string? Image { get; set; }
    public string? Context { get; set; }
    public string Word { get; set; } = "";

    /// <summary>What a correct answer must convey (for the reviewer).</summary>
    public string? Expect { get; set; }
}

/// <summary>
/// The app's lookup pipeline without the screen: OCR of an image file, the word at a point, dictionaries, and
/// the AI card and translator against any OpenAI-compatible endpoint (llama-server), with timings.
/// </summary>
public sealed class Bench : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly OcrEngine _ocr = new(DataPaths.OcrModels);
    private readonly JapaneseAnalyzer _ja = new(DataPaths.UniDic);
    private readonly ChineseDictionary _zh = new(DataPaths.Cedict);
    private readonly DictionaryService _dicts = new(DataPaths.Packs);
    private readonly LevelService _levels = new(DataPaths.Levels);
    private readonly WordLookup _words;

    /// <summary>The lookup's word finder, for ocr-eval.</summary>
    internal WordLookup Words => _words;

    private readonly Dictionary<string, OcrPage> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };

    public Bench()
    {
        _dicts.Reload([], []);
        _words = new WordLookup(() => _ja, () => _zh, _dicts, () => _levels);
    }

    public async Task<OcrPage> OcrAsync(string image)
    {
        if (_pages.TryGetValue(image, out var cached)) return cached;
        using var decoded = SKBitmap.Decode(image) ?? throw new InvalidDataException("not an image: " + image);
        using var bgra = decoded.Copy(SKColorType.Bgra8888);
        var bytes = bgra.GetPixelSpan().ToArray();
        var page = await _ocr.RecognizeAsync(bytes, bgra.Width, bgra.Height, bgra.RowBytes,
            new PixelRect(0, 0, bgra.Width, bgra.Height), OcrModelFamily.CjkLatin, CancellationToken.None);
        return _pages[image] = page;
    }

    public async Task<LookupPlan?> PlanAsync(EvalCase c)
    {
        var cjk = c.Lang == "ja" ? "ja" : "zh";
        var settings = new DictionarySettings();
        WordHit? hit;
        if (c.Image is not null)
        {
            var page = await OcrAsync(c.Image);
            // Point at the first character of the word, as a user hovering it would.
            var line = page.Lines.FirstOrDefault(l => l.Text.Contains(c.Word, StringComparison.Ordinal));
            if (line is null) return null;
            var at = line.Text.IndexOf(c.Word, StringComparison.Ordinal);
            var x = line.Box.Left + line.Box.Width * (at + 0.5) / line.Text.Length;
            hit = _words.Hit(page, x, line.Box.CenterY, cjk);
        }
        else
        {
            var context = c.Context ?? c.Word;
            var offset = context.IndexOf(c.Word, StringComparison.Ordinal);
            hit = new WordHit(c.Word, default, context, context, offset, Scripts.Of(c.Word[0]));
        }
        return hit is null ? null : _words.Plan(hit, cjk, "ru", settings);
    }

    /// <summary>A lookup that takes longer than this per word is unusable: the run stops (user's rule).</summary>
    public int MaxCaseMs { get; init; } = 60_000;

    /// <summary>After the first 3 words, a median above this means the model is far too slow to finish the set.</summary>
    public int ProbeMedianMs { get; init; } = 20_000;

    /// <summary>Give the card the dictionary senses (the app's default, <see cref="DictionarySettings.HintAi"/>).</summary>
    public bool HintDictionary { get; init; } = true;

    public async Task EvalAsync(string casesPath, string outPath, string? card, string? translator)
    {
        var cases = JsonSerializer.Deserialize<List<EvalCase>>(await File.ReadAllTextAsync(casesPath), Json)!;
        string? aborted = null;
        var times = new List<long>();
        var cardLlm = card is null ? null : Client(card);
        var trLlm = translator is null ? null : Client(translator);
        var results = new List<object>();
        var cards = new CardService();
        var tr = new TranslationService();

        foreach (var c in cases)
        {
            var plan = await PlanAsync(c);
            if (plan is null)
            {
                results.Add(new { c.Id, c.Word, error = "word not found on the image" });
                Console.WriteLine($"{c.Id}: not found");
                continue;
            }

            // As in the app: the translator answers first (its line is shown first).
            string? translation = null;
            WordCard? last = null;
            long trMs = -1, firstMs = -1, cardMs = -1;
            try
            {
                if (trLlm is not null)
                {
                    var sw = Stopwatch.StartNew();
                    await foreach (var sofar in tr.StreamAsync(trLlm, plan.Hit.Context, plan.Language, plan.Target, CancellationToken.None)) translation = sofar;
                    translation = translation?.Trim();
                    trMs = sw.ElapsedMilliseconds;
                }
                if (cardLlm is not null)
                {
                    var sw = Stopwatch.StartNew();
                    // As in the app: when the line has its own translation request, the card skips it.
                    var req = new CardRequest(plan.Hit, plan.Language, plan.Target, "Game", plan.Seed, HintDictionary ? plan.Hint : null,
                        WithContextTranslation: trLlm is null);
                    await foreach (var partial in cards.StreamAsync(cardLlm, req, CancellationToken.None))
                    {
                        last = partial;
                        if (firstMs < 0 && partial.Translation is not null) firstMs = sw.ElapsedMilliseconds;
                    }
                    cardMs = sw.ElapsedMilliseconds;
                }
            }
            catch (LlmException e)
            {
                // A dead server fails every later case too: keep what is done and stop.
                aborted = $"stopped: {c.Id}: {e.Message}";
            }

            results.Add(new
            {
                c.Id, lang = plan.Language, hint = HintDictionary ? plan.Hint : null, word = plan.Hit.Word, context = plan.Hit.Context, c.Expect,
                form = last?.DictionaryForm ?? plan.Seed.DictionaryForm, reading = last?.Reading ?? plan.Seed.Reading,
                level = last?.Level, pos = last?.PartOfSpeech, register = last?.Register, usage = last?.UsageNote,
                translation = last?.Translation, definition = last?.Definition, definitionTranslation = last?.DefinitionTranslation,
                contextTranslation = last?.ContextTranslation, synonyms = last?.Synonyms, error = last?.Error,
                firstMs, cardMs, translator = translation, trMs,
            });
            Console.WriteLine($"{c.Id} [{plan.Language}] {plan.Hit.Word}: {last?.Translation} | {last?.ContextTranslation} | {translation} ({firstMs}/{cardMs}/{trMs} ms)");

            // Written after every case, so a crash keeps the finished part.
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, Json));
            var total = Math.Max(cardMs, 0) + Math.Max(trMs, 0);
            times.Add(total);
            if (Math.Max(cardMs, trMs) > MaxCaseMs)
                aborted ??= $"stopped: {c.Id} took {total / 1000.0:F0} s (limit {MaxCaseMs / 1000} s per word)";
            else if (times.Count == 3 && times.Order().ElementAt(1) > ProbeMedianMs)
                aborted ??= $"stopped after 3 words: median {times.Order().ElementAt(1) / 1000.0:F0} s per word";
            if (aborted is not null)
            {
                Console.WriteLine(aborted);
                break;
            }
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, Json));
        if (aborted is not null) await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".aborted"), aborted);
    }

    public async Task DictionaryCheckAsync(string casesPath, string resultsPath)
    {
        var cases = JsonSerializer.Deserialize<List<EvalCase>>(await File.ReadAllTextAsync(casesPath), Json)!;
        using var results = JsonDocument.Parse(await File.ReadAllTextAsync(resultsPath));
        var translations = results.RootElement.EnumerateArray()
            .Where(r => r.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String)
            .ToDictionary(r => r.GetProperty("Id").GetString()!, r => r.GetProperty("translation").GetString()!);
        int match = 0, differ = 0, none = 0;
        foreach (var c in cases)
        {
            if (!translations.TryGetValue(c.Id, out var translation) || await PlanAsync(c) is not { } plan) continue;
            var check = DictionaryCheck.Of(translation, plan.Sections, plan.Target);
            var mark = check is null ? "—" : check.Matches ? "✓" : "в словаре: " + string.Join(", ", check.Equivalents);
            if (check is null) none++; else if (check.Matches) match++; else differ++;
            Console.WriteLine($"{c.Id,-22} {translation,-32} {mark}");
        }
        Console.WriteLine($"-- ✓ {match}, differs {differ}, no dictionary {none}");
    }

    private OpenAiCompatibleClient Client(string spec)
    {
        var parts = spec.Split('|');
        return new OpenAiCompatibleClient(_http,
            new LlmEndpoint("bench", LlmProviderKind.LlamaServer, parts[0], parts.Length > 1 ? parts[1] : "local"));
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _ja.Dispose();
        _dicts.Dispose();
        _levels.Dispose();
        _http.Dispose();
    }
}
