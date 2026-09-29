using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Llm;

public class PartialJsonTests
{
    [Fact]
    public void Reads_complete_fields_and_marks_the_streaming_one()
    {
        var s = PartialJson.Read("{\"translation\": \"терпеть\", \"definition\": \"to tolerate somethi");

        Assert.Equal("терпеть", s.Strings["translation"]);
        Assert.Equal("to tolerate somethi", s.Strings["definition"]);
        Assert.Equal("definition", s.StreamingKey);
    }

    [Fact]
    public void Skips_a_code_fence_or_words_before_the_object()
    {
        // An endpoint without a JSON schema may wrap the answer; the card must not come out empty.
        var fenced = PartialJson.Read("```json\n{\"translation\": \"терпеть\"}\n```");
        var spoken = PartialJson.Read("Here is the card: {\"translation\": \"терп");

        Assert.Equal("терпеть", fenced.Strings["translation"]);
        Assert.Equal("терп", spoken.Strings["translation"]);
        Assert.Empty(PartialJson.Read("```json\n").Strings);
    }

    [Fact]
    public void Reads_string_arrays_and_nested_objects()
    {
        const string json = """
            {"synonyms": ["tolerate", "endure"], "components": [{"part": "薄", "meaning": "thin"}], "level": "B1"}
            """;
        var s = PartialJson.Read(json);

        Assert.Equal(["tolerate", "endure"], s.StringArrays["synonyms"]);
        Assert.Contains("\"薄\"", s.RawValues["components"]);
        Assert.Equal("B1", s.Strings["level"]);
        Assert.Null(s.StreamingKey);
    }

    [Fact]
    public void Handles_escapes_and_unfinished_array()
    {
        var s = PartialJson.Read("{\"a\": \"line\\nnext \\\"q\\\"\", \"b\": [\"x\", \"y");

        Assert.Equal("line\nnext \"q\"", s.Strings["a"]);
        Assert.Equal(["x"], s.StringArrays["b"]);
        Assert.Equal("b", s.StreamingKey);
    }

    [Fact]
    public void Card_merge_keeps_verified_fields_and_cleans_synonyms()
    {
        var hit = new WordHit("泣い", new PixelRect(0, 0, 1, 1), "", "泣いていた", 0, Script.Han);
        var seed = new WordCard { Word = "泣い", Language = "ja", DictionaryForm = "泣く", Reading = "なく" };
        var req = new CardRequest(hit, "ja", "ru", null, seed);

        var snap = PartialJson.Read("""
            {"translation_ru": "плакать", "dictionary_form": "泣き", "reading": "なき", "synonyms": ["泣く", "むせぶ", "むせぶ"]}
            """);
        var card = CardService.Clean(CardService.Merge(req, snap, partial: false), req);

        Assert.Equal("泣く", card.DictionaryForm);
        Assert.Equal("なく", card.Reading);
        Assert.Equal("плакать", card.Translation);
        Assert.Equal(["むせぶ"], card.Synonyms);
        Assert.Null(card.Error);
    }

    [Fact]
    public void Card_flags_translation_in_wrong_script()
    {
        var hit = new WordHit("薄暗い", new PixelRect(0, 0, 1, 1), "", "", 0, Script.Han);
        var seed = new WordCard { Word = "薄暗い", Language = "ja" };
        var req = new CardRequest(hit, "ja", "ru", null, seed);

        var card = CardService.Clean(CardService.Merge(req, PartialJson.Read("{\"translation_ru\": \"昏暗的\"}"), false), req);

        Assert.NotNull(card.Error);
    }

    [Fact]
    public void Schema_skips_verified_fields()
    {
        var seed = new WordCard { Word = "出生", Language = "zh", Reading = "chūshēng" };
        var hit = new WordHit("出生", new PixelRect(0, 0, 1, 1), "", "", 0, Script.Han);
        var schema = CardService.Schema(new CardRequest(hit, "zh", "ru", null, seed));
        var props = schema["properties"]!.AsObject();

        Assert.False(props.ContainsKey("reading"));
        Assert.True(props.ContainsKey("components"));
        Assert.Equal("translation_ru", props.First().Key);
        Assert.True(props.ContainsKey("definition_ru")); // Chinese definitions go to the user's language
    }

