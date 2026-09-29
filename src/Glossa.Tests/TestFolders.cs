namespace Glossa.Tests;

/// <summary>
/// Scratch folders for one test (xUnit makes a class instance per test), removed when it ends. They live in the
/// user's temp folder, never in Glossa's data folder: a few KB each, gone after the run.
/// </summary>
public sealed class TestFolders : IDisposable
{
    private readonly List<string> _made = [];

    public string New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glossa-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _made.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _made)
        {
            // A file the code under test left open fails the test here, as it should; a short retry only covers a
            // scanner that opened a fresh file for a moment.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
