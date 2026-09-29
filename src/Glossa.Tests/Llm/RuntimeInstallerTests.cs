using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Glossa.Core.Config;
using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

public class RuntimeInstallerTests
{
    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    /// <summary>Serves each archive by its file name, as the release page does.</summary>
    private sealed class Release(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(Path.GetFileName(request.RequestUri!.AbsolutePath), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static (RuntimeEntry Entry, RuntimeInstaller Installer) Build(params (string Name, byte[] Body)[] archives)
    {
        var entry = new RuntimeEntry("cuda", "test", "b1",
            archives.Select(a => new RuntimeFile(a.Name, a.Body.Length, Convert.ToHexString(SHA256.HashData(a.Body)).ToLowerInvariant())).ToList());
        var http = new HttpClient(new Release(archives.ToDictionary(a => a.Name, a => a.Body)));
        return (entry, new RuntimeInstaller(new ModelDownloader(http, http)));
    }

    private static string Root() => Path.Combine(Path.GetTempPath(), "glossa-test-rt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_server_in_a_subfolder_and_the_loose_cuda_libraries_end_up_together()
    {
        var root = Root();
        try
        {
            var (entry, installer) = Build(
                ("llama.zip", Zip(("build/bin/llama-server.exe", "server"), ("build/bin/ggml-cuda.dll", "ggml"))),
                ("cudart.zip", Zip(("cudart64_13.dll", "cudart"))));
            var server = await installer.InstallAsync(entry, root, null, CancellationToken.None);
            Assert.Equal(entry.Server(root), server);
            Assert.True(File.Exists(Path.Combine(entry.Folder(root), "ggml-cuda.dll")));
            Assert.True(File.Exists(Path.Combine(entry.Folder(root), "cudart64_13.dll")));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "downloads")));
            Assert.False(Directory.Exists(entry.Folder(root) + ".unpack"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_flat_archive_unpacks_as_is_and_an_installed_build_is_not_fetched_again()
    {
        var root = Root();
        try
        {
            var (entry, installer) = Build(("vulkan.zip", Zip(("llama-server.exe", "server"), ("ggml-vulkan.dll", "vk"))));
            var progress = new List<ModelProgress>();
            await installer.InstallAsync(entry, root, new Sink(progress), CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(entry.Folder(root), "ggml-vulkan.dll")));
            Assert.Contains(progress, p => p.Verifying && p.Done == entry.Size);

            var (_, offline) = Build(); // nothing to serve: an installed build must not be downloaded again
            Assert.Equal(entry.Server(root), await offline.InstallAsync(entry, root, null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task An_archive_without_the_server_leaves_nothing_behind_to_start()
    {
        var root = Root();
        try
        {
            var (entry, installer) = Build(("odd.zip", Zip(("readme.txt", "no server here"))));
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(entry, root, null, CancellationToken.None));
            Assert.False(File.Exists(entry.Server(root)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_catalog_pins_official_release_files_and_picks_cuda_only_where_it_runs()
    {
        foreach (var e in RuntimeCatalog.Items)
            foreach (var f in e.Files)
            {
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.True(f.Size > 10_000_000);
                Assert.StartsWith($"https://github.com/ggml-org/llama.cpp/releases/download/{e.Release}/", e.Url(f));
            }
        Assert.Equal("cuda", RuntimeCatalog.Recommended(616, 12.0).Id);   // RTX 50xx on a current driver
        Assert.Equal("vulkan", RuntimeCatalog.Recommended(566, 12.0).Id); // driver older than CUDA 13 needs
        Assert.Equal("vulkan", RuntimeCatalog.Recommended(580, 6.1).Id);  // GTX 10xx: CUDA 13 dropped Pascal
        Assert.Equal("vulkan", RuntimeCatalog.Recommended(0, 0).Id);      // AMD, Intel: no NVML
    }

    [Fact]
    public void Ones_own_llama_server_wins_over_the_downloaded_build()
    {
        var ai = new LocalAiSettings { LlamaServerPath = @"X:\own\llama-server.exe", Runtime = "vulkan" };
        Assert.Equal(@"X:\own\llama-server.exe", ai.LlamaServerResolved());
        Assert.Equal("vulkan", ai.RuntimeResolved("cuda"));
        Assert.Equal(RuntimeCatalog.For("vulkan")!.Server(DataPaths.Runtime), new LocalAiSettings { Runtime = "vulkan" }.LlamaServerResolved());
    }

    private sealed class Sink(List<ModelProgress> seen) : IProgress<ModelProgress>
    {
        public void Report(ModelProgress value) => seen.Add(value);
    }
}
