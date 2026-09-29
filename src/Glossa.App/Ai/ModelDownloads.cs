using Glossa.Core.Llm;
using Glossa.Core.Logging;

namespace Glossa.App.Ai;

/// <summary>
/// Downloads of Настройки → ИИ и модели — the models and the llama.cpp build — owned by the app rather than the settings
/// page: closing the window does not stop a 13 GB download, and reopening it shows where it is. Keyed by the model's
/// profile, or <see cref="Runtime"/> for llama.cpp. Events are raised on the window's thread.
/// </summary>
public sealed class ModelDownloads(HttpClient proxied, HttpClient direct, ILog log, Action<string, string> installed)
{
    /// <summary>The key of the llama.cpp download.</summary>
    public const string Runtime = "llama.cpp";

    private readonly ModelDownloader _downloader = new(proxied, direct);
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly Dictionary<string, ModelProgress> _progress = [];
    private readonly Dictionary<string, string> _errors = [];

    /// <summary>A download started, moved, finished or failed (its key).</summary>
    public event Action<string>? Changed;

    public bool IsRunning(string key) => _running.ContainsKey(key);

    public ModelProgress? ProgressOf(string key) => _progress.TryGetValue(key, out var p) ? p : null;

    public string? ErrorOf(string key) => _errors.GetValueOrDefault(key);

    public void Start(ModelEntry entry, string folder) =>
        Run(entry.Profile, entry.Size, $"{entry.Url} → {folder}", (p, ct) => _downloader.DownloadAsync(entry, folder, p, ct));

    /// <summary>A llama.cpp build into <paramref name="root"/>; the finished one is reported with its llama-server.exe.</summary>
    public void StartRuntime(RuntimeEntry entry, string root) =>
        Run(Runtime, entry.Size, $"llama.cpp {entry.Release} {entry.Id} → {root}",
            (p, ct) => new RuntimeInstaller(_downloader).InstallAsync(entry, root, p, ct));

    private async void Run(string key, long size, string what, Func<IProgress<ModelProgress>, CancellationToken, Task<string>> download)
    {
        if (_running.ContainsKey(key)) return;
        var cts = new CancellationTokenSource();
        _running[key] = cts;
        _errors.Remove(key);
        _progress[key] = new ModelProgress(0, size, false);
        Changed?.Invoke(key);
        var progress = new Progress<ModelProgress>(p =>
        {
            _progress[key] = p;
            Changed?.Invoke(key);
        });
        try
        {
            log.Info($"download: {what}");
            var path = await Task.Run(() => download(progress, cts.Token));
            log.Info($"downloaded: {path}");
            installed(key, path);
        }
        catch (OperationCanceledException)
        {
            _errors[key] = "Загрузка остановлена - продолжится с того же места.";
        }
        catch (Exception ex)
        {
            log.Error("download", ex);
            _errors[key] = ex is InvalidDataException ? ex.Message : "Не удалось скачать: " + ex.Message;
        }
        finally
        {
            _running.Remove(key);
            _progress.Remove(key);
            cts.Dispose();
            Changed?.Invoke(key);
        }
    }

    public void Cancel(string key)
    {
        if (_running.TryGetValue(key, out var cts)) cts.Cancel();
    }
}
