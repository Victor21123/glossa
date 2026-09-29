using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Lookup;
using Xunit;

namespace Glossa.Tests.Llm;

public class ModelLifetimeTests
{
    private static AppSettings Settings(int idle = 15, int gameIdle = 3, bool yield = true)
    {
        var s = new AppSettings();
        s.LocalAi.IdleUnloadMinutes = idle;
        s.Performance.GameIdleUnloadMinutes = gameIdle;
        s.Performance.YieldVram = yield;
        return s;
    }

    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(1);

    [Fact]
    public void KeepsAModelInUseRecently()
    {
        Assert.Null(ModelLifetime.UnloadReason(Settings(), inGame: false, TimeSpan.FromMinutes(5), Settled, freeVramMb: 3000, 3000));
    }

    [Fact]
    public void UnloadsAfterTheIdleTime()
    {
        Assert.NotNull(ModelLifetime.UnloadReason(Settings(), inGame: false, TimeSpan.FromMinutes(15), Settled, freeVramMb: 3000, 3000));
    }

    [Fact]
    public void InAGameTheShorterTimeApplies()
    {
        var s = Settings();
        Assert.Null(ModelLifetime.UnloadReason(s, inGame: false, TimeSpan.FromMinutes(4), Settled, 3000, 3000));
        Assert.Contains("in a game", ModelLifetime.UnloadReason(s, inGame: true, TimeSpan.FromMinutes(4), Settled, 3000, 3000));
    }

    [Fact]
    public void GameTimeZeroMeansAsOutsideGames()
    {
        Assert.Null(ModelLifetime.UnloadReason(Settings(gameIdle: 0), inGame: true, TimeSpan.FromMinutes(4), Settled, 3000, 3000));
    }

    [Fact]
    public void IdleZeroNeverUnloadsForIdleness()
    {
        Assert.Null(ModelLifetime.UnloadReason(Settings(idle: 0, gameIdle: 0), inGame: false, TimeSpan.FromHours(5), Settled, 3000, 3000));
    }

    [Fact]
    public void GivesVideoMemoryToAGameThatRunsOut()
    {
        // The model left 1.5 GB; a game then took most of it.
        Assert.Contains("video memory", ModelLifetime.UnloadReason(Settings(), inGame: true, TimeSpan.FromSeconds(10), Settled, freeVramMb: 250,
            freeAfterLoadMb: 1500));
    }

    [Fact]
    public void DoesNotMistakeItsOwnLoadForAGame()
    {
        // Right after the load the model itself filled the memory up to the reserve.
        Assert.Null(ModelLifetime.UnloadReason(Settings(), inGame: true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 250, 1500));
        // Settled, but the model itself left only 100 MB (it happens): nobody else took anything.
        Assert.Null(ModelLifetime.UnloadReason(Settings(), inGame: false, TimeSpan.FromSeconds(40), Settled, freeVramMb: 100, freeAfterLoadMb: 110));
    }

    [Fact]
    public void YieldingCanBeTurnedOffAndUnknownMemoryIsIgnored()
    {
        Assert.Null(ModelLifetime.UnloadReason(Settings(yield: false), inGame: true, TimeSpan.FromSeconds(10), Settled, 250, 1500));
        Assert.Null(ModelLifetime.UnloadReason(Settings(), inGame: true, TimeSpan.FromSeconds(10), Settled, freeVramMb: -1, 1500));
        Assert.Null(ModelLifetime.UnloadReason(Settings(), inGame: true, TimeSpan.FromSeconds(10), Settled, freeVramMb: 250, -1));
    }
}

public class CardCacheTests
{
    private static WordCard Card(string word) => new() { Language = "en", Word = word, Translation = word + "-ru" };

    [Fact]
    public void ReturnsWhatWasPut()
    {
        var cache = new CardCache(3);
        cache.Put("a", Card("a"));
        Assert.Equal("a-ru", cache.Get("a")?.Translation);
        Assert.Null(cache.Get("b"));
    }

    [Fact]
    public void DropsTheLeastRecentlyUsed()
    {
        var cache = new CardCache(2);
        cache.Put("a", Card("a"));
        cache.Put("b", Card("b"));
        Assert.NotNull(cache.Get("a")); // a is now the most recent
        cache.Put("c", Card("c"));
        Assert.Null(cache.Get("b"));
        Assert.NotNull(cache.Get("a"));
        Assert.NotNull(cache.Get("c"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ReplacingAKeyKeepsOneEntry()
    {
        var cache = new CardCache(2);
        cache.Put("a", Card("a"));
        cache.Put("a", Card("a2"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("a2-ru", cache.Get("a")?.Translation);
    }
}
