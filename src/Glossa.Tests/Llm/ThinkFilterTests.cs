using Glossa.Core.Llm;

namespace Glossa.Tests.Llm;

public class ThinkFilterTests
{
    private static string Run(params string[] chunks)
    {
        var filter = new ThinkFilter();
        return string.Concat(chunks.Select(filter.Feed)) + filter.Flush();
    }

    [Fact]
    public void A_thought_that_opens_and_closes_in_one_chunk_does_not_swallow_the_answer()
    {
        Assert.Equal("{\"a\":1}", Run("<think>plan it</think>{\"a\":1}"));
        Assert.Equal("{\"a\":1}", Run("<think>plan</think>{\"a\"", ":1}"));
    }

    [Fact]
    public void Tags_split_between_chunks_are_still_found()
    {
        Assert.Equal("ok", Run("<thi", "nk>x</th", "ink>ok"));
        Assert.Equal("ok", Run("<", "think", ">x<", "/think>", "o", "k"));
    }

    [Fact]
    public void Thinking_tags_and_the_harmony_analysis_channel_are_dropped()
    {
        Assert.Equal("answer", Run("<thinking>hm</thinking>answer"));
        Assert.Equal("answer", Run("<|channel|>analysis<|message|>reasoning here<|end|>",
            "<|start|>assistant<|channel|>final<|message|>answer"));
    }

    [Fact]
    public void Plain_text_with_angle_brackets_passes_through()
    {
        Assert.Equal("a < b and <b>bold</b>", Run("a <", " b and <b>bo", "ld</b>"));
        Assert.Equal("ends with <", Run("ends with <"));
    }

    [Fact]
    public void An_unclosed_thought_is_dropped_but_text_before_it_stays()
    {
        Assert.Equal("before ", Run("before <think>never closed"));
    }
}
