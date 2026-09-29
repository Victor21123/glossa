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

        var en = new SavedWord
        {
            Language = "en", Word = "business", DictionaryForm = "business", Reading = "/ˈbɪznɪs/", Level = "B1",
            PartOfSpeech = "noun", Translation = "дело", Definition = "the purpose of your visit",
            Context = "State your business, stranger.", ContextOffset = 11,
            ContextTranslation = "Скажите, в чём дело, незнакомец.", Synonyms = ["purpose", "affair"],
            WindowTitle = "Test Game", AppExe = "game.exe", ShotFile = Path.Combine("shots", "scene.jpg"),
            WordBox = new PixelRect(160, 150, 280, 190),
        };
        var ja = new SavedWord
        {
            Language = "ja", Word = "薄暗い", DictionaryForm = "薄暗い", Reading = "うすぐらい", Level = "JLPT N2",
            Translation = "сумрачный", Context = "薄暗い所で泣いていた。", ContextOffset = 0,
            Components = [new CardComponent("薄", null, "тонкий"), new CardComponent("暗", null, "тёмный")],
        };
        return (root, [en, ja]);
    }

    [Fact]
    public void Apkg_contains_collection_notes_cards_and_media()
    {
        var (root, words) = Sample();
        var apkg = Path.Combine(root, "out.apkg");

        ApkgWriter.Write(apkg, root, words, new AnkiExportOptions(ReverseCards: true));

        using var zip = ZipFile.OpenRead(apkg);
        Assert.NotNull(zip.GetEntry("collection.anki2"));
        var media = JsonNode.Parse(new StreamReader(zip.GetEntry("media")!.Open()).ReadToEnd())!.AsObject();
        Assert.Single(media); // only the English word has a screenshot
        Assert.Equal($"glossa_{words[0].Id}.jpg", media["0"]!.GetValue<string>());

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

    private sealed class FakeAnki(string existingGlossaId) : HttpMessageHandler
    {
        public List<string> Actions { get; } = [];
        public Dictionary<string, long> Existing { get; } = new() { [existingGlossaId] = 42 };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var action = body["action"]!.GetValue<string>();
            Actions.Add(action);
            JsonNode? result = action switch
            {
                "modelNames" => new JsonArray("Basic"),
                "findNotes" => FindResult(body["params"]!["query"]!.GetValue<string>()),
                "addNote" => 1001,
                _ => null,
            };
            var json = new JsonObject { ["result"] = result, ["error"] = null }.ToJsonString();
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
