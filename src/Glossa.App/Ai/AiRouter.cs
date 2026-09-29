using System.Runtime.InteropServices;
using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Logging;

namespace Glossa.App.Ai;

/// <param name="SeparateTranslation">
/// The context line is translated by its own request (a user's translator endpoint, or the same model with the
/// translator prompt, which keeps swearing and tone better than the card's JSON field); the card then skips it.
/// </param>
public sealed record AiClients(ILlmClient? Dictionary, ILlmClient? Translator, string Description, bool SeparateTranslation = false);

/// <summary>
/// Chooses which models serve the dictionary and the translator, from settings and free VRAM,
/// and hands out clients. User-supplied endpoints take precedence over local models when selected.
/// </summary>
public sealed class AiRouter : IDisposable
{
    private const string DictRole = "dictionary";

    private readonly Func<AppSettings> _settings;
    private readonly LlamaServerHost _host;
    private readonly HttpClient _localHttp;
    private readonly HttpClient _remoteHttp;
    private readonly Func<string, string?> _apiKey;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AiClients? _current;
    private string? _currentMode;
    private DateTime _lastUse = DateTime.UtcNow;
    private DateTime _loadedAt = DateTime.MinValue;
    private int _freeAfterLoad = -1;
    private int _inUse;
    private readonly Timer _idleTimer;

