namespace Glossa.Core.Export;

/// <summary>
/// Which files a "frame" may be. The path in a library row is data, not a promise: a damaged or tampered row such as
/// ShotFile="library.db" must never get its target copied out or opened, so every read of a frame goes through here.
/// </summary>
public static class FrameFiles
{
    /// <summary>Where the words' frames live, relative to the data folder.</summary>
    public const string ShotsFolder = "shots";

    /// <summary>
    /// The full path of a frame file when <paramref name="file"/> (relative to the data folder, or already full) is a
    /// .jpg / .jpeg inside <c>&lt;data&gt;\shots\</c> and has no stream suffix; null otherwise. Does not touch the disk.
    /// </summary>
    public static string? Resolve(string dataRoot, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(dataRoot, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var shots = Path.Combine(Path.GetFullPath(dataRoot), ShotsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(shots, StringComparison.OrdinalIgnoreCase)) return null;
        if (full.IndexOf(':', 2) >= 0) return null; // "a.jpg:secret" is an alternate data stream, not a file
        var ext = Path.GetExtension(full);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The file exists, is a real file (not a link to somewhere else) and starts with the JPEG signature FF D8 FF.</summary>
    public static bool IsUsable(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null) return false;
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[3];
            return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
