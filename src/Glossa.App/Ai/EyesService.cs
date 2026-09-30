using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Logging;

namespace Glossa.App.Ai;

/// <summary>
/// The eyes' own llama-server (<see cref="ModelCatalog.Eyes"/>): a small model that reads the text at a lookup's point
/// when the recognizer found none, for the models whose own sight misreads stylized text. It comes up after the main
/// model (with it when there is room in memory, else at the first reading), leaves after the idle time the user set,
/// and one that died on the video card (out of memory) comes back on the processor with one notice. Only the "eyes"
/// role of the shared host is touched: the main model's server is the router's.
/// </summary>
public sealed class EyesService : IDisposable
{
    public const string Role = "eyes";

    /// <summary>The one-time notice after the eyes left the video card for the processor.</summary>
    public const string FellBack = "Глаза не поместились в видеопамять и перешли на процессор - чтение будет медленнее. " +
        "Вернуть видеокарту: Настройки -> ИИ и модели -> Глаза.";

    private readonly Func<AppSettings> _settings;
    private readonly LlamaServerHost _host;
    private readonly HttpClient _http;
    private readonly Func<int> _freeRamMb;
    private readonly Func<int> _freeVramMb;
    private readonly ILog _log;
    /// <summary>One start or stop of the eyes' server at a time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Cancelled when Glossa closes: a start in progress gives up and its process is killed.</summary>
    private readonly CancellationTokenSource _life = new();
    private readonly Timer _idleTimer;
    private readonly object _state = new();
    private ILlmClient? _client;
    private string? _device;
    private bool _forceCpu;
    private int _inUse;
    private DateTime _lastUse = DateTime.UtcNow;

