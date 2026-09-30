using System.Text.Json;
using Glossa.Core.Config;
using Glossa.Core.Llm;

namespace Glossa.Tests.Config;

public class EyesSettingsTests
{
    [Fact]
    public void By_default_the_eyes_read_on_the_processor_and_leave_after_15_minutes()
    {
        var eyes = new EyesSettings();
        Assert.Equal("cpu", eyes.Device);
        Assert.Equal(15, eyes.IdleUnloadMinutes);
    }

    [Fact]
    public void Settings_from_before_the_eyes_get_the_defaults()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{"LocalAi": {"Profile": "gemma12b"}}""")!;
        Assert.Equal("cpu", s.Eyes.Device);
        Assert.Equal(15, s.Eyes.IdleUnloadMinutes);
    }

    [Fact]
    public void An_unknown_device_becomes_the_processor()
    {
        var eyes = new EyesSettings { Device = "npu", IdleUnloadMinutes = -3 };
        eyes.Normalize();
        Assert.Equal("cpu", eyes.Device);
        Assert.Equal(0, eyes.IdleUnloadMinutes);
    }

    [Fact]
    public void The_eyes_lie_in_the_models_folder_and_have_a_port_of_their_own()
    {
        var ai = new LocalAiSettings { ModelsFolder = @"D:\m", BasePort = 18091 };
        Assert.Equal(Path.Combine(@"D:\m", ModelCatalog.Eyes.File), ai.EyesModel());
        Assert.Equal(Path.Combine(@"D:\m", ModelCatalog.Eyes.Vision!.LocalName), ai.EyesVisionFile());
        Assert.Equal(18092, ai.EyesPort);
    }

    [Fact]
    public void Only_models_that_misread_stylized_text_use_the_eyes()
    {
        var dir = Directory.CreateTempSubdirectory("glossa-eyes-").FullName;
        try
        {
            var ai = new LocalAiSettings { ModelsFolder = dir };
            File.WriteAllText(ai.EyesModel(), "");
            File.WriteAllText(ai.EyesVisionFile(), "");
            var on = new EyesSettings();
            foreach (var (profile, uses) in new[] { ("gemma26b", false), ("gemma12b", true), ("light", true), ("custom", true) })
            {
                ai.Profile = profile;
                Assert.Equal(uses, ai.UsesEyes(on));
            }
            ai.Profile = "gemma12b";
            Assert.False(ai.UsesEyes(new EyesSettings { Device = "off" }));
            File.Delete(ai.EyesVisionFile()); // half a download is no eyes
            Assert.False(ai.UsesEyes(on));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_eyes_are_pinned_to_a_revision_with_both_files_and_are_no_profile()
    {
        var eyes = ModelCatalog.Eyes;
        Assert.Equal("Qwen/Qwen3-VL-4B-Instruct-GGUF", eyes.Repo);
        Assert.Equal(40, eyes.Revision.Length);
        Assert.Equal(64, eyes.Sha256.Length);
        Assert.NotNull(eyes.Vision);
        Assert.Equal(64, eyes.Vision!.Sha256.Length);
        Assert.DoesNotContain(ModelCatalog.Items, e => e.Profile == ModelCatalog.EyesKey);
        Assert.Null(ModelCatalog.For(ModelCatalog.EyesKey));
    }
}
