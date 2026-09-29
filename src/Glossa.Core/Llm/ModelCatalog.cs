using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Glossa.Core.Llm;

/// <summary>
/// A local model Glossa downloads by a button (Настройки → ИИ и модели), pinned to the exact file and repository
/// revision the 62-case comparison graded, so a re-upload upstream can never swap it for an untested one.
/// </summary>
public sealed record ModelEntry(string Profile, string Title, string Repo, string Revision, string File, long Size, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repo}/resolve/{Revision}/{File}";

    /// <summary>Where it comes from, as the model tile names it.</summary>
    public string Page => $"huggingface.co/{Repo}";
}

public static class ModelCatalog
{
    /// <summary>The models of the local profiles (test of 2026-09-29: 93%, 94% and 75% of the 62 cases).</summary>
    public static IReadOnlyList<ModelEntry> Items { get; } =
    [
        new("gemma26b", "Gemma 4 26B A4B (Huihui abliterated, UD-IQ4_XS)", "groxaxo/Huihui-gemma-4-26B-A4B-it-abliterated-GGUF",
            "5d4351c2dfd4a11f36ede3c3eaa0a4595c1d150e", "gemma-4-26B-A4B-it-UD-IQ4_XS.gguf", 13418748864,
            "1cde6460e82c26afb90f63bfcb2511a654c31f90cb987d2558f4f834cfbf6978"),
        new("gemma12b", "Gemma 4 12B heretic (Q4_K_M)", "culturerevolt/gemma-4-12b-heretic-abliterated-GGUF",
            "ca1e60be3a69f79a699ff85c9c3f97a1614e5617", "gemma-4-12b-heretic-Q4_K_M.gguf", 7381382496,
            "6c4067ea0210d2367b2dbdd460d2dd86032a9b6e8dcbe03b83a3ea0a0a16dbee"),
        new("light", "Gemma 4 E4B uncensored (TrevorJS, Q4_K_M)", "TrevorJS/gemma-4-E4B-it-uncensored-GGUF",
            "771f130d4c49735ace331f68a80f7ae31387e51c", "gemma-4-E4B-it-uncensored-Q4_K_M.gguf", 5335285280,
            "b2a89ec2df7f13440c723fb3fda2c531696cbe8e20ed2f1122f97a43aaafcb50"),
    ];

    public static ModelEntry? For(string profile) => Items.FirstOrDefault(e => e.Profile == profile);
}

/// <summary>Where a model download stands: bytes received, then the check of the whole file.</summary>
public readonly record struct ModelProgress(long Done, long Total, bool Verifying);

/// <summary>
/// Downloads a catalog model: through the system proxy first and directly if that fails (as the dictionary catalog
/// does); a broken download resumes from its «.part» file; the file takes its real name only once its SHA-256 matches.
/// </summary>
public sealed class ModelDownloader(HttpClient proxied, HttpClient direct)
{
    public Task<string> DownloadAsync(ModelEntry entry, string folder, IProgress<ModelProgress>? progress, CancellationToken ct) =>
        DownloadAsync(entry.Url, entry.File, entry.Size, entry.Sha256, folder, progress, ct);

    /// <summary>Any pinned file (a model, a llama.cpp archive) into <paramref name="folder"/> under <paramref name="file"/>.</summary>
    public async Task<string> DownloadAsync(string url, string file, long size, string sha256, string folder,
        IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        if (File.Exists(path) && new FileInfo(path).Length == size) return path;
        var part = path + ".part";
        try
        {
            await FetchAsync(proxied, url, size, part, progress, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await FetchAsync(direct, url, size, part, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(new ModelProgress(size, size, Verifying: true));
        if (await HashAsync(part, ct).ConfigureAwait(false) != sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("Файл пришёл повреждённым: контрольная сумма не совпала. Попробуй скачать ещё раз.");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    private static async Task FetchAsync(HttpClient client, string url, long size, string part, IProgress<ModelProgress>? progress,
        CancellationToken ct)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have >= size)
        {
            if (have == size) return; // complete, only the check is left
            File.Delete(part);
            have = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Glossa", "0.4"));
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // A server that ignores the range sends the whole file again: start over rather than append it.
        var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        var lastReport = Environment.TickCount64;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            have += read;
            if (Environment.TickCount64 - lastReport >= 250)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new ModelProgress(have, size, Verifying: false));
            }
        }
        progress?.Report(new ModelProgress(have, size, Verifying: false));
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
