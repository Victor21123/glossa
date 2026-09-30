namespace Glossa.Core.Llm;

/// <summary>
/// The rules for the eyes (<see cref="ModelCatalog.Eyes"/>) measured on 2026-09-30 for the reference mid PC (i5-11400,
/// RTX 3060 12 GB, 16 GB of RAM), in <c>docs/research/ocr.md</c>: where they run, when they come up with the main
/// model, when video memory is too short for them and when they leave memory.
/// </summary>
public static class EyesPolicy
{
    /// <summary>Their context: a 1200x360 picture, the prompt and at most 600 tokens of lines.</summary>
    public const int ContextSize = 2048;

    /// <summary>Free RAM after the main model loaded that lets the eyes (4.1 GB on the processor) come up with it.</summary>
    public const int WarmRamMb = 6144;

    /// <summary>What the eyes take on the video card (4.2 GB measured) and the margin kept above it.</summary>
    public const int NeedVramMb = 4200, VramMarginMb = 512;

    public static bool ShouldWarm(int freeRamMb) => freeRamMb >= WarmRamMb;

    /// <summary>The video card was chosen and the driver reports less free memory than the eyes need (unknown: -1, no warning).</summary>
    public static bool VramShort(string device, int freeVramMb) =>
        device == "gpu" && freeVramMb >= 0 && freeVramMb < NeedVramMb + VramMarginMb;

    /// <summary>Whether the eyes, idle this long, leave memory; 0 minutes - never.</summary>
    public static bool ShouldUnload(int idleMinutes, TimeSpan idle) => idleMinutes > 0 && idle >= TimeSpan.FromMinutes(idleMinutes);

    /// <summary>
    /// The eyes' own llama-server options beside the common ones. On the processor the CUDA build is kept off the card
    /// altogether: "-ngl 0" alone still sends prompt batches there (op offload: +430 MB of video memory, and other
    /// numbers that change borderline readings), "--device none" does not (same speed, +6 MB).
    /// </summary>
    public static IReadOnlyList<string> Placement(string device, string mmproj, bool lowPriority)
    {
        List<string> args = ["--mmproj", mmproj, "--cache-ram", "0"];
        args.AddRange(device == "gpu" ? ["-ngl", "99"] : ["-ngl", "0", "--no-mmproj-offload", "--no-op-offload", "--device", "none"]);
        if (lowPriority) args.AddRange(["--prio", "-1"]);
        return args;
    }
}
