using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Glossa.Core.Pictures;

/// <summary>
/// The picture of a word's meaning, as stored with the word: the file (JPEG, relative to the data folder) and where it
/// came from, to credit its author under it.
/// </summary>
public sealed record MeaningPicture(string File, string Source, string? Author = null, string? License = null, string? Page = null)
{
    /// <summary>What the user added from the clipboard or a file.</summary>
    public const string OwnSource = "своя картинка";

    /// <summary>"Wikimedia Commons, Jane Doe, CC BY-SA 4.0" under the picture.</summary>
    public string Credit => string.Join(", ", new[] { Source, Author, License }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A picture a search found, before the user picks one.</summary>
public sealed record PictureCandidate(string Source, string ThumbUrl, string? Page, string? Author, string? License)
{
    public MeaningPicture Keep(string file) => new(file, Source, Author, License, Page);
}

/// <summary>What a search found; <see cref="Offline"/> when not one source answered.</summary>
public sealed record PictureSearch(IReadOnlyList<PictureCandidate> Found, bool Offline);

/// <summary>
/// Pictures of a word's meaning (decided 2026-09-28, sources chosen to be reachable from Russia): the picture of the
/// Wikipedia article first (it shows the thing itself), then Wikimedia Commons, then Openverse, until there are
/// <see cref="Count"/> and <see cref="Spare"/>; no filter for sensitive pictures ("ПО для личного использования").
/// Each request goes out directly first and through the system proxy when there is no answer; once one way has failed
/// and the other worked, the other goes first for the rest of the run. Live 2026-09-30: 6 found in 1.6-2.2 s directly.
/// </summary>
public sealed class MeaningPictures(HttpClient direct, HttpClient proxied)
{
    /// <summary>Pictures shown to choose from.</summary>
    public const int Count = 6;

    /// <summary>
    /// Found beyond <see cref="Count"/>, to take the place of one that will not load: Openverse still lists Flickr
    /// photos long gone (2 of 4 for "fruit bat" answered 403, live 2026-09-30).
    /// </summary>
    public const int Spare = 3;

    /// <summary>A width Wikimedia keeps ready-made thumbnails for: other widths may be refused to programs.</summary>
    private const int ThumbWidth = 500;

    private const string UserAgent = "Glossa (https://github.com/Victor21123/glossa)";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private volatile bool _proxyFirst;

    public async Task<PictureSearch> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return new PictureSearch([], Offline: false);
        Func<string, int, CancellationToken, Task<IReadOnlyList<PictureCandidate>>>[] sources = [WikipediaAsync, CommonsAsync, OpenverseAsync];
        var found = new List<PictureCandidate>();
        const int wanted = Count + Spare;
        int tried = 0, failed = 0;
        foreach (var source in sources)
        {
            if (found.Count >= wanted) break;
            tried++;
            try
            {
                foreach (var c in await source(query, wanted - found.Count, ct).ConfigureAwait(false))
                    if (found.Count < wanted && !found.Any(f => SamePicture(f, c))) found.Add(c);
            }
            catch (Exception e) when (IsNetwork(e, ct) || e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                failed++;
            }
        }
        return new PictureSearch(found, Offline: failed == tried && found.Count == 0);
    }

    /// <summary>
    /// A Wikipedia article's picture usually lives on Commons, and Commons may find it again: one file page, and one
    /// thumbnail but for the "?utm_source=" each site adds (seen live 2026-09-30).
    /// </summary>
    internal static bool SamePicture(PictureCandidate a, PictureCandidate b) =>
        a.Page is not null && a.Page == b.Page || Bare(a.ThumbUrl) == Bare(b.ThumbUrl);

    private static string Bare(string url) => url.IndexOf('?') is var q and >= 0 ? url[..q] : url;

    /// <summary>The candidate's picture, or null when it cannot be had (its address comes from the site's answer).</summary>
    public async Task<byte[]?> FetchAsync(PictureCandidate candidate, CancellationToken ct)
    {
        try
        {
            return await GetBytesAsync(candidate.ThumbUrl, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsNetwork(e, ct) || e is UriFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<PictureCandidate>> WikipediaAsync(string query, int want, CancellationToken ct)
    {
        var api = $"https://{WikiLanguage(query)}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2";
        var files = LeadImages(await GetStringAsync(
            $"{api}&generator=search&gsrnamespace=0&gsrlimit=3&gsrsearch={Uri.EscapeDataString(query)}&prop=pageimages&piprop=name", ct).ConfigureAwait(false));
        if (files.Count == 0) return [];
        var titles = string.Join('|', files.Select(f => "File:" + f));
        var info = ImageInfo(await GetStringAsync(
            $"{api}&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth={ThumbWidth}&iiextmetadatafilter=Artist|LicenseShortName"
            + $"&titles={Uri.EscapeDataString(titles)}", ct).ConfigureAwait(false), "Википедия");
        // The info comes back in its own order: the articles' order is the search's.
        return files.Select(f => info.FirstOrDefault(i => i.Name == SameName(f)).Picture).OfType<PictureCandidate>().Take(want).ToList();
    }

    private async Task<IReadOnlyList<PictureCandidate>> CommonsAsync(string query, int want, CancellationToken ct)
    {
        var json = await GetStringAsync(
            "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6"
            + $"&gsrlimit={want + 2}&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}"
            + $"&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth={ThumbWidth}&iiextmetadatafilter=Artist|LicenseShortName", ct).ConfigureAwait(false);
        return ImageInfo(json, "Wikimedia Commons").OrderBy(i => i.Index).Select(i => i.Picture).Take(want).ToList();
    }

    private async Task<IReadOnlyList<PictureCandidate>> OpenverseAsync(string query, int want, CancellationToken ct)
    {
        var json = await GetStringAsync(
            $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&page_size={Math.Min(20, want + 4)}&mature=true", ct).ConfigureAwait(false);
        return Openverse(json).Take(want).ToList();
    }

    /// <summary>The pictures of the articles a Wikipedia search found, in the search's order (file names).</summary>
    internal static IReadOnlyList<string> LeadImages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) return [];
        return pages.EnumerateArray()
            .Where(p => Str(p, "pageimage") is not null)
            .OrderBy(p => p.TryGetProperty("index", out var i) ? i.GetInt32() : int.MaxValue)
            .Select(p => Str(p, "pageimage")!)
            .Distinct()
            .ToList();
    }

    /// <summary>Files with their thumbnails and credits from a MediaWiki imageinfo answer.</summary>
    internal static IReadOnlyList<(string Name, int Index, PictureCandidate Picture)> ImageInfo(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) return [];
        var list = new List<(string, int, PictureCandidate)>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            if (Str(info, "thumburl") is not { } thumb) continue;
            string? Meta(string key) =>
                info.TryGetProperty("extmetadata", out var meta) && meta.TryGetProperty(key, out var field) ? Str(field, "value") : null;
            // Every title has its namespace ("File:", "Файл:" on the Russian Wikipedia) before the first colon.
            var title = Str(page, "title") ?? "";
            var index = page.TryGetProperty("index", out var i) ? i.GetInt32() : int.MaxValue;
            list.Add((SameName(title[(title.IndexOf(':') + 1)..]), index,
                new PictureCandidate(source, thumb, Str(info, "descriptionurl"), PlainText(Meta("Artist")), PlainText(Meta("LicenseShortName")))));
        }
        return list;
    }

    /// <summary>Openverse results, without those from Wikimedia (Commons was asked already).</summary>
    internal static IReadOnlyList<PictureCandidate> Openverse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray()
            .Where(r => Str(r, "source") != "wikimedia" && Str(r, "thumbnail") is not null)
            .Select(r => new PictureCandidate("Openverse", Str(r, "thumbnail")!, Str(r, "foreign_landing_url"), PlainText(Str(r, "creator")),
                LicenseName(Str(r, "license"), Str(r, "license_version"))))
            .ToList();
    }

