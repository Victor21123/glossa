using System.Diagnostics;
using System.Runtime.InteropServices;
using Glossa.App.Interop;
using Glossa.Core.Llm;
using Glossa.Core.Logging;

namespace Glossa.App.Ai;

/// <summary>
/// Starts and stops llama-server processes. They live in a job object that kills them when Glossa exits,
/// crashes included, so a dead app never leaves gigabytes of VRAM occupied.
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    private sealed record Instance(string Key, int Port, Process Process, LlmEndpoint Endpoint);

    private readonly ILog _log;
    private readonly HttpClient _http;
    private readonly Func<int> _freeVramMb, _freeRamMb;
    private readonly IntPtr _job;
    private readonly Dictionary<string, Instance> _running = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="freeVramMb">Free video memory as the card reports it (NVML), -1 when unknown.</param>
    /// <param name="freeRamMb">Free RAM, -1 when unknown.</param>
    public LlamaServerHost(ILog log, HttpClient localHttp, Func<int> freeVramMb, Func<int> freeRamMb)
    {
        _log = log;
        _http = localHttp;
        _freeVramMb = freeVramMb;
        _freeRamMb = freeRamMb;
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, ref info,
            Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }

    /// <summary>The running llama-server processes (for the load meter).</summary>
    public IReadOnlyList<Process> Processes
    {
        get { lock (_running) return _running.Values.Select(i => i.Process).Where(p => !p.HasExited).ToList(); }
    }

    public IReadOnlyCollection<string> RunningRoles
    {
        get { lock (_running) return _running.Keys.ToList(); }
    }

    /// <summary>The role's server is running and has not died (a crash or the driver killing it on VRAM loss).</summary>
    public bool IsAlive(string role)
    {
        lock (_running) return _running.TryGetValue(role, out var inst) && !inst.Process.HasExited;
    }

    /// <summary>A server was started for the role and not stopped (it may have died since).</summary>
    public bool IsRunning(string role)
    {
        lock (_running) return _running.ContainsKey(role);
    }

    /// <summary>A server exited on its own (not stopped): its role and exit code. Raised on a thread-pool thread.</summary>
    public event Action<string, int>? Crashed;

    /// <summary>
    /// Makes sure <paramref name="modelPath"/> is served for <paramref name="role"/> and returns its endpoint.
    /// <paramref name="placement"/> decides where the weights go ("--n-gpu-layers 99", "--fit on", "--cpu-moe");
    /// a different placement restarts the server.
    /// </summary>
    public async Task<LlmEndpoint> EnsureAsync(string role, string serverExe, string modelPath, int port, int ctxSize,
        IReadOnlyList<string> placement, CancellationToken ct)
    {
        var key = modelPath + "|" + string.Join(' ', placement);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running.TryGetValue(role, out var inst))
            {
                if (inst.Key == key && !inst.Process.HasExited) return inst.Endpoint;
                StopUnlocked(role);
            }

            if (!File.Exists(serverExe)) throw new LlmException($"Не найден llama-server: {serverExe}");
            if (!File.Exists(modelPath)) throw new LlmException($"Не найдена модель: {modelPath}");
            var (args, others) = await FitAsync(role, serverExe, modelPath, placement, ct).ConfigureAwait(false);

            var alias = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
            var psi = new ProcessStartInfo(serverExe)
            {
                WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
            {
                "--model", modelPath, "--alias", alias, "--ctx-size", ctxSize.ToString(),
                "--flash-attn", "on", "--jinja", "--parallel", "1",
                // Read the weights instead of mapping the file: the mapped 13 GB stayed in llama-server's memory although
                // they sit on the GPU (selftest 2026-09-29: 14.6 GB → 2.8 GB of RAM, same speed, faster reload).
                "--load-mode", "none", "--poll", "0",
                "--host", "127.0.0.1", "--port", port.ToString(), "--no-webui",
            }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            // For measurements (Glossa.exe --selftest): extra llama-server options without a rebuild; later ones win.
            if (Environment.GetEnvironmentVariable("GLOSSA_LLAMA_ARGS") is { Length: > 0 } extra)
                foreach (var a in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            // Keep the CUDA kernel cache with Glossa's data rather than on the system drive.
            psi.Environment["CUDA_CACHE_PATH"] = Glossa.Core.Config.DataPaths.CudaCache;
            psi.Environment["CUDA_CACHE_MAXSIZE"] = "4294967296";

            var process = Process.Start(psi) ?? throw new LlmException("llama-server не запустился");
            Native.AssignProcessToJobObject(_job, process.Handle);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnExited(role, process);
            var outOfMemory = false;
            void Line(string? line)
            {
                if (line is not null && VramFit.IsOutOfMemory(line)) outOfMemory = true;
                LogServer(role, line);
            }
            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var endpoint = new LlmEndpoint($"local:{alias}", LlmProviderKind.LlamaServer, $"http://127.0.0.1:{port}/v1", alias);
            _log.Info($"llama-server[{role}] starting {alias} on :{port} (pid {process.Id}) {string.Join(' ', args)}");

            var sw = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        process.WaitForExit(); // its last lines, the reason among them, are read first
                        throw new LlmException(outOfMemory ? VramFit.Short(others) : $"llama-server завершился с кодом {process.ExitCode} - см. лог",
                            new ServerExitedException(process.ExitCode));
                    }
                    if (sw.Elapsed > StartLimit) throw new LlmException($"llama-server не ответил за {StartLimit.TotalSeconds:0} с - см. лог");
                    try
                    {
                        using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/health", ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Not registered yet: a start given up (a newer lookup, the app closing), failed or timed out must not
                // stay loading on its port and holding memory until Glossa exits.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
                throw;
            }
            _log.Info($"llama-server[{role}] ready in {sw.ElapsedMilliseconds} ms");
            lock (_running) _running[role] = new Instance(key, port, process, endpoint);
            return endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A cold start from a hard disk reads the whole file: 13 GB take about two minutes.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The placement as started. "--fit" plans with the free memory CUDA reports, which under Windows leaves out what
    /// other programs hold: its target is raised by that gap, so the model takes on the card what the card has free
    /// (<see cref="VramFit"/>). A model whose part off the card RAM cannot hold either is refused with the card's
    /// message rather than paging the PC. Unchanged without "--fit-target", NVML or a CUDA device. Also whether
    /// another program holds the card (for the message).
    /// </summary>
    private async Task<(IReadOnlyList<string> Args, bool Others)> FitAsync(string role, string serverExe, string modelPath,
        IReadOnlyList<string> placement, CancellationToken ct)
    {
        if (!placement.Contains("--fit-target")) return (placement, false);
        var free = _freeVramMb();
        if (free < 0) return (placement, false);
        if (VramFit.CudaFreeMb(await ListDevicesAsync(serverExe, ct).ConfigureAwait(false)) is not { } counted) return (placement, false);
        var others = counted - free >= VramFit.OthersMb;
        var needed = VramFit.RamNeededMb(placement, new FileInfo(modelPath).Length, free, f => File.Exists(f) ? new FileInfo(f).Length : 0);
        var ram = _freeRamMb();
        _log.Info($"llama-server[{role}] fit: CUDA counts {counted} MB free, the card has {free} MB; the part off the card needs {needed} MB of RAM, {ram} MB free");
        if (VramFit.RamShort(needed, ram)) throw new LlmException(VramFit.Short(others));
        return (VramFit.Adjust(placement, counted, free), others);
    }

    /// <summary>What <c>llama-server --list-devices</c> prints (~0.3 s): the devices with CUDA's own free memory. Empty on failure.</summary>
    private async Task<string> ListDevicesAsync(string serverExe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(serverExe, "--list-devices")
        {
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["CUDA_CACHE_PATH"] = Glossa.Core.Config.DataPaths.CudaCache;
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return "";
            Native.AssignProcessToJobObject(_job, process.Handle);
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                _log.Warn("llama-server --list-devices did not answer in 20 s");
                return "";
            }
            return await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _log.Warn($"llama-server --list-devices failed: {ex.Message}");
            return "";
        }
    }

    public void Stop(string role)
    {
        _gate.Wait();
        try { StopUnlocked(role); }
        finally { _gate.Release(); }
    }

    public void StopAll()
    {
        _gate.Wait();
        try { foreach (var role in RunningRoles) StopUnlocked(role); }
        finally { _gate.Release(); }
    }

    private void StopUnlocked(string role)
    {
        Instance? inst;
        lock (_running)
            if (!_running.Remove(role, out inst)) return;
        try
        {
            // Waited for: the card frees its memory with the process, and a start right after measures what is free.
            if (!inst.Process.HasExited) inst.Process.Kill(entireProcessTree: true);
            inst.Process.WaitForExit(2000);
        }
        catch (InvalidOperationException) { }
        inst.Process.Dispose();
        _log.Info($"llama-server[{role}] stopped");
    }

    private void OnExited(string role, Process process)
    {
        // Stopping removes the role first, so a registered process that exits has crashed.
        bool crashed;
        lock (_running) crashed = _running.TryGetValue(role, out var inst) && ReferenceEquals(inst.Process, process);
        if (!crashed) return;
        _log.Info($"llama-server[{role}] exited unexpectedly (code {process.ExitCode}); it restarts on the next lookup");
        Crashed?.Invoke(role, process.ExitCode);
    }

    private static readonly bool LogAll = Environment.GetEnvironmentVariable("GLOSSA_LLAMA_LOG") == "all";

    private void LogServer(string role, string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        // llama-server is chatty; keep errors and the load summary (where the weights, cache and buffers went).
        // For measurements GLOSSA_LLAMA_LOG=all keeps every line (prompt cache reuse, timings).
        if (LogAll || line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("model loaded", StringComparison.OrdinalIgnoreCase) || line.Contains("buffer size", StringComparison.OrdinalIgnoreCase)
            || line.Contains("offload", StringComparison.OrdinalIgnoreCase) || line.Contains("llama_params_fit", StringComparison.OrdinalIgnoreCase))
            _log.Info($"llama[{role}] {line}");
    }

    public void Dispose()
    {
        StopAll();
        _gate.Dispose();
    }
}

/// <summary>llama-server exited while starting (out of memory, a bad file, the port taken): its exit code.</summary>
public sealed class ServerExitedException(int code) : Exception($"llama-server exited with code {code}")
{
    public int Code { get; } = code;
}