    [Fact]
    public void An_english_definition_also_comes_in_the_users_language()
    {
        var en = new CardRequest(new WordHit("reconsider", new PixelRect(0, 0, 1, 1), "", "", 0, Script.Latin), "en", "ru", null,
            new WordCard { Word = "reconsider", Language = "en" });
        var props = CardService.Schema(en)["properties"]!.AsObject();
        Assert.True(props.ContainsKey("definition_en"));
        Assert.True(props.ContainsKey("definition_ru"));
        Assert.Contains("\"definition_ru\": the same definition in Russian", CardService.SystemPrompt(en));

        var card = CardService.Merge(en, PartialJson.Read(
            "{\"definition_en\": \"To think again.\", \"definition_ru\": \"Обдумать ещё раз.\"}"), partial: false);
        Assert.Equal(("To think again.", "Обдумать ещё раз."), (card.Definition, card.DefinitionTranslation));

        // Chinese and Japanese definitions are in the user's language already: one field, nothing to translate.
        var zh = new CardRequest(new WordHit("出生", new PixelRect(0, 0, 1, 1), "", "", 0, Script.Han), "zh", "ru", null,
            new WordCard { Word = "出生", Language = "zh" });
        Assert.Null(CardService.Merge(zh, PartialJson.Read("{\"definition_ru\": \"родиться\"}"), false).DefinitionTranslation);
        Assert.DoesNotContain("the same definition in", CardService.SystemPrompt(zh));
    }

    [Fact]
    public void Dictionary_rule_appears_only_with_the_hint()
    {
        var seed = new WordCard { Word = "nuts", Language = "en" };
        var hit = new WordHit("nuts", new PixelRect(0, 0, 1, 1), "Deez nuts!", "Deez nuts!", 5, Script.Latin);
        var bare = CardService.SystemPrompt(new CardRequest(hit, "en", "ru", null, seed));
        var hinted = CardService.SystemPrompt(new CardRequest(hit, "en", "ru", null, seed, "орехи"));

        Assert.DoesNotContain("DICTIONARY SENSES", bare);
        Assert.Contains("The TEXT decides the sense", hinted);
    }

    [Theory]
    [InlineData("Он смотрит на свою заляпанную дерьмомget-out-of-the-wayget-out-of-the-wayget-out-of-the-wayget-out-of-the-wayget-out-of-the-wayget-out-of-the-way", 37)]
    [InlineData("get-get-get-get-get-get-get-get-get", 0)]
    public void Translation_loop_is_found(string text, int start) =>
        Assert.Equal(start, TranslationService.LoopStart(text));

    [Theory]
    [InlineData("Нет-нет-нет-нет-нет! Только не это!")]
    [InlineData("ГАРТЕ, ЗАВЕДУЮЩИЙ СТОЛОВОЙ — «Ты должен мне 130 риалов». ГАРТЕ, ЗАВЕДУЮЩИЙ СТОЛОВОЙ — «Ну?»")]
    [InlineData("Ха-ха-ха-ха-ха-ха")]
    public void Real_repetition_is_not_a_loop(string text) =>
        Assert.Null(TranslationService.LoopStart(text));

    [Fact]
    public void Translator_bans_the_stray_get_token_for_russian_only()
    {
        var llm = new OpenAiCompatibleClient(new HttpClient(),
            new LlmEndpoint("local", LlmProviderKind.LlamaServer, "http://127.0.0.1:1/v1", "gemma26b"));
        var ru = llm.BuildBody(TranslationService.BuildRequest(llm.Endpoint, "Deez nuts!", "en", "ru"));
        var en = llm.BuildBody(TranslationService.BuildRequest(llm.Endpoint, "変態！", "ja", "en"));

        Assert.Equal("""[["get",false]]""", ru["logit_bias"]!.ToJsonString());
        Assert.False(en.ContainsKey("logit_bias"));
    }

    [Fact]
    public void A_picture_goes_first_as_a_data_url_then_the_text()
    {
        var llm = new OpenAiCompatibleClient(new HttpClient(),
            new LlmEndpoint("local", LlmProviderKind.LlamaServer, "http://127.0.0.1:1/v1", "gemma26b"));
        var body = llm.BuildBody(new LlmRequest([new LlmMessage("user", VisionReading.Prompt, [1, 2, 3])]));

        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image_url", (string?)content[0]!["type"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)content[0]!["image_url"]!["url"]);
        Assert.Equal(VisionReading.Prompt, (string?)content[1]!["text"]);
        // Without a picture the content stays a plain string.
        var plain = llm.BuildBody(new LlmRequest([new LlmMessage("user", "hi")]));
        Assert.Equal("hi", (string?)plain["messages"]![0]!["content"]);
    }
}
