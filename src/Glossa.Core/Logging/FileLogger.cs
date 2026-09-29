using System.Text;

namespace Glossa.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Daily log file (glossa-yyyyMMdd.log); keeps two weeks.</summary>
public sealed class FileLogger : ILog
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileLogger(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"glossa-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8); }
            catch (IOException) { /* logging must never take the app down */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "glossa-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
    }
}
