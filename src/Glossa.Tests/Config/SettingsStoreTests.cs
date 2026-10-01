using Glossa.Core.Config;

namespace Glossa.Tests.Config;

public class SettingsStoreTests
{
    [Fact]
    public void Writers_on_several_threads_never_collide_on_the_temp_file()
    {
        using var folders = new TestFolders();
        var store = new SettingsStore(Path.Combine(folders.New(), "settings.json"));
        var settings = new AppSettings();

        // The settings window, the tray, the hotkeys and the update check all save: one temp file, so one writer at a time.
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 200, i =>
        {
            try
            {
                store.Save(settings);
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        });

        Assert.Empty(errors);
        Assert.Equal("Alt+Q", store.Load().Hotkey);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }
}
