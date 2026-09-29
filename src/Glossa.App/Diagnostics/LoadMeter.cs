using System.Diagnostics;
using Glossa.App.Ai;

namespace Glossa.App.Diagnostics;

/// <summary>
/// What Glossa costs the PC at one moment: memory of Glossa and of its llama-server processes (working set, as the
/// Task Manager shows), processor time used so far, and free video memory on the GPU.
/// </summary>
public sealed record LoadSample(DateTime At, double GlossaMb, double ServerMb, double GlossaCpu, double ServerCpu, int VramFreeMb, int VramTotalMb)
{
    /// <summary>Processor seconds used so far by Glossa and its servers together.</summary>
    public double CpuSeconds => GlossaCpu + ServerCpu;

    /// <summary>Share of all cores used between two samples, 0–100.</summary>
    public double CpuPercentSince(LoadSample before) =>
        (At - before.At).TotalSeconds is var s and > 0 ? 100 * (CpuSeconds - before.CpuSeconds) / (s * Environment.ProcessorCount) : 0;
}

public static class LoadMeter
{
    public static LoadSample Sample(LlamaServerHost host)
    {
        using var me = Process.GetCurrentProcess();
        double serverMb = 0, serverCpu = 0;
        foreach (var p in host.Processes)
        {
            try
            {
                p.Refresh();
                serverMb += p.WorkingSet64 / 1048576.0;
                serverCpu += p.TotalProcessorTime.TotalSeconds;
            }
            catch (InvalidOperationException)
            {
                // exited between listing and reading
            }
        }
        var (free, total) = AiRouter.Vram();
        return new LoadSample(DateTime.Now, me.WorkingSet64 / 1048576.0, serverMb, me.TotalProcessorTime.TotalSeconds, serverCpu, free, total);
    }
}
