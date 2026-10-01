using System.Text.Json;
using Glossa.Core.Config;

namespace Glossa.Tests.Config;

public class MeaningPicturesSettingsTests
{
    [Fact]
    public void Meaning_pictures_are_on_by_default_and_in_old_settings()
    {
        Assert.True(new AppSettings().MeaningPictures);

        var path = Path.Combine(Path.GetTempPath(), "glossa-mp-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"Hotkey": "Alt+W", "Study": {"BackPicture": true}}""");
            var loaded = new SettingsStore(path).Load();
            Assert.Equal("Alt+W", loaded.Hotkey);
            Assert.True(loaded.MeaningPictures);

            loaded.MeaningPictures = false;
            var store = new SettingsStore(path);
            store.Save(loaded);
            Assert.False(store.Load().MeaningPictures);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void Meaning_pictures_off_turn_off_the_anki_field_and_the_study_back()
    {
        var s = new AppSettings();
        Assert.True(s.AnkiMeaningPictures());
        Assert.True(s.StudyBackPicture());

        s.MeaningPictures = false;
        Assert.False(s.AnkiMeaningPictures());
        Assert.False(s.StudyBackPicture());

        // The finer checkboxes keep their own values, so switching back restores them.
        s.MeaningPictures = true;
        s.Anki.IncludeMeaningPictures = false;
        Assert.False(s.AnkiMeaningPictures());
        Assert.True(s.StudyBackPicture());
        s.Study.BackPicture = false;
        Assert.False(s.StudyBackPicture());
    }

    [Fact]
    public void The_helper_methods_are_not_written_into_settings_json()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        Assert.Contains("\"MeaningPictures\":true", json);
        Assert.DoesNotContain("AnkiMeaningPictures", json);
        Assert.DoesNotContain("StudyBackPicture", json);
    }
}
