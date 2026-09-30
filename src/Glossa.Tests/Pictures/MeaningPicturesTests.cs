using System.Net;
using System.Text;
using Glossa.Core.Pictures;
using SkiaSharp;

namespace Glossa.Tests.Pictures;

public sealed class MeaningPicturesTests : IDisposable
{
    private readonly TestFolders _folders = new();
    public void Dispose() => _folders.Dispose();

    private const string WikiSearch = """
        {"batchcomplete":true,"query":{"pages":[
          {"pageid":2,"ns":0,"title":"Megabat","index":2,"pageimage":"Pteropus_vampyrus.jpg"},
          {"pageid":1,"ns":0,"title":"Bat","index":1,"pageimage":"Big-eared-townsend-fledermaus.jpg"},
          {"pageid":3,"ns":0,"title":"Bat (disambiguation)","index":3}]}}
        """;

    private const string WikiInfo = """
        {"query":{"pages":[
          {"ns":6,"title":"File:Pteropus vampyrus.jpg","missing":true,"known":true,"imagerepository":"shared","imageinfo":[{
            "thumburl":"https://upload.wikimedia.org/thumb/megabat-500.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Pteropus_vampyrus.jpg",
            "extmetadata":{"Artist":{"value":"<a href=\"//commons.wikimedia.org/wiki/User:Ann\" title=\"User:Ann\">Ann &amp; Bo</a>"},
                           "LicenseShortName":{"value":"CC BY-SA 3.0"}}}]},
          {"ns":6,"title":"File:Big-eared-townsend-fledermaus.jpg","missing":true,"known":true,"imagerepository":"shared","imageinfo":[{
            "thumburl":"https://upload.wikimedia.org/thumb/bat-500.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Big-eared-townsend-fledermaus.jpg",
            "extmetadata":{"Artist":{"value":"U.S. Fish and Wildlife Service"},"LicenseShortName":{"value":"Public domain"}}}]}]}}
        """;

    private const string CommonsSearch = """
        {"query":{"pages":[
          {"ns":6,"title":"File:Bat c.png","index":3,"imageinfo":[{"thumburl":"https://upload.wikimedia.org/thumb/c-500.png","descriptionurl":"https://c"}]},
          {"ns":6,"title":"File:Bat a.jpg","index":1,"imageinfo":[{"thumburl":"https://upload.wikimedia.org/thumb/bat-500.jpg","descriptionurl":"https://a"}]},
          {"ns":6,"title":"File:Bat b.jpg","index":2,"imageinfo":[{"thumburl":"https://upload.wikimedia.org/thumb/b-500.jpg","descriptionurl":"https://b",
            "extmetadata":{"Artist":{"value":"Cy"},"LicenseShortName":{"value":"CC BY 4.0"}}}]}]}}
        """;

    private const string OpenverseSearch = """
        {"result_count":4,"results":[
          {"id":"1","source":"wikimedia","thumbnail":"https://api.openverse.org/v1/images/1/thumb/","license":"by","license_version":"2.0"},
          {"id":"2","source":"flickr","thumbnail":"https://api.openverse.org/v1/images/2/thumb/","foreign_landing_url":"https://flickr.com/2",
           "creator":"Dee","license":"by-sa","license_version":"2.0"},
          {"id":"3","source":"flickr","thumbnail":"https://api.openverse.org/v1/images/3/thumb/","creator":"Eve","license":"cc0","license_version":"1.0"},
          {"id":"4","source":"flickr","thumbnail":"https://api.openverse.org/v1/images/4/thumb/","license":"pdm"}]}
        """;

