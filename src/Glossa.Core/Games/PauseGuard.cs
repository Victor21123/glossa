namespace Glossa.Core.Games;

/// <summary>Suspends and resumes a whole process (Windows: NtSuspendProcess / NtResumeProcess).</summary>
public interface IProcessSuspender
{
    /// <summary>False with a reason when the process cannot be paused (gone, no rights, elevated).</summary>
    bool Suspend(int pid, out string? error);

    void Resume(int pid);
}

/// <summary>
/// «Пауза игры» made safe. It lives in a separate process (Glossa.exe --resume-guard) and keeps count of what it
/// suspended, so a game is always woken: when the card closes, when the time limit runs out, when the panic key is
/// pressed, and when Glossa itself is gone — crashed or killed. Every suspend is matched by exactly one resume.
/// </summary>
public sealed class PauseGuard(IProcessSuspender suspender, TimeSpan limit)
{
    private DateTime _since;

    /// <summary>The paused process, or null.</summary>
    public int? Paused { get; private set; }

    public TimeSpan Limit { get; } = limit;

    /// <summary>Pauses <paramref name="pid"/> (waking another paused one first); the error, or null on success.</summary>
    public string? Pause(int pid, DateTime now)
    {
        if (Paused == pid) return null;
        Resume();
        if (!suspender.Suspend(pid, out var error)) return error ?? "не удалось остановить игру";
        Paused = pid;
        _since = now;
        return null;
    }

    /// <summary>Wakes the paused process, if any.</summary>
    public void Resume()
    {
        if (Paused is not { } pid) return;
        Paused = null;
        suspender.Resume(pid);
    }

    /// <summary>Called about once a second: wakes the game when it has slept longer than the limit. True when it did.</summary>
    public bool Tick(DateTime now)
    {
        if (Paused is null || now - _since < Limit) return false;
        Resume();
        return true;
    }

    /// <summary>One line of the pipe protocol from Glossa: «pause 1234», «resume», «ping». The answer: «ok» or «error …».</summary>
    public string Handle(string command, DateTime now)
    {
        var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault())
        {
            case "pause" when parts.Length == 2 && int.TryParse(parts[1], out var pid) && pid > 0:
                return Pause(pid, now) is { } error ? "error " + error : "ok";
            case "resume":
                Resume();
                return "ok";
            case "ping":
                return "ok";
            default:
                return "error unknown command";
        }
    }
}
