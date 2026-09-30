using Glossa.Core.Ocr;

namespace Glossa.Tests.Ocr;

public class ReadingMemoryTests
{
    [Fact]
    public void Gives_back_what_was_kept()
    {
        var memory = new ReadingMemory();
        memory.Set("a", "one");
        Assert.True(memory.TryGet("a", out var value));
        Assert.Equal("one", value);
        Assert.False(memory.TryGet("b", out _));
    }

    [Fact]
    public void Starts_over_at_its_limit()
    {
        var memory = new ReadingMemory(limit: 3);
        foreach (var k in new[] { "a", "b", "c", "d" }) memory.Set(k, k);
        Assert.False(memory.TryGet("a", out _));
        Assert.True(memory.TryGet("d", out _));
    }

    [Fact]
    public void Keys_join_their_parts_so_different_points_differ()
    {
        Assert.NotEqual(ReadingMemory.Key("eyes", "png", 220, 90), ReadingMemory.Key("eyes", "png", 280, 90));
        Assert.Equal(ReadingMemory.Key("eyes", "png", 220.4, 90), ReadingMemory.Key("eyes", "png", 220.4, 90));
    }

    [Fact]
    public async Task Is_safe_from_many_lookups_at_once()
    {
        var memory = new ReadingMemory(limit: 50);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                memory.Set($"{t}-{i}", "x");
                memory.TryGet($"{t}-{i - 1}", out _);
            }
        })));
    }
}
