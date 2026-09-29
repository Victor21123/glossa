using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;
using Xunit.Abstractions;

namespace Glossa.Tests.Llm;

/// <summary>
/// Runs against llama-server instances when they are up (8091: instruct model, 8092: translator).
/// Silently passes when nothing listens, so the suite stays green on a clean machine.
/// </summary>
public class LiveLlmTests(ITestOutputHelper output)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(90) };

    private static async Task<bool> UpAsync(int port)
    {
        try { return (await Http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
    }

    [Fact]
    public async Task Card_streams_fields_in_russian()
    {
        if (!await UpAsync(8091)) return;
        var llm = new OpenAiCompatibleClient(Http,
            new LlmEndpoint("local", LlmProviderKind.LlamaServer, "http://127.0.0.1:8091/v1", "qwen3.5-9b"));

        const string context = "I can't put up with his whining any longer, so I'm leaving the party tonight.";
        var hit = new WordHit("put", new PixelRect(0, 0, 1, 1), context, context, 6, Script.Latin);
        var req = new CardRequest(hit, "en", "ru", "Disco Elysium", new WordCard { Word = "put", Language = "en" });

        var updates = 0;
        WordCard? last = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? firstTranslation = null;
        await foreach (var card in new CardService().StreamAsync(llm, req, CancellationToken.None))
        {
            updates++;
            last = card;
            if (firstTranslation is null && card.Translation is not null) firstTranslation = sw.Elapsed;
        }

        output.WriteLine($"updates={updates} first translation={firstTranslation?.TotalMilliseconds:F0} ms total={sw.ElapsedMilliseconds} ms");
        output.WriteLine($"{last!.DictionaryForm} | {last.Translation} | {last.Definition} | {last.ContextTranslation} | {last.Level} | {string.Join(", ", last.Synonyms)}");
        Assert.True(updates > 3);
        Assert.Null(last.Error);
        Assert.False(Scripts.ContainsCjk(last.Translation!));
        Assert.Contains("терп", last.Translation!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Games swear and have adult scenes: the card and the translators must keep the register (no softening,
    /// no refusals). Prints the results for review; fails only on errors or an empty translation.
    /// </summary>
    [Fact]
    public async Task Mature_game_text_keeps_its_register()
    {
        if (!await UpAsync(8091)) return;
        var llm = new OpenAiCompatibleClient(Http,
            new LlmEndpoint("local", LlmProviderKind.LlamaServer, "http://127.0.0.1:8091/v1", "qwen3.5-9b"));
        var cases = new (string Lang, string Word, string? Lemma, string? Reading, string Context)[]
        {
            ("en", "bastard", null, null, "Get the fuck out of my tavern, you drunk bastard!"),
            ("en", "whore", null, null, "Everyone in this town knows she's a whore, half the guards have fucked her."),
            ("ja", "クソ野郎", "クソ野郎", null, "うるせえ、このクソ野郎！さっさと失せろ！"),
            ("zh", "他妈的", "他妈的", "tāmāde", "你他妈的给我滚出去，傻逼！"),
        };
        // As in the app: installed dictionaries give the model real senses and real Russian equivalents.
        using var dictionaries = new Glossa.Core.Dictionaries.DictionaryService(Glossa.Core.Config.DataPaths.Packs);
        dictionaries.Reload([], []);
        foreach (var (lang, word, lemma, reading, context) in cases)
        {
            var hit = new WordHit(word, new PixelRect(0, 0, 1, 1), context, context, context.IndexOf(word, StringComparison.Ordinal), Script.Latin);
            var hint = Glossa.Core.Dictionaries.DictionaryService.Hint(dictionaries.Lookup(lang, [lemma ?? word]), "ru");
            var req = new CardRequest(hit, lang, "ru", "Test RPG",
                new WordCard { Word = word, Language = lang, DictionaryForm = lemma, Reading = reading }, hint);
            WordCard? last = null;
            await foreach (var card in new CardService().StreamAsync(llm, req, CancellationToken.None)) last = card;
            output.WriteLine($"[{lang}] {word}: {last!.Translation} | {last.PartOfSpeech} | {last.Definition} | {last.ContextTranslation} | err={last.Error}");
            Assert.Null(last.Error);
            Assert.False(string.IsNullOrWhiteSpace(last.Translation));
        }

        foreach (var (port, model) in new[] { (8092, "hy-mt2-7b"), (8091, "qwen3.5-9b") })
        {
            if (!await UpAsync(port)) continue;
            var translator = new OpenAiCompatibleClient(Http,
                new LlmEndpoint("local", LlmProviderKind.LlamaServer, $"http://127.0.0.1:{port}/v1", model));
            foreach (var (lang, _, _, _, context) in cases)
            {
                var text = await translator.CompleteAsync(TranslationService.BuildRequest(translator.Endpoint, context, lang, "ru"), CancellationToken.None);
                output.WriteLine($"{model} [{lang}] {text.Trim()}");
            }
        }
    }

    [Fact]
    public async Task Translator_prompt_formats_work()
    {
        foreach (var (port, model) in new[] { (8092, "hy-mt2-7b"), (8091, "qwen3.5-9b") })
        {
            if (!await UpAsync(port)) continue;
            var llm = new OpenAiCompatibleClient(Http,
                new LlmEndpoint("local", LlmProviderKind.LlamaServer, $"http://127.0.0.1:{port}/v1", model));
            var text = "";
            await foreach (var sofar in new TranslationService()
                .StreamAsync(llm, "別に心配しなくていいよ。俺に任せとけ。", "ja", "ru", CancellationToken.None))
                text = sofar;

            output.WriteLine($"{model}: {text}");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.False(Scripts.ContainsCjk(text));
        }
    }
}