    /// <summary>Openverse's "by-sa", "4.0" as a person writes it: "CC BY-SA 4.0".</summary>
    internal static string? LicenseName(string? license, string? version) => license?.ToLowerInvariant() switch
    {
        null or "" => null,
        "cc0" => "CC0",
        "pdm" => "общественное достояние",
        var l => $"CC {l.ToUpperInvariant()} {version}".TrimEnd(),
    };

    /// <summary>The Wikipedia of the query's script: an English query (the AI card gives one) goes to the English one.</summary>
    internal static string WikiLanguage(string query) =>
        query.Any(c => c is >= 'Ѐ' and <= 'ӿ') ? "ru"
        : query.Any(c => c is >= '぀' and <= 'ヿ') ? "ja"
        : query.Any(c => c is >= '一' and <= '鿿') ? "zh"
        : "en";

    /// <summary>"Red_Apple.jpg" and "Red Apple.jpg" name one file.</summary>
    private static string SameName(string name) => name.Replace('_', ' ').Trim();

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Commons writes the author as HTML (a link to their page); the card shows the name only.</summary>
    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
        if (text.Length == 0) return null;
        return text.Length <= 80 ? text : text[..77].TrimEnd() + "...";
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await GetBytesAsync(url, ct).ConfigureAwait(false));

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        var proxyFirst = _proxyFirst;
        var (first, second) = proxyFirst ? (proxied, direct) : (direct, proxied);
        try
        {
            return await SendAsync(first, url, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouteDown(e, ct))
        {
            var bytes = await SendAsync(second, url, ct).ConfigureAwait(false);
            _proxyFirst = !proxyFirst;
            return bytes;
        }
    }

    /// <summary>
    /// No answer at all (refused, reset, timed out: how a blocked HTTPS site looks). A status such as 404 or 429 is the
    /// site's own answer: the other way would get the same, and the way that got it works.
    /// </summary>
    private static bool IsRouteDown(Exception e, CancellationToken ct) =>
        e is HttpRequestException { StatusCode: null } || e is OperationCanceledException && !ct.IsCancellationRequested;

    private static async Task<byte[]> SendAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Wikimedia refuses programs that do not say who they are.
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>A failed or timed-out request, not the caller cancelling.</summary>
    private static bool IsNetwork(Exception e, CancellationToken ct) =>
        e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested;
}

