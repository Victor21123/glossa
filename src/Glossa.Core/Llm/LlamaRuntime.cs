using System.IO.Compression;

namespace Glossa.Core.Llm;

/// <summary>One archive of an official llama.cpp release, pinned by size and SHA-256.</summary>
public sealed record RuntimeFile(string Name, long Size, string Sha256);

/// <summary>
/// A llama.cpp build Glossa downloads by a button (Настройки → ИИ и модели → Движок): the official Windows release of
/// github.com/ggml-org/llama.cpp, pinned to one release so every PC runs the build the tests ran.
/// </summary>
public sealed record RuntimeEntry(string Id, string Title, string Release, IReadOnlyList<RuntimeFile> Files)
{
    public long Size => Files.Sum(f => f.Size);

    public string Url(RuntimeFile file) => $"https://github.com/ggml-org/llama.cpp/releases/download/{Release}/{file.Name}";

    /// <summary>Where it is unpacked under the runtime folder, one folder per release and kind: «b11243-cuda».</summary>
    public string Folder(string root) => Path.Combine(root, $"{Release}-{Id}");

    public string Server(string root) => Path.Combine(Folder(root), "llama-server.exe");
}

public static class RuntimeCatalog
{
    /// <summary>
    /// CUDA for NVIDIA cards (the build and its CUDA libraries), Vulkan for any other card. Release b11243 of 2026-09-29.
    /// </summary>
    public static IReadOnlyList<RuntimeEntry> Items { get; } =
    [
        new("cuda", "NVIDIA (CUDA 13.4)", "b11243",
        [
            new("llama-b11243-bin-win-cuda-13.4-x64.zip", 153542094, "f57efcdfe60a11fd64e481ab2e6a056f51662eff125bcfeb3caa2b1e6434f06e"),
            new("cudart-llama-bin-win-cuda-13.4-x64.zip", 423535356, "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"),
        ]),
        new("vulkan", "Любая видеокарта (Vulkan)", "b11243",
        [
            new("llama-b11243-bin-win-vulkan-x64.zip", 33065310, "147f88e011cb04cbaea45917b6f5f639b76c53d3b15e3e077d1458963af61b25"),
        ]),
    ];

    public static RuntimeEntry? For(string id) => Items.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// CUDA 13 needs an NVIDIA driver of branch 580 or later and a Turing or newer card (compute capability 7.5+);
    /// anything else — an older NVIDIA card, AMD, Intel — gets Vulkan.
    /// </summary>
    public static RuntimeEntry Recommended(int nvidiaDriver, double computeCapability) =>
        For(nvidiaDriver >= 580 && computeCapability >= 7.5 ? "cuda" : "vulkan")!;
}

/// <summary>
/// Downloads a runtime's archives (each checked like a model), unpacks them into a fresh folder and only then moves it
/// into place, so a half-unpacked build is never started; the archives are deleted afterwards.
/// </summary>
public sealed class RuntimeInstaller(ModelDownloader downloader)
{
    public async Task<string> InstallAsync(RuntimeEntry entry, string root, IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        var server = entry.Server(root);
        if (File.Exists(server)) return server;

        var archives = Path.Combine(root, "downloads");
        var zips = new List<string>();
        long before = 0;
        foreach (var file in entry.Files)
        {
            var offset = before;
            var part = progress is null ? null : new Relay(p => progress.Report(new ModelProgress(offset + p.Done, entry.Size, p.Verifying)));
            zips.Add(await downloader.DownloadAsync(entry.Url(file), file.Name, file.Size, file.Sha256, archives, part, ct).ConfigureAwait(false));
            before += file.Size;
        }

        progress?.Report(new ModelProgress(entry.Size, entry.Size, Verifying: true));
        var folder = entry.Folder(root);
        var unpack = folder + ".unpack";
        if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
        foreach (var zip in zips)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpack, overwriteFiles: true), ct).ConfigureAwait(false);
        }

        // The server may sit in a subfolder of its archive while the CUDA libraries lie at the top of theirs:
        // the folder with llama-server.exe becomes the build, and loose top-level files join it.
        var exe = Directory.GetFiles(unpack, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("В сборке llama.cpp нет llama-server.exe.");
        var bin = Path.GetDirectoryName(exe)!;
        if (!string.Equals(bin, unpack, StringComparison.OrdinalIgnoreCase))
            foreach (var loose in Directory.GetFiles(unpack))
                File.Move(loose, Path.Combine(bin, Path.GetFileName(loose)), overwrite: true);

        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(bin, folder);
        if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
        foreach (var zip in zips) File.Delete(zip);
        return server;
    }

    /// <summary>Reports straight away on the caller's thread (a <see cref="Progress{T}"/> would post and reorder).</summary>
    private sealed class Relay(Action<ModelProgress> report) : IProgress<ModelProgress>
    {
        public void Report(ModelProgress value) => report(value);
    }
}
