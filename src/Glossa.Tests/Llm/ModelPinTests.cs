using Glossa.Core.Config;
using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

/// <summary>"Загрузить в фон" (user, 2026-10-03): a model loaded by hand stays through idle, but still yields to a game.</summary>
public sealed class ModelPinTests
{
    [Fact]
    public void A_pinned_model_is_not_unloaded_for_idle_but_still_for_a_game_short_of_video_memory()
    {
        var s = new AppSettings();
        var idle = TimeSpan.FromHours(3);
        var settled = ModelLifetime.Settle + TimeSpan.FromMinutes(1);

        Assert.NotNull(ModelLifetime.UnloadReason(s, inGame: false, idle, settled, -1, -1));
        Assert.Null(ModelLifetime.UnloadReason(s, inGame: false, idle, settled, -1, -1, pinned: true));
        Assert.Null(ModelLifetime.UnloadReason(s, inGame: true, idle, settled, -1, -1, pinned: true));

        var reason = ModelLifetime.UnloadReason(s, inGame: true, TimeSpan.Zero, settled, freeVramMb: 100, freeAfterLoadMb: 3000,
            pinned: true);
        Assert.NotNull(reason);
        Assert.Contains("video memory", reason);
    }
}
