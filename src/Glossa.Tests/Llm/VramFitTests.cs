using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

public class VramFitTests
{
    // llama-server b11243 --list-devices beside ComfyUI (2026-09-30): nvidia-smi showed 2513 MiB free.
    private const string Devices = "load_backend: loaded CUDA backend from ggml-cuda.dll\nAvailable devices:\n" +
                                   "  CUDA0: NVIDIA GeForce RTX 5060 Ti (16283 MiB, 14155 MiB free)\n";

    private static readonly string[] Placement = ["--fit", "on", "--fit-target", "1536", "--prio", "-1"];

    [Fact]
    public void Reads_what_CUDA_counts_free()
    {
        Assert.Equal(14155, VramFit.CudaFreeMb(Devices));
        Assert.Null(VramFit.CudaFreeMb("Available devices:\n  Vulkan0: AMD Radeon RX 7800 XT (16368 MiB, 15800 MiB free)\n"));
        Assert.Null(VramFit.CudaFreeMb(""));
    }

    [Fact]
    public void The_target_grows_by_what_CUDA_counts_free_and_the_card_has_not()
    {
        var args = VramFit.Adjust(Placement, cudaFreeMb: 14155, freeMb: 2513);
        Assert.Equal(["--fit", "on", "--fit-target", "13178", "--prio", "-1"], args);
        Assert.Equal("1536", Placement[3]); // the caller's options stay as they were: they are the running server's key
    }

    [Fact]
    public void Without_a_gap_or_a_fit_target_the_options_stay()
    {
        Assert.Same(Placement, VramFit.Adjust(Placement, cudaFreeMb: 9000, freeMb: 9000));
        Assert.Same(Placement, VramFit.Adjust(Placement, cudaFreeMb: 9000, freeMb: 9500));
        string[] experts = ["--n-gpu-layers", "99", "--cpu-moe"];
        Assert.Same(experts, VramFit.Adjust(experts, cudaFreeMb: 14155, freeMb: 2513));
    }

    [Fact]
    public void RAM_takes_the_weights_the_card_cannot_hold_and_the_vision_on_the_processor()
    {
        const long model = 12_500L << 20;
        // 26B beside ComfyUI: 2513 MiB free, 1536 kept -> ~1 GB on the card, the rest and the 1.1 GB vision in RAM.
        string[] seeing = [.. Placement, "--mmproj", "v.gguf", "--no-mmproj-offload"];
        Assert.Equal(12_500 - 977 + 1_100, VramFit.RamNeededMb(seeing, model, 2513, _ => 1_100L << 20));
        // The vision on the card is not RAM's.
        string[] onCard = [.. Placement, "--mmproj", "v.gguf"];
        Assert.Equal(12_500 - 977, VramFit.RamNeededMb(onCard, model, 2513, _ => 1_100L << 20));
        // The whole model on a free card: nothing in RAM.
        Assert.Equal(0, VramFit.RamNeededMb(Placement, model, 15_000, _ => 0));
        // Experts in RAM by choice: not this check's business.
        Assert.Equal(0, VramFit.RamNeededMb(["--n-gpu-layers", "99", "--cpu-moe"], model, 2513, _ => 0));
    }

    [Theory]
    [InlineData(10_000, 11_700, false)]
    [InlineData(10_676, 11_700, false)]
    [InlineData(10_677, 11_700, true)]
    [InlineData(10_000, -1, false)]
    public void RAM_is_short_below_the_margin(long neededMb, int freeRamMb, bool shortOfIt) =>
        Assert.Equal(shortOfIt, VramFit.RamShort(neededMb, freeRamMb));

    [Theory]
    [InlineData("E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 11736.39 MiB on device 0: cudaMalloc failed: out of memory", true)]
    [InlineData("E llama_model_load: error loading model: unable to allocate CUDA0 buffer", true)]
    [InlineData("E alloc_tensor_range: failed to allocate CUDA0 buffer of size 12306496768", true)]
    [InlineData("E srv    load_model: failed to load model, 'D:\\models\\llm\\gemma.gguf'", false)]
    [InlineData("E couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 18091", false)]
    public void Knows_a_card_out_of_memory(string line, bool oom) => Assert.Equal(oom, VramFit.IsOutOfMemory(line));

    [Fact]
    public void Names_the_other_program_only_when_one_holds_the_card()
    {
        Assert.Contains("другая программа", VramFit.Short(others: true));
        Assert.DoesNotContain("другая программа", VramFit.Short(others: false));
        Assert.StartsWith("Недостаточно видеопамяти", VramFit.Short(others: false));
    }
}