    /// <summary>Answers like the three sites; can fail like a blocked route, or answer every request with one status.</summary>
    private sealed class Web(bool fail = false, HttpStatusCode? status = null) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            if (fail) throw new HttpRequestException("unreachable");
            if (status is { } code) return Task.FromResult(new HttpResponseMessage(code));
            Assert.StartsWith("Glossa", request.Headers.UserAgent.ToString());
            var body = request.RequestUri.Host switch
            {
                "en.wikipedia.org" when url.Contains("generator=search") => WikiSearch,
                "en.wikipedia.org" => WikiInfo,
                "commons.wikimedia.org" => CommonsSearch,
                "api.openverse.org" when url.Contains("/thumb/") => "picture",
                "api.openverse.org" => OpenverseSearch,
                _ => "picture",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });
        }
    }

    [Fact]
    public async Task Wikipedia_comes_first_then_commons_then_openverse_until_six_and_spares()
    {
        var web = new Web();
        var search = await new MeaningPictures(new HttpClient(web), new HttpClient(new Web(fail: true))).SearchAsync(" bat ", CancellationToken.None);

        Assert.False(search.Offline);
        // Wikipedia in its search's order, Commons in its own without the picture Wikipedia gave, Openverse without Wikimedia;
        // all the sites have here is 7, fewer than six and three spares.
        Assert.Equal(new[]
        {
            "https://upload.wikimedia.org/thumb/bat-500.jpg", "https://upload.wikimedia.org/thumb/megabat-500.jpg",
            "https://upload.wikimedia.org/thumb/b-500.jpg", "https://upload.wikimedia.org/thumb/c-500.png",
            "https://api.openverse.org/v1/images/2/thumb/", "https://api.openverse.org/v1/images/3/thumb/",
            "https://api.openverse.org/v1/images/4/thumb/",
        }, search.Found.Select(f => f.ThumbUrl));
        Assert.Equal(new[] { "Википедия", "Википедия", "Wikimedia Commons", "Wikimedia Commons", "Openverse", "Openverse", "Openverse" },
            search.Found.Select(f => f.Source));

        var megabat = search.Found[1];
        Assert.Equal(("Ann & Bo", "CC BY-SA 3.0"), (megabat.Author, megabat.License));
        Assert.Equal("Википедия, Ann & Bo, CC BY-SA 3.0", megabat.Keep("images/x.jpg").Credit);
        Assert.Equal(("Dee", "CC BY-SA 2.0", "https://flickr.com/2"), (search.Found[4].Author, search.Found[4].License, search.Found[4].Page));
        Assert.Equal("CC0", search.Found[5].License);
        Assert.Contains(web.Asked, u => u.Contains("mature=true")); // no filter: the user's decision
    }

    [Fact]
    public async Task A_blocked_direct_route_falls_back_to_the_proxy_and_the_proxy_then_goes_first()
    {
        var direct = new Web(fail: true);
        var proxied = new Web();
        var pictures = new MeaningPictures(new HttpClient(direct), new HttpClient(proxied));

        var search = await pictures.SearchAsync("bat", CancellationToken.None);
        Assert.Equal(7, search.Found.Count);
        Assert.NotNull(await pictures.FetchAsync(search.Found[0], CancellationToken.None));
        Assert.Single(direct.Asked);
    }

    [Fact]
    public async Task A_site_answering_with_an_error_is_not_asked_again_through_the_proxy()
    {
        var direct = new Web(status: HttpStatusCode.NotFound);
        var proxied = new Web();
        var pictures = new MeaningPictures(new HttpClient(direct), new HttpClient(proxied));

        Assert.Null(await pictures.FetchAsync(new PictureCandidate("Openverse", "https://x/1.jpg", null, null, null), CancellationToken.None));
        Assert.Null(await pictures.FetchAsync(new PictureCandidate("Openverse", "not an address", null, null, null), CancellationToken.None));
        Assert.Empty(proxied.Asked);
        Assert.Single(direct.Asked);
    }

    [Fact]
    public async Task With_no_way_out_the_search_says_it_is_offline()
    {
        var pictures = new MeaningPictures(new HttpClient(new Web(fail: true)), new HttpClient(new Web(fail: true)));
        var search = await pictures.SearchAsync("bat", CancellationToken.None);
        Assert.True(search.Offline);
        Assert.Empty(search.Found);
        Assert.Null(await pictures.FetchAsync(new PictureCandidate("Openverse", "https://x/1.jpg", null, null, null), CancellationToken.None));
    }

    [Fact]
    public void One_file_found_by_wikipedia_and_commons_is_one_picture()
    {
        const string thumb = "https://thumb.wikimedia.org/wikipedia/commons/thumb/6/67/Fox.jpg/500px-Fox.jpg";
        var wiki = new PictureCandidate("Википедия", thumb + "?utm_source=en.wikipedia.org", "https://commons.wikimedia.org/wiki/File:Fox.jpg", null, null);
        Assert.True(MeaningPictures.SamePicture(wiki, wiki with { Source = "Wikimedia Commons", ThumbUrl = thumb + "?utm_source=commons.wikimedia.org" }));
        Assert.True(MeaningPictures.SamePicture(wiki, wiki with { Page = null, ThumbUrl = thumb }));
        Assert.False(MeaningPictures.SamePicture(wiki, new PictureCandidate("Openverse", "https://api.openverse.org/v1/images/2/thumb/", null, null, null)));
    }

    [Theory]
    [InlineData("fruit bat", "en")]
    [InlineData("летучая мышь", "ru")]
    [InlineData("こうもり", "ja")]
    [InlineData("蝙蝠", "zh")]
    public void The_query_goes_to_the_wikipedia_of_its_script(string query, string wiki) => Assert.Equal(wiki, MeaningPictures.WikiLanguage(query));

    [Fact]
    public void Licenses_and_authors_read_as_people_write_them()
    {
        Assert.Equal("CC BY-NC-SA 4.0", MeaningPictures.LicenseName("by-nc-sa", "4.0"));
        Assert.Equal("общественное достояние", MeaningPictures.LicenseName("pdm", null));
        Assert.Null(MeaningPictures.LicenseName(null, "4.0"));
        Assert.Null(MeaningPictures.PlainText("<span> </span>"));
        Assert.Equal(80, MeaningPictures.PlainText(new string('a', 200))!.Length);
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Theory]
    [InlineData(2560, 1440, 480, 270)]
    [InlineData(300, 900, 160, 480)]
    [InlineData(200, 100, 200, 100)]
    public void A_picture_is_stored_as_a_jpeg_no_larger_than_480(int width, int height, int storedWidth, int storedHeight)
    {
        using var stored = SKBitmap.Decode(PictureFiles.Shrink(Png(width, height)));
        Assert.Equal((storedWidth, storedHeight), (stored.Width, stored.Height));
        // Transparency turns white rather than black.
        Assert.Equal(SKColors.White, stored.GetPixel(stored.Width / 2, stored.Height / 2));
    }

    [Fact]
    public void A_phone_photo_is_turned_upright_as_its_exif_says()
    {
        // 200 x 100 as the sensor saw it, marked "turn 90 degrees clockwise" (EXIF orientation 6): upright it is 100 x 200.
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.White);
        for (var x = 0; x < 20; x++)
            for (var y = 0; y < 100; y++) bitmap.SetPixel(x, y, SKColors.Black); // a black band on the sensor's left edge
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = data.ToArray();
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,                          // TIFF, little-endian, IFD at 8
            0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, // one entry: Orientation = 6
            0x00, 0x00, 0x00, 0x00,
        ];
        var marked = jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray();

        using var stored = SKBitmap.Decode(PictureFiles.Shrink(marked));
        Assert.Equal((100, 200), (stored.Width, stored.Height));
        // Turned clockwise, the sensor's left edge is now the top.
        Assert.True(stored.GetPixel(50, 5).Red < 60);
        Assert.True(stored.GetPixel(50, 195).Red > 200);
    }

    [Fact]
    public void Saved_pictures_live_in_the_images_folder_and_go_when_deleted()
    {
        var root = _folders.New();
        Assert.Null(PictureFiles.Save(root, "w1", Encoding.UTF8.GetBytes("not a picture")));

        var first = PictureFiles.Save(root, "w1", Png(64, 64))!;
        Assert.StartsWith("images/w1-", first);
        Assert.EndsWith(".jpg", first);
        Assert.True(File.Exists(Path.Combine(root, first)));

        PictureFiles.Delete(root, first);
        Assert.False(File.Exists(Path.Combine(root, first)));
        PictureFiles.Delete(root, first); // already gone: nothing happens
    }
}