/// <summary>Meaning pictures on disk: small JPEGs in the data folder's images.</summary>
public static class PictureFiles
{
    public const int MaxSide = 480;
    public const string Folder = "images";

    /// <summary>Beyond this a picture is decoded smaller (JPEG can) or refused: 40 MP is 160 MB of pixels.</summary>
    private const long MaxPixels = 40_000_000;

    /// <summary>
    /// The picture as stored: JPEG with its longer side at most <see cref="MaxSide"/>, turned upright as the camera
    /// marked it (EXIF), transparency on white. Null when the bytes are not a picture or one too large to open.
    /// </summary>
    public static byte[]? Shrink(byte[] image, int quality = 85)
    {
        using var bytes = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(bytes); // null for what is not a picture (Decode would throw)
        if (codec is null) return null;
        var size = codec.Info.Size;
        if ((long)size.Width * size.Height > MaxPixels)
        {
            size = codec.GetScaledDimensions((float)Math.Sqrt(MaxPixels / ((double)size.Width * size.Height)));
            if ((long)size.Width * size.Height > MaxPixels) return null;
        }
        using var decoded = SKBitmap.Decode(codec, codec.Info.WithSize(size.Width, size.Height));
        if (decoded is null || decoded.Width == 0 || decoded.Height == 0) return null;
        using var upright = Upright(decoded, codec.EncodedOrigin);
        var src = upright ?? decoded;
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(src.Width, src.Height));
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(src.Width * scale)), Math.Max(1, (int)Math.Round(src.Height * scale)));
        var current = SKImage.FromBitmap(src);
        try
        {
            // Halving first: one big step down skips most pixels and a screenshot comes out jagged.
            while (current.Width / 2 >= info.Width && current.Height / 2 >= info.Height)
            {
                var half = Draw(current, new SKImageInfo(current.Width / 2, current.Height / 2), new SKSamplingOptions(SKFilterMode.Linear), white: false);
                current.Dispose();
                current = half;
            }
            using var final = Draw(current, info, new SKSamplingOptions(SKCubicResampler.Mitchell), white: true);
            using var data = final.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>
    /// A phone photo is stored as the sensor saw it and marked with how to turn it; decoding does not turn it. Null when
    /// it is upright already. The matrices are Skia's own (SkEncodedOriginToMatrix).
    /// </summary>
    private static SKBitmap? Upright(SKBitmap src, SKEncodedOrigin origin)
    {
        float w = src.Width, h = src.Height;
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is not { } m) return null;
        var turned = origin >= SKEncodedOrigin.LeftTop; // the four with width and height swapped
        var dst = new SKBitmap(turned ? src.Height : src.Width, turned ? src.Width : src.Height);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    private static SKImage Draw(SKImage image, SKImageInfo info, SKSamplingOptions sampling, bool white)
    {
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(white ? SKColors.White : SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), sampling);
        return surface.Snapshot();
    }

    /// <summary>
    /// Writes a word's picture and returns its path relative to the data folder, or null when the bytes are not a
    /// picture. Each picture gets a new name, so a view still holding the old file never shows it for the new one.
    /// </summary>
    public static string? Save(string dataRoot, string wordId, byte[] image)
    {
        if (Shrink(image) is not { } jpeg) return null;
        Directory.CreateDirectory(Path.Combine(dataRoot, Folder));
        var relative = $"{Folder}/{wordId}-{DateTime.UtcNow.Ticks:x}.jpg";
        File.WriteAllBytes(Path.Combine(dataRoot, relative), jpeg);
        return relative;
    }

    /// <summary>Removes a picture the word no longer uses; one that is locked or gone is left alone.</summary>
    public static void Delete(string dataRoot, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return;
        try
        {
            File.Delete(Path.Combine(dataRoot, relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
