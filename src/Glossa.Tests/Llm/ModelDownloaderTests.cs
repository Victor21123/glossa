using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

public class ModelDownloaderTests
{
    private static readonly byte[] Body = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31)).ToArray();

    private static ModelEntry Entry(string? sha = null) =>
        new("light", "test", "user/repo", new string('a', 40), "model.gguf", Body.Length, sha ?? Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant());

    /// <summary>Serves <see cref="Body"/>, honouring a Range header with 206 when asked to; can fail like a dead proxy.</summary>
    private sealed class Server(bool fail = false, bool ranges = true) : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (fail) throw new HttpRequestException("proxy unreachable");
            Ranges.Add(request.Headers.Range);
            if (ranges && request.Headers.Range?.Ranges.First().From is { } from)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Body[(int)from..]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) });
        }
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), "glossa-test-dl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Downloads_checks_and_names_the_file()
    {
        var dir = Folder();
        try
        {
            var path = await new ModelDownloader(new HttpClient(new Server()), new HttpClient(new Server(fail: true)))
                .DownloadAsync(Entry(), dir, null, CancellationToken.None);
            Assert.Equal(Body, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_broken_download_resumes_where_it_stopped()
    {
        var dir = Folder();
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "model.gguf.part"), Body[..1_000_000]);
            var server = new Server();
            var path = await new ModelDownloader(new HttpClient(server), new HttpClient(server)).DownloadAsync(Entry(), dir, null, CancellationToken.None);
            Assert.Equal(1_000_000, server.Ranges.Single()!.Ranges.First().From);
            Assert.Equal(Body, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_starts_the_file_over()
    {
        var dir = Folder();
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "model.gguf.part"), Body[..1_000_000]);
            var server = new Server(ranges: false);
            var path = await new ModelDownloader(new HttpClient(server), new HttpClient(server)).DownloadAsync(Entry(), dir, null, CancellationToken.None);
            Assert.Equal(Body, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_damaged_file_is_refused_and_removed()
    {
        var dir = Folder();
        try
        {
            var downloader = new ModelDownloader(new HttpClient(new Server()), new HttpClient(new Server()));
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Entry(new string('0', 64)), dir, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_proxy_that_cannot_reach_the_site_falls_back_to_a_direct_connection()
    {
        var dir = Folder();
        try
        {
            var direct = new Server();
            var path = await new ModelDownloader(new HttpClient(new Server(fail: true)), new HttpClient(direct))
                .DownloadAsync(Entry(), dir, null, CancellationToken.None);
            Assert.Single(direct.Ranges);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