    public EyesService(Func<AppSettings> settings, LlamaServerHost host, HttpClient localHttp, Func<int> freeRamMb, Func<int> freeVramMb, ILog log)
    {
        _settings = settings;
        _host = host;
        _http = localHttp;
        _freeRamMb = freeRamMb;
        _freeVramMb = freeVramMb;
        _log = log;
        _host.Crashed += OnCrashed;
        _idleTimer = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>The eyes came up on the video card or left it (the router reads its free video memory afresh).</summary>
    public event Action? MovedOnCard;

    /// <summary>Something the user should know once (the eyes moved to the processor); raised on a background thread.</summary>
    public event Action<string>? Notice;

    /// <summary>The eyes read for the current settings: on, downloaded, and the model's own sight not trusted with stylized text.</summary>
    public bool UsesEyes(AppSettings s) => s.LocalAi.Mode != "off" && s.LocalAi.UsesEyes(s.Eyes);

    public bool IsAlive => _host.IsAlive(Role);

    /// <summary>Where the eyes read now: "cpu", "gpu" or null (not in memory).</summary>
    public string? Device
    {
        get { lock (_state) return _client is not null && _host.IsAlive(Role) ? _device : null; }
    }

    /// <summary>They were moved to the processor after dying on the video card (until the settings change).</summary>
    public bool ForcedToProcessor
    {
        get { lock (_state) return _forceCpu; }
    }

    /// <summary>
    /// The eyes' client, their server started first if needed; null when they are not used or could not start (the
    /// caller then reads with the model's own sight). Only a cancellation of <paramref name="ct"/> passes through.
    /// </summary>
    public async Task<ILlmClient?> EnsureAsync(CancellationToken ct)
    {
        var s = _settings();
        if (!UsesEyes(s)) return null;
        lock (_state) _lastUse = DateTime.UtcNow;
        if (Ready(s) is { } ready) return ready;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        await _gate.WaitAsync(both.Token).ConfigureAwait(false);
        try
        {
            return Ready(s) ?? await StartAsync(s, Wanted(s), both.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Wanted(AppSettings s)
    {
        lock (_state) return _forceCpu ? "cpu" : s.Eyes.Device;
    }

    /// <summary>The running eyes' client if they run where they should; a server found dead is taken as a crash first.</summary>
    private ILlmClient? Ready(AppSettings s)
    {
        var died = false;
        ILlmClient? ready;
        lock (_state)
        {
            if (_client is not null && !_host.IsAlive(Role)) died = NoteDeathLocked();
            ready = _client is { } c && _device == (_forceCpu ? "cpu" : s.Eyes.Device) ? c : null;
        }
        if (died) Moved(off: true);
        return ready;
    }

    /// <summary>
    /// The server behind the client is dead: the client is dropped, and one on the video card sends the eyes to the
    /// processor. Idempotent, so it does not matter whether the lookup or the process' exit event notices first.
    /// </summary>
    private bool NoteDeathLocked()
    {
        var onCard = _device == "gpu" && !_forceCpu;
        _client = null;
        _device = null;
        if (onCard) _forceCpu = true;
        return onCard;
    }

    private void Moved(bool off)
    {
        MovedOnCard?.Invoke();
        if (off) Notice?.Invoke(FellBack);
    }

    private async Task<ILlmClient?> StartAsync(AppSettings s, string device, CancellationToken ct)
    {
        var local = s.LocalAi;
        var server = local.LlamaServerResolved();
        if (!File.Exists(server))
        {
            _log.Warn($"eyes: no llama-server at {server}");
            return null;
        }
        if (device == "gpu") _log.Info($"eyes: free video memory {_freeVramMb()} MB, they need about {EyesPolicy.NeedVramMb}");
        try
        {
            var endpoint = await _host.EnsureAsync(Role, server, local.EyesModel(), local.EyesPort, EyesPolicy.ContextSize,
                EyesPolicy.Placement(device, local.EyesVisionFile(), s.Performance.Priority != "normal"), ct).ConfigureAwait(false);
            var client = new OpenAiCompatibleClient(_http, endpoint);
            lock (_state)
            {
                _client = client;
                _device = device;
                _lastUse = DateTime.UtcNow;
            }
            _log.Info($"eyes: up on the {(device == "gpu" ? "video card" : "processor")}");
            if (device == "gpu") Moved(off: false);
            return client;
        }
        catch (LlmException ex) when (device == "gpu" && ex.InnerException is ServerExitedException && !ct.IsCancellationRequested)
        {
            // The server died starting on the card (out of video memory with the driver's fallback to RAM switched off):
            // the processor, and the user is told once. Other failures (a missing file) are no reason to move.
            _log.Warn($"eyes: could not start on the video card ({ex.Message}); starting on the processor");
            lock (_state) _forceCpu = true;
            Notice?.Invoke(FellBack);
            return await StartAsync(s, "cpu", ct).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            _log.Warn($"eyes: could not start ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Right after the main model loaded: the eyes come up too when it reads with them and enough RAM is left (6 GB -
    /// on 16 GB with a game they would push into the page file), else at the first reading that needs them.
    /// </summary>
    /// <param name="busy">The model is writing a card: the eyes wait for it (their load took 15 s off the first card).</param>
    public void WarmAfterMain(AiClients main, Func<bool> busy)
    {
        var s = _settings();
        if (!UsesEyes(s) || main.Dictionary is not { Endpoint.IsLocal: true }) return;
        var free = _freeRamMb();
        if (!EyesPolicy.ShouldWarm(free))
        {
            _log.Info($"eyes: {free} MB of RAM free after the model; they come up at the first reading");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (var waited = 0; busy() && waited < 120; waited++) await Task.Delay(1000, _life.Token).ConfigureAwait(false);
                await EnsureAsync(_life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Warn($"eyes: warming up failed ({ex.Message})"); }
        });
    }

    /// <summary>Before the main model loads, or when it is unloaded: eyes on the video card leave (they come back after it).</summary>
    public void StopIfGpu()
    {
        bool onCard;
        lock (_state) onCard = _device == "gpu";
        if (onCard && _host.IsRunning(Role)) Stop();
    }

    /// <summary>Stops the eyes' server, after any start in progress (a start and a stop never cross).</summary>
    public void Stop()
    {
        _gate.Wait();
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    private void StopCore()
    {
        bool wasOnCard;
        lock (_state)
        {
            wasOnCard = _device == "gpu";
            _client = null;
            _device = null;
        }
        _host.Stop(Role);
        if (wasOnCard) Moved(off: false);
    }

    /// <summary>
    /// Settings changed: stopped, and a move to the processor forgotten (the video card may be chosen again). Done in the
    /// background after any start in progress, so the settings window never waits for a load.
    /// </summary>
    public void Reset()
    {
        lock (_state) _forceCpu = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                try { StopCore(); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Warn($"eyes: reset failed ({ex.Message})"); }
        });
    }

    /// <summary>The server died while reading: the next start (on the processor if it was the video card) is tried at once.</summary>
    public Task<ILlmClient?> RecoverAsync(CancellationToken ct) => EnsureAsync(ct);

    /// <summary>Held while a reading runs: the eyes are not unloaded under it, and their idle time starts when it ends.</summary>
    public IDisposable Use()
    {
        lock (_state) _inUse++;
        return new Using(this);
    }

    private sealed class Using(EyesService eyes) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (eyes._state)
            {
                eyes._inUse--;
                eyes._lastUse = DateTime.UtcNow;
            }
        }
    }

    private void OnCrashed(string role, int code)
    {
        if (role != Role) return;
        bool onCard;
        lock (_state) onCard = NoteDeathLocked();
        _log.Warn($"eyes: the server died (code {code}){(onCard ? "; they come back on the processor" : "")}");
        if (onCard) Moved(off: true);
    }

    /// <summary>Every 5 s: eyes idle longer than the user's setting, or no longer used (turned off, another model), leave.</summary>
    private void Watch()
    {
        try
        {
            if (!_host.IsRunning(Role)) return;
            var s = _settings();
            var unused = !UsesEyes(s);
            TimeSpan idle;
            lock (_state)
            {
                // Decided and the client dropped together: a lookup that takes the eyes from now on starts them anew.
                idle = DateTime.UtcNow - _lastUse;
                if (_inUse > 0 || !unused && !EyesPolicy.ShouldUnload(s.Eyes.IdleUnloadMinutes, idle)) return;
                _client = null;
            }
            _log.Info(unused ? "eyes: no longer used; unloading" : $"eyes: idle {idle.TotalMinutes:0} min; unloading");
            if (!_gate.Wait(0)) return; // a start is in progress: next time
            try { StopCore(); }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            _log.Error("eyes watch", ex);
        }
    }

    public void Dispose()
    {
        _host.Crashed -= OnCrashed;
        _life.Cancel();
        _idleTimer.Dispose();
    }
}
