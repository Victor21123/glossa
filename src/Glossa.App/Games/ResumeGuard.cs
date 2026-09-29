using System.Diagnostics;
using System.IO.Pipes;
using Glossa.App.Interop;
using Glossa.Core.Config;
using Glossa.Core.Games;
using Glossa.Core.Logging;

namespace Glossa.App.Games;

/// <summary>Suspends a whole process with NtSuspendProcess and keeps its handle until it is resumed.</summary>
internal sealed class Win32Suspender : IProcessSuspender
{
    private readonly Dictionary<int, IntPtr> _handles = [];

    public bool Suspend(int pid, out string? error)
    {
        error = null;
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
        {
            error = "нет доступа к игре (она запущена от администратора?)";
            return false;
        }
        var status = Native.NtSuspendProcess(h);
        if (status != 0)
        {
            Native.CloseHandle(h);
            error = $"Windows не дала остановить игру (0x{status:X8})";
            return false;
        }
        _handles[pid] = h;
        return true;
    }

    public void Resume(int pid)
    {
        if (!_handles.Remove(pid, out var h)) return;
        Native.NtResumeProcess(h);
        Native.CloseHandle(h);
    }

    /// <summary>Glossa's own last resort when the guard is gone: wakes the game it asked to pause.</summary>
    public static void ResumeDirectly(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return;
        Native.NtResumeProcess(h);
        Native.CloseHandle(h);
    }
}

/// <summary>
/// Glossa's side of «Пауза игры»: asks the guard process to pause and wake the game over a named pipe, starting the
/// guard on first use. If the guard is gone when the game should wake, Glossa wakes it itself.
/// </summary>
public sealed class ResumeGuardClient(ILog log) : IDisposable
{
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _in;
    private StreamWriter? _out;
    private int? _paused;

    public static string PipeName(int owner) => $"Glossa.ResumeGuard.{owner}";

    public bool IsPaused
    {
        get { lock (_gate) return _paused is not null; }
    }

    /// <summary>Pauses the game's process; null on success, else why not (the lookup then goes on without a pause).</summary>
    public string? Pause(int pid)
    {
        lock (_gate)
        {
            var reply = Send($"pause {pid}", startGuard: true);
            if (reply == "ok")
            {
                _paused = pid;
                return null;
            }
            return reply is not null && reply.StartsWith("error ", StringComparison.Ordinal) ? reply[6..] : "страж паузы не отвечает";
        }
    }

    /// <summary>Wakes the paused game (the card closed, the still frame closed).</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_paused is not { } pid) return;
            _paused = null;
            if (Send("resume", startGuard: false) != "ok")
            {
                log.Warn($"resume guard gone — waking {pid} directly");
                Win32Suspender.ResumeDirectly(pid);
            }
        }
    }

    private string? Send(string line, bool startGuard)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Connect(startGuard)) return null;
                _out!.WriteLine(line);
                _out.Flush();
                var read = _in!.ReadLineAsync();
                if (read.Wait(TimeSpan.FromSeconds(3))) return read.Result;
                Drop();
                return null;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AggregateException)
            {
                Drop();
            }
        }
        return null;
    }

    private bool Connect(bool startGuard)
    {
        if (_pipe is { IsConnected: true }) return true;
        Drop();
        var name = PipeName(Environment.ProcessId);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try
        {
            pipe.Connect(150);
        }
        catch (TimeoutException)
        {
            if (!startGuard)
            {
                pipe.Dispose();
                return false;
            }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--resume-guard {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
            try
            {
                pipe.Connect(3000);
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                log.Warn("resume guard did not start");
                return false;
            }
        }
        _pipe = pipe;
        _in = new StreamReader(pipe);
        _out = new StreamWriter(pipe) { AutoFlush = false };
        return true;
    }

    private void Drop()
    {
        _in?.Dispose();
        _out?.Dispose();
        _pipe?.Dispose();
        _in = null;
        _out = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Resume();
        lock (_gate) Drop();
    }
}

/// <summary>
/// Glossa.exe --resume-guard &lt;pid&gt;: a small process with no window that holds «Пауза игры». It wakes the game when
/// Glossa asks, after 5 minutes, on Ctrl+Alt+R, when Glossa disconnects and when Glossa's process ends — so a crash of
/// Glossa can never leave a game frozen. It quits after 10 idle minutes; Glossa starts it again when needed.
/// </summary>
internal static class ResumeGuardHost
{
    private const int PanicHotkey = 1;
    private const uint WmCheck = Native.WM_APP + 2, VK_R = 0x52;

    public static int Run(int ownerPid)
    {
        var log = new FileLogger(DataPaths.Logs);
        var owner = Native.OpenProcess(Native.SYNCHRONIZE, false, ownerPid);
        if (owner == IntPtr.Zero) return 1;
        var guard = new PauseGuard(new Win32Suspender(), TimeSpan.FromMinutes(5));
        var gate = new object();
        var mainThread = Native.GetCurrentThreadId();
        log.Info($"guard: started for Glossa {ownerPid}");

        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(ResumeGuardClient.PipeName(ownerPid), PipeDirection.InOut, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        string reply;
                        lock (gate) reply = guard.Handle(line, DateTime.UtcNow);
                        log.Info($"guard: {line} → {reply}");
                        Native.PostThreadMessage(mainThread, WmCheck, IntPtr.Zero, IntPtr.Zero);
                        writer.WriteLine(reply);
                        writer.Flush();
                    }
                }
                catch (IOException)
                {
                }
                // Glossa hung up: nobody is left to ask for the wake-up.
                lock (gate) guard.Resume();
                Native.PostThreadMessage(mainThread, WmCheck, IntPtr.Zero, IntPtr.Zero);
            }
        }) { IsBackground = true, Name = "guard pipe" }.Start();

        var hotkey = false;
        var idleSince = DateTime.UtcNow;
        var handles = new[] { owner };
        while (true)
        {
            var wait = Native.MsgWaitForMultipleObjects(1, handles, false, 1000, Native.QS_ALLINPUT);
            if (wait == 0)
            {
                lock (gate) guard.Resume();
                log.Info("guard: Glossa has gone — game woken, quitting");
                break;
            }
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                if (msg.message == Native.WM_HOTKEY)
                {
                    lock (gate) guard.Resume();
                    log.Info("guard: Ctrl+Alt+R — game woken");
                }
            }
            bool paused;
            lock (gate)
            {
                if (guard.Tick(DateTime.UtcNow)) log.Info("guard: 5 minutes passed — game woken");
                paused = guard.Paused is not null;
            }
            // The panic key belongs to the guard only while a game sleeps.
            if (paused && !hotkey) hotkey = Native.RegisterHotKey(IntPtr.Zero, PanicHotkey, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_R);
            if (!paused && hotkey)
            {
                Native.UnregisterHotKey(IntPtr.Zero, PanicHotkey);
                hotkey = false;
            }
            if (paused) idleSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromMinutes(10)) break;
        }
        if (hotkey) Native.UnregisterHotKey(IntPtr.Zero, PanicHotkey);
        Native.CloseHandle(owner);
        return 0;
    }
}
