using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

public class EyesPolicyTests
{
    [Fact]
    public void On_the_processor_the_video_card_is_left_alone()
    {
        var args = EyesPolicy.Placement("cpu", "m.gguf", lowPriority: false);
        Assert.Equal(["--mmproj", "m.gguf", "--cache-ram", "0", "-ngl", "0", "--no-mmproj-offload", "--no-op-offload", "--device", "none"], args);
    }

    [Fact]
    public void On_the_video_card_everything_goes_there()
    {
        var args = EyesPolicy.Placement("gpu", "m.gguf", lowPriority: false);
        Assert.Equal(["--mmproj", "m.gguf", "--cache-ram", "0", "-ngl", "99"], args);
    }

    [Fact]
    public void Low_priority_is_passed_on()
    {
        Assert.Equal(["--prio", "-1"], EyesPolicy.Placement("cpu", "m.gguf", lowPriority: true).TakeLast(2));
        Assert.Equal(["--prio", "-1"], EyesPolicy.Placement("gpu", "m.gguf", lowPriority: true).TakeLast(2));
    }

    [Theory]
    [InlineData(8000, true)]
    [InlineData(6144, true)]
    [InlineData(6000, false)]
    [InlineData(-1, false)]
    public void The_eyes_come_up_with_the_model_only_with_room_in_memory(int freeRamMb, bool warm) =>
        Assert.Equal(warm, EyesPolicy.ShouldWarm(freeRamMb));

    [Theory]
    [InlineData("gpu", 3000, true)]
    [InlineData("gpu", 4711, true)]
    [InlineData("gpu", 4712, false)]
    [InlineData("gpu", -1, false)]
    [InlineData("cpu", 1000, false)]
    public void Short_video_memory_is_reported_only_for_the_video_card(string device, int freeVramMb, bool warn) =>
        Assert.Equal(warn, EyesPolicy.VramShort(device, freeVramMb));

    [Fact]
    public void Zero_minutes_means_never_unload()
    {
        Assert.False(EyesPolicy.ShouldUnload(0, TimeSpan.FromHours(5)));
        Assert.False(EyesPolicy.ShouldUnload(15, TimeSpan.FromMinutes(14)));
        Assert.True(EyesPolicy.ShouldUnload(15, TimeSpan.FromMinutes(15)));
    }
}