    public AiRouter(Func<AppSettings> settings, LlamaServerHost host, HttpClient localHttp, HttpClient remoteHttp,
        Func<string, string?> apiKey, ILog log)
    {
        _settings = settings;
        _host = host;
        _localHttp = localHttp;
        _remoteHttp = remoteHttp;
        _apiKey = apiKey;
        _log = log;
        _idleTimer = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public AiClients? Current => _current;

    /// <summary>True while the program of the last lookup (usually a game) is in front: the model leaves memory sooner.</summary>
    public Func<bool>? InGame { get; set; }

    /// <summary>
    /// Marks the AI as busy for the lifetime of the returned handle (a lookup, a check): nothing is unloaded under it,
    /// and the idle clock starts when it ends, not when it began.
    /// </summary>
    public IDisposable Use()
    {
        Interlocked.Increment(ref _inUse);
        _lastUse = DateTime.UtcNow;
        return new Busy(this);
    }

    private sealed class Busy(AiRouter router) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Decrement(ref router._inUse);
            router._lastUse = DateTime.UtcNow;
        }
    }

    /// <param name="mode">A game profile's own mode («ИИ в этой игре»: lowvram), or null for the general one. A model
    /// loaded for another mode is restarted in the one asked for.</param>
    public async Task<AiClients> GetAsync(CancellationToken ct, string? mode = null)
    {
        _lastUse = DateTime.UtcNow;
        mode ??= _settings().LocalAi.Mode;
        bool Usable(AiClients c) => _currentMode == mode && (c.Dictionary is null || _host.AllAlive || !c.Dictionary.Endpoint.IsLocal);
        if (_current is { } c && Usable(c)) return c;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current is { } again && Usable(again)) return again;
            if (_current is not null && _currentMode != mode)
            {
                _log.Info($"AI: switching to mode {mode}");
                _current = null;
                _host.StopAll();
            }
            _currentMode = mode;
            _current = await ResolveAsync(mode, ct).ConfigureAwait(false);
            _loadedAt = DateTime.UtcNow;
            _freeAfterLoad = -1;
            _log.Info($"AI: {_current.Description}");
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the current choice (settings changed); servers are restarted on the next lookup.</summary>
    public void Reset()
    {
        _current = null;
        _host.StopAll();
    }

    /// <summary>Настройки → Нагрузка на ПК → «Низкий»: llama-server's threads yield to the game as well.</summary>
    private static IReadOnlyList<string> WithPriority(AppSettings s, IReadOnlyList<string> placement) =>
        s.Performance.Priority == "normal" ? placement : [.. placement, "--prio", "-1"];

    /// <summary>
    /// One local model for the card and the context translation. "auto" lets llama.cpp fit it into free VRAM
    /// (keeping <see cref="LocalAiSettings.VramReserveMb"/> free), moving MoE experts or layers to RAM as needed;
    /// "lowvram" keeps every MoE expert in RAM (~2 GB of VRAM for Gemma 26B, ~3x slower).
    /// </summary>
    private async Task<AiClients> ResolveAsync(string mode, CancellationToken ct)
    {
        var s = _settings();
        var local = s.LocalAi;
        var dictCustom = Custom(s, s.DictionaryEngine);
        var trCustom = Custom(s, s.TranslatorEngine);
        var off = mode == "off";
        var lowVram = mode is "lowvram" or "light";

        ILlmClient? dict = dictCustom;
        if (dict is null && !off)
        {
            var model = local.SingleModel(local.Profile) ?? "";
            if (!File.Exists(model))
                throw new LlmException(model.Length == 0
                    ? "Не выбран файл своей модели — Настройки → ИИ и модели."
                    : $"Модель не скачана ({Path.GetFileName(model)}) — Настройки → ИИ и модели → «Скачать».");
            var server = local.LlamaServerResolved();
            if (!File.Exists(server))
                throw new LlmException(local.LlamaServerPath.Length > 0
                    ? $"Не найден свой llama-server ({server}) — Настройки → ИИ и модели → Файлы."
                    : "Движок llama.cpp не скачан — Настройки → ИИ и модели → Движок → «Скачать».");
            // Only the MoE model has experts to keep in RAM; the dense and small ones are fitted to free VRAM either way.
            IReadOnlyList<string> placement = lowVram && local.Profile == "gemma26b"
                ? ["--n-gpu-layers", "99", "--cpu-moe"]
                : ["--fit", "on", "--fit-target", local.VramReserveMb.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            var ep = await _host.EnsureAsync(DictRole, server, model, local.BasePort, local.ContextSize, WithPriority(s, placement), ct).ConfigureAwait(false);
            dict = new OpenAiCompatibleClient(_localHttp, ep);
        }
        var tr = trCustom ?? dict;
        var desc = $"{local.Profile}{(lowVram ? " (минимум видеопамяти)" : "")}, словарь: {dict?.Endpoint.Name ?? "нет"}, перевод: {tr?.Endpoint.Name ?? "нет"}";
        // A local model translates the context line with its translator prompt as a separate request.
        var separate = tr is not null && (!ReferenceEquals(tr, dict) || dict!.Endpoint.Kind == LlmProviderKind.LlamaServer);
        return new AiClients(dict, tr, desc, separate);
    }

    private ILlmClient? Custom(AppSettings s, string engine)
    {
        if (engine == "local") return null;
        var e = s.CustomEndpoints.FirstOrDefault(x => x.Name == engine);
        if (e is null) return null;
        var ep = new LlmEndpoint(e.Name, e.Kind, e.BaseUrl, e.Model, _apiKey(e.Name), e.Structured);
        if (e.Kind == LlmProviderKind.Anthropic) return new AnthropicLlmClient(ep);
        return new OpenAiCompatibleClient(ep.IsLocal ? _localHttp : _remoteHttp, ep);
    }

    /// <summary>Every 5 s while a local model is loaded: idle too long, or a game short of video memory, unloads it.</summary>
    private void Watch()
    {
        try
        {
            if (Volatile.Read(ref _inUse) > 0 || _host.RunningRoles.Count == 0) return;
            var s = _settings();
            var game = InGame?.Invoke() == true;
            var now = DateTime.UtcNow;
            // Free video memory is read only when it can matter; the first reading after the load settles is what the
            // model itself left, so later drops below it are someone else's (a game's).
            var free = s.Performance.YieldVram && now - _loadedAt >= ModelLifetime.Settle ? FreeVramMb() : -1;
            if (_freeAfterLoad < 0 && free >= 0) _freeAfterLoad = free;
            if (ModelLifetime.UnloadReason(s, game, now - _lastUse, now - _loadedAt, free, _freeAfterLoad) is { } reason)
            {
                _log.Info("AI: unloading local models — " + reason);
                Unload();
            }
        }
        catch (Exception ex)
        {
            _log.Error("AI watch", ex);
        }
    }

    private void Unload()
    {
        _current = null;
        _host.StopAll();
    }

    /// <summary>Free memory on GPU 0 in MB via NVML (ships with the NVIDIA driver); -1 when unavailable.</summary>
    public static int FreeVramMb() => Vram().FreeMb;

    /// <summary>Free and total memory on GPU 0 in MB; -1 for both when NVML is unavailable.</summary>
    public static (int FreeMb, int TotalMb) Vram()
    {
        try
        {
            if (nvmlInit_v2() != 0) return (-1, -1);
            try
            {
                if (nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0) return (-1, -1);
                if (nvmlDeviceGetMemoryInfo(dev, out var mem) != 0) return (-1, -1);
                return ((int)(mem.Free / (1024 * 1024)), (int)(mem.Total / (1024 * 1024)));
            }
            finally
            {
                nvmlShutdown();
            }
        }
        catch (DllNotFoundException) { return (-1, -1); }
        catch (EntryPointNotFoundException) { return (-1, -1); }
    }

    /// <summary>The llama.cpp build for this PC's card: CUDA for a Turing or newer NVIDIA card on a 580+ driver, else Vulkan.</summary>
    public static RuntimeEntry RecommendedRuntime()
    {
        try
        {
            if (nvmlInit_v2() != 0) return RuntimeCatalog.Recommended(0, 0);
            try
            {
                var version = new System.Text.StringBuilder(80);
                if (nvmlSystemGetDriverVersion(version, (uint)version.Capacity) != 0
                    || !int.TryParse(version.ToString().Split('.')[0], out var driver)) return RuntimeCatalog.Recommended(0, 0);
                if (nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0
                    || nvmlDeviceGetCudaComputeCapability(dev, out var major, out var minor) != 0) return RuntimeCatalog.Recommended(0, 0);
                return RuntimeCatalog.Recommended(driver, major + minor / 10.0);
            }
            finally
            {
                nvmlShutdown();
            }
        }
        catch (DllNotFoundException) { return RuntimeCatalog.Recommended(0, 0); }
        catch (EntryPointNotFoundException) { return RuntimeCatalog.Recommended(0, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total, Free, Used; }

    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(System.Text.StringBuilder version, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    public void Dispose()
    {
        _idleTimer.Dispose();
        _gate.Dispose();
    }
}
