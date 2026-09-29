using Glossa.Core.Config;
using Glossa.Core.Llm;

namespace Glossa.Tests.Config;

public class LocalAiSettingsTests
{
    [Fact]
    public void A_file_set_by_hand_wins_over_the_catalog()
    {
        var ai = new LocalAiSettings { Gemma26bModel = "a.gguf", Gemma12bModel = "b.gguf", LightModel = "c.gguf", CustomModel = "d.gguf" };
        Assert.Equal("a.gguf", ai.SingleModel("gemma26b"));
        Assert.Equal("b.gguf", ai.SingleModel("gemma12b"));
        Assert.Equal("c.gguf", ai.SingleModel("light"));
        Assert.Equal("d.gguf", ai.SingleModel("custom"));
        Assert.Null(ai.SingleModel("qwen9b-hymt")); // retired
    }

    [Fact]
    public void Without_a_file_the_catalog_file_is_looked_for_in_the_models_folder()
    {
        var ai = new LocalAiSettings { ModelsFolder = @"D:\m" };
        Assert.Equal(Path.Combine(@"D:\m", ModelCatalog.For("light")!.File), ai.SingleModel("light"));
        Assert.Equal("", ai.SingleModel("custom")); // no catalog file for the user's own model
    }

    [Fact]
    public void Models_folder_follows_the_main_model_when_not_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glossa-test-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ai = new LocalAiSettings { Gemma26bModel = Path.Combine(dir, "x.gguf") };
            Assert.Equal(dir, ai.ModelsFolderResolved());
            Assert.Equal(Path.Combine(dir, ModelCatalog.For("light")!.File), ai.SingleModel("light"));
            Assert.False(ai.HasModel("light"));
            File.WriteAllText(ai.SingleModel("light")!, "");
            Assert.True(ai.HasModel("light"));
            Assert.False(ai.HasModel("custom")); // no file chosen
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retired_profiles_and_tiers_fall_back_to_the_default_model()
    {
        var ai = new LocalAiSettings { Profile = "qwen9b-hymt", Mode = "quality" };
        ai.Normalize();
        Assert.Equal("gemma26b", ai.Profile);
        Assert.Equal("auto", ai.Mode);
        var light = new LocalAiSettings { Profile = "japanese", Mode = "light" };
        light.Normalize();
        Assert.Equal(("gemma26b", "lowvram"), (light.Profile, light.Mode));
    }

    [Fact]
    public void Catalog_pins_each_model_to_a_revision_and_checksum()
    {
        foreach (var e in ModelCatalog.Items)
        {
            Assert.Matches("^[0-9a-f]{40}$", e.Revision);
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.True(e.Size > 1_000_000_000);
            Assert.StartsWith($"https://huggingface.co/{e.Repo}/resolve/{e.Revision}/", e.Url);
        }
        Assert.Equal(["gemma26b", "gemma12b", "light"], ModelCatalog.Items.Select(e => e.Profile));
    }
}
