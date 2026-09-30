using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Glossa.Tests.Export;

public sealed class ExportTests : IDisposable
{
    private readonly TestFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Sample library in a temp data folder, with a real screenshot for the English word.</summary>
    private (string Root, List<SavedWord> Words) Sample()
    {
        var root = _temp.New();
        Directory.CreateDirectory(Path.Combine(root, "shots"));
        using (var bmp = new SKBitmap(640, 360))
        using (var canvas = new SKCanvas(bmp))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 28))
        using (var paint = new SKPaint { Color = SKColors.White })
        {
            canvas.Clear(new SKColor(30, 40, 30));
            canvas.DrawText("State your business, stranger.", 20, 180, font, paint);
            using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 85);
            File.WriteAllBytes(Path.Combine(root, "shots", "scene.jpg"), data.ToArray());
        }

        // Fixed ids, as a library's are: two exports of the sample are the same notes to Anki (tools\validate_apkg.py).
        var en = new SavedWord
        {
            Id = "0e5a0000000000000000000000000001", Language = "en", Word = "business", DictionaryForm = "business", Reading = "/ˈbɪznɪs/", Level = "B1",
            PartOfSpeech = "noun", Translation = "дело", Definition = "the purpose of your visit",
            Context = "State your business, stranger.", ContextOffset = 11,
            ContextTranslation = "Скажите, в чём дело, незнакомец.", Synonyms = ["purpose", "affair"],
            WindowTitle = "Test Game", AppExe = "game.exe", ShotFile = Path.Combine("shots", "scene.jpg"),
            WordBox = new PixelRect(160, 150, 280, 190),
        };
        var ja = new SavedWord
        {
            Id = "0e5a0000000000000000000000000002", Language = "ja", Word = "薄暗い", DictionaryForm = "薄暗い", Reading = "うすぐらい", Level = "JLPT N2",
            Translation = "сумрачный", Context = "薄暗い所で泣いていた。", ContextOffset = 0,
            Components = [new CardComponent("薄", null, "тонкий"), new CardComponent("暗", null, "тёмный")],
        };
        return (root, [en, ja]);
    }

    [Fact]
    public void Apkg_contains_collection_notes_cards_and_media()
    {
        var (root, words) = Sample();
        // The English word also has a meaning picture, so the copy below lets the real Anki show one.
        Directory.CreateDirectory(Path.Combine(root, "images"));
        File.Copy(Path.Combine(root, "shots", "scene.jpg"), Path.Combine(root, "images", "business-1.jpg"));
        words[0] = words[0] with
        {
            Register = "informal", UsageNote = "вежливый вопрос с угрозой",
            Picture = new Glossa.Core.Pictures.MeaningPicture("images/business-1.jpg", "Wikimedia Commons", "Ann", "CC BY-SA 4.0"),
        };
        var apkg = Path.Combine(root, "out.apkg");

        ApkgWriter.Write(apkg, root, words, new AnkiExportOptions(ReverseCards: true));

        using var zip = ZipFile.OpenRead(apkg);
        Assert.NotNull(zip.GetEntry("collection.anki2"));
        var media = JsonNode.Parse(new StreamReader(zip.GetEntry("media")!.Open()).ReadToEnd())!.AsObject();
        // Only the English word has a screenshot and a meaning picture.
        Assert.Equal(new[] { $"glossa_{words[0].Id}.jpg", "glossa_business-1.jpg" }, media.Select(m => m.Value!.GetValue<string>()));

        var db = Path.Combine(root, "check.anki2");
        zip.GetEntry("collection.anki2")!.ExtractToFile(db);
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM notes), (SELECT COUNT(*) FROM cards), (SELECT guid FROM notes ORDER BY id LIMIT 1)";
        using var r = cmd.ExecuteReader();
        r.Read();
        Assert.Equal(2, r.GetInt32(0));
        Assert.Equal(4, r.GetInt32(1)); // forward + reverse for each note
        Assert.Equal(words[0].Id, r.GetString(2));

        // keep a copy for tools/validate_apkg.py (real Anki import check)
        Directory.CreateDirectory(@"D:\GlossaData\test");
        File.Copy(apkg, @"D:\GlossaData\test\glossa_test.apkg", overwrite: true);
    }

    [Fact]
    public void Context_word_is_highlighted_and_html_escaped()
    {
        var w = new SavedWord { Language = "en", Word = "<b>", Context = "a <b> tag", ContextOffset = 2 };
        var fields = AnkiNoteType.FieldValues(w, false, false, false);
        Assert.Equal("a <b>&lt;b&gt;</b> tag", fields[Array.IndexOf(AnkiNoteType.Fields, "Context")]);
    }

    [Fact]
    public void Quizlet_and_csv_exports()
    {
        var (root, words) = Sample();
        var q = Path.Combine(root, "q.txt");
        var c = Path.Combine(root, "c.csv");
        TextExport.WriteQuizlet(q, words);
        TextExport.WriteCsv(c, words);

        var lines = File.ReadAllLines(q);
        Assert.Equal("business [/ˈbɪznɪs/]\tдело — the purpose of your visit", lines[0]);
        Assert.Equal("薄暗い [うすぐらい]\tсумрачный", lines[1]);
        Assert.Contains("\"Скажите, в чём дело, незнакомец.\"", File.ReadAllText(c));
    }

    [Fact]
    public async Task AnkiConnect_adds_new_notes_updates_existing_and_tags_removed()
    {
        var (root, words) = Sample();
        var fake = new FakeAnki(existingGlossaId: words[1].Id);
        var sync = new AnkiConnectSync(new HttpClient(fake));
        var removed = new SavedWord { Language = "en", Word = "gone", Id = "deadbeef" };
        fake.Existing["deadbeef"] = 99;

        var result = await sync.SyncAsync(root, words, [removed], new AnkiExportOptions(), null, CancellationToken.None);

        Assert.Equal(new AnkiSyncResult(Added: 1, Updated: 1, Tagged: 1), result);
        Assert.Contains("createModel", fake.Actions);
        Assert.Contains("storeMediaFile", fake.Actions);
        Assert.DoesNotContain("deleteNotes", fake.Actions);
    }

    /// <summary>The fields of "Glossa Word v1" before 2026-09-30.</summary>
    private static readonly string[] FirstFields = AnkiNoteType.Fields[..18];

    [Fact]
    public async Task A_note_type_from_an_older_glossa_gets_the_new_fields_and_templates_and_keeps_its_notes()
    {
        var (root, words) = Sample();
        var fake = new FakeAnki(existingGlossaId: words[1].Id, modelFields: FirstFields);

        await new AnkiConnectSync(new HttpClient(fake)).SyncAsync(root, words, [], new AnkiExportOptions(), null, CancellationToken.None);

        Assert.DoesNotContain("createModel", fake.Actions);
        Assert.Equal(new[] { "Register", "Scene", "MeaningImage", "MeaningCredit" },
            fake.Requests.Where(r => r["action"]!.GetValue<string>() == "modelFieldAdd").Select(r => r["params"]!["fieldName"]!.GetValue<string>()));
        var templates = Assert.Single(fake.Requests, r => r["action"]!.GetValue<string>() == "updateModelTemplates");
        Assert.Contains("{{MeaningImage}}", templates["params"]!["model"]!["templates"]![AnkiNoteType.Templates[0].Name]!["Back"]!.GetValue<string>());
        Assert.Contains("updateModelStyling", fake.Actions);
        Assert.Contains("updateNoteFields", fake.Actions); // the old note is updated in place, not added again

        // Up to date: nothing is added and the user's own template changes stay.
        var current = new FakeAnki(existingGlossaId: words[1].Id, modelFields: AnkiNoteType.Fields);
        await new AnkiConnectSync(new HttpClient(current)).SyncAsync(root, words, [], new AnkiExportOptions(), null, CancellationToken.None);
        Assert.DoesNotContain("modelFieldAdd", current.Actions);
        Assert.DoesNotContain("updateModelTemplates", current.Actions);
    }

    [Fact]
    public async Task An_ankiconnect_that_cannot_add_fields_says_to_update_it()
    {
        var (root, words) = Sample();
        var fake = new FakeAnki(existingGlossaId: words[1].Id, modelFields: FirstFields, unsupported: "modelFieldAdd");
        var e = await Assert.ThrowsAsync<AnkiConnectException>(() =>
            new AnkiConnectSync(new HttpClient(fake)).SyncAsync(root, words, [], new AnkiExportOptions(), null, CancellationToken.None));
        Assert.Contains("обновите", e.Message);
    }

    [Fact]
    public void The_meaning_picture_register_and_scene_go_into_the_note()
    {
        var (root, words) = Sample();
        Directory.CreateDirectory(Path.Combine(root, "images"));
        File.WriteAllBytes(Path.Combine(root, "images", "w1-8dd.jpg"), File.ReadAllBytes(Path.Combine(root, "shots", "scene.jpg")));
        var en = words[0] with
        {
            Register = "slang", UsageNote = "вежливый вопрос с угрозой",
            Picture = new Glossa.Core.Pictures.MeaningPicture("images/w1-8dd.jpg", "Wikimedia Commons", "Ann", "CC BY-SA 4.0"),
        };
        var apkg = Path.Combine(root, "out.apkg");

        ApkgWriter.Write(apkg, root, [en], new AnkiExportOptions());

        using var zip = ZipFile.OpenRead(apkg);
        var media = JsonNode.Parse(new StreamReader(zip.GetEntry("media")!.Open()).ReadToEnd())!.AsObject();
        Assert.Contains(media, m => m.Value!.GetValue<string>() == "glossa_w1-8dd.jpg");
        var fields = AnkiNoteType.FieldValues(en, false, false, false, hasMeaning: true);
        string F(string name) => fields[Array.IndexOf(AnkiNoteType.Fields, name)];
        Assert.Equal(("сленг", "вежливый вопрос с угрозой"), (F("Register"), F("Scene")));
        Assert.Equal("<img src=\"glossa_w1-8dd.jpg\">", F("MeaningImage"));
        Assert.Equal("Wikimedia Commons, Ann, CC BY-SA 4.0", F("MeaningCredit"));
        // Without the option the note has no picture and no media for it.
        var plain = Path.Combine(root, "plain.apkg");
        ApkgWriter.Write(plain, root, [en], new AnkiExportOptions(IncludeMeaningPictures: false));
        using var without = ZipFile.OpenRead(plain);
        Assert.DoesNotContain("glossa_w1-8dd.jpg", new StreamReader(without.GetEntry("media")!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task A_word_deleted_from_the_library_is_tagged_in_anki_not_deleted()
    {
        // B-03: the whole path - "Словарь" deletes (soft), the next sync finds the note by GlossaId and tags it.
        var db = Path.Combine(_temp.New(), "library.db");
        using var store = new LibraryStore(db);
        var keep = store.Record(new SavedWord { Language = "en", Word = "keep", Translation = "оставить" }, newLookup: true);
        var gone = store.Record(new SavedWord { Language = "en", Word = "gone", Translation = "ушедший" }, newLookup: true);
        var fake = new FakeAnki(existingGlossaId: keep);
        fake.Existing[gone] = 77;

        store.Delete(gone);
        var result = await new AnkiConnectSync(new HttpClient(fake))
            .SyncAsync(Path.GetDirectoryName(db)!, store.List(), store.ListDeleted(), new AnkiExportOptions(), null, CancellationToken.None);

        Assert.Equal(new AnkiSyncResult(Added: 0, Updated: 1, Tagged: 1), result);
        var tag = Assert.Single(fake.Requests, r => r["action"]!.GetValue<string>() == "addTags");
        Assert.Equal(77, tag["params"]!["notes"]![0]!.GetValue<long>());
        Assert.Equal(AnkiNoteType.RemovedTag, tag["params"]!["tags"]!.GetValue<string>());
        Assert.DoesNotContain("deleteNotes", fake.Actions);
    }

    [Fact]
    public async Task A_deleted_word_met_again_comes_back_with_its_note()
    {
        var db = Path.Combine(_temp.New(), "library.db");
        using var store = new LibraryStore(db);
        var id = store.Record(new SavedWord { Language = "en", Word = "gone", Translation = "ушедший", Context = "It's gone." }, newLookup: true);
        store.SetPinned(id, true);
        store.Delete(id);

        var again = store.Record(new SavedWord { Language = "en", Word = "gone", Translation = "пропавший", Context = "Gone again." }, newLookup: true);

        Assert.Equal(id, again);
        var word = Assert.Single(store.List());
        Assert.Equal(2, word.Lookups);
        Assert.True(word.Pinned);
        Assert.Equal("ушедший", word.Translation); // a later lookup only fills blanks
        Assert.Empty(store.ListDeleted());

        var fake = new FakeAnki(existingGlossaId: id);
        await new AnkiConnectSync(new HttpClient(fake))
            .SyncAsync(Path.GetDirectoryName(db)!, store.List(), store.ListDeleted(), new AnkiExportOptions(), null, CancellationToken.None);
        var untag = Assert.Single(fake.Requests, r => r["action"]!.GetValue<string>() == "removeTags");
        Assert.Equal(42, untag["params"]!["notes"]![0]!.GetValue<long>());
        Assert.Equal(AnkiNoteType.RemovedTag, untag["params"]!["tags"]!.GetValue<string>());
        Assert.DoesNotContain("addNote", fake.Actions);
    }

    /// <summary>AnkiConnect with the Glossa note type absent, or present with <paramref name="modelFields"/>; an action can be unknown to it.</summary>
    private sealed class FakeAnki(string existingGlossaId, string[]? modelFields = null, string? unsupported = null) : HttpMessageHandler
    {
        public List<string> Actions { get; } = [];
        public List<JsonNode> Requests { get; } = [];
        public Dictionary<string, long> Existing { get; } = new() { [existingGlossaId] = 42 };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var action = body["action"]!.GetValue<string>();
            Actions.Add(action);
            Requests.Add(body);
            JsonNode? result = action switch
            {
                "modelNames" => modelFields is null ? new JsonArray("Basic") : new JsonArray("Basic", AnkiNoteType.Name),
                "modelFieldNames" => new JsonArray((modelFields ?? []).Select(f => (JsonNode)f).ToArray()),
                "findNotes" => FindResult(body["params"]!["query"]!.GetValue<string>()),
                "addNote" => 1001,
                _ => null,
            };
            var error = action == unsupported ? (JsonNode)"unsupported action" : null;
            var json = new JsonObject { ["result"] = result, ["error"] = error }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private JsonArray FindResult(string query)
        {
            var arr = new JsonArray();
            foreach (var (id, nid) in Existing) if (query.Contains(id)) arr.Add(nid);
            return arr;
        }
    }
}
