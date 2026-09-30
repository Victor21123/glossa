using System.Globalization;
using System.Text.RegularExpressions;

namespace Glossa.Core.Llm;

/// <summary>
/// Fitting a local model into the video memory that is really free. Under Windows the CUDA driver's free memory, which
/// llama.cpp's "--fit" plans with, leaves out most of what other programs hold (2026-09-30, beside ComfyUI: 15.2 GB
/// "free" while the card had 4.2): the model was laid on memory that was not there and the server died at the start
/// ("код 1"). The start raises the fit target by that gap, so the model takes on the card what NVML reports free and
/// the rest goes to RAM; when RAM cannot hold the rest either, the lookup says so instead of paging the PC to a halt.
/// </summary>
public static partial class VramFit
{
    /// <summary>Kept free in RAM beside what the model's part there takes (the game, the system).</summary>
    public const int RamMarginMb = 1024;

    /// <summary>What CUDA may overcount without another program behind it (its own rounding, the desktop).</summary>
    public const int OthersMb = 1024;

    /// <summary>The card's message: short of video memory, taken by another program or too small for the model.</summary>
    public static string Short(bool others) => others
        ? "Недостаточно видеопамяти - её заняла другая программа. Закрой её или выбери модель поменьше (Настройки -> ИИ и модели)."
        : "Недостаточно видеопамяти для этой модели - выбери модель поменьше (Настройки -> ИИ и модели).";

    /// <summary>The free MiB of the first CUDA device in <c>llama-server --list-devices</c>; null without one.</summary>
    public static int? CudaFreeMb(string listDevices) =>
        CudaDevice().Match(listDevices) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"CUDA\d+:.*?\(\s*\d+\s*MiB,\s*(\d+)\s*MiB free\)")]
    private static partial Regex CudaDevice();

    /// <summary>
    /// The server's options with "--fit-target" raised by what CUDA counts free that the card has not
    /// (<paramref name="cudaFreeMb"/> - <paramref name="freeMb"/>); unchanged without "--fit-target" or with no gap.
    /// </summary>
    public static IReadOnlyList<string> Adjust(IReadOnlyList<string> args, int cudaFreeMb, int freeMb)
    {
        var i = IndexOfTarget(args);
        var gap = cudaFreeMb - freeMb;
        if (i < 0 || gap <= 0) return args;
        var raised = args.ToArray();
        raised[i + 1] = (Target(args, i) + gap).ToString(CultureInfo.InvariantCulture);
        return raised;
    }

    /// <summary>
    /// The RAM the model's part off the card needs: the weights beyond what fits in <paramref name="freeMb"/> less the
    /// fit target, plus the vision file when it stays on the processor ("--no-mmproj-offload"). 0 without "--fit-target"
    /// (a placement of its own, such as the experts in RAM).
    /// </summary>
    public static long RamNeededMb(IReadOnlyList<string> args, long modelBytes, int freeMb, Func<string, long> fileBytes)
    {
        var i = IndexOfTarget(args);
        if (i < 0) return 0;
        var onCard = Math.Max(0, freeMb - Target(args, i));
        var needed = Math.Max(0, (modelBytes >> 20) - onCard);
        var mmproj = IndexOf(args, "--mmproj");
        if (mmproj >= 0 && mmproj + 1 < args.Count && IndexOf(args, "--no-mmproj-offload") >= 0)
            needed += fileBytes(args[mmproj + 1]) >> 20;
        return needed;
    }

    /// <summary>Whether RAM, less <see cref="RamMarginMb"/>, is short of <paramref name="neededMb"/> (unknown free RAM: -1, never).</summary>
    public static bool RamShort(long neededMb, int freeRamMb) => freeRamMb >= 0 && neededMb > freeRamMb - RamMarginMb;

    /// <summary>A llama-server line saying the card ran out of memory (a buffer or a computation it could not place).</summary>
    public static bool IsOutOfMemory(string line) =>
        line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || line.Contains("unable to allocate CUDA", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed to allocate CUDA", StringComparison.OrdinalIgnoreCase);

    private static int IndexOfTarget(IReadOnlyList<string> args)
    {
        var i = IndexOf(args, "--fit-target");
        return i >= 0 && i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? i : -1;
    }

    private static int Target(IReadOnlyList<string> args, int i) => int.Parse(args[i + 1], CultureInfo.InvariantCulture);

    private static int IndexOf(IReadOnlyList<string> args, string key)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i] == key) return i;
        return -1;
    }
}
