using System.IO;
using Glossa.Core.Export;
using Glossa.Core.Ocr;

namespace Glossa.App.Views;

/// <summary>
/// «Открыть кадр»: the saved scene stays as it was captured, so the viewer gets a copy with the word outlined — white
/// on a dark halo, as on the still frame, so it shows on bright and dark scenes alike.
/// </summary>
public static class FrameExport
{
    public static string Outlined(string shot, PixelRect box, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in new DirectoryInfo(folder).GetFiles("*.jpg").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
        {
            try { old.Delete(); }
            catch (IOException) { } // still open in a viewer
        }

        // One ring rule for the viewer copy and the exported frames.
        var jpeg = FrameOutline.Jpeg(shot, box);

        // A new name each time: the previous copy may still be open in the viewer.
        var file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(shot)}-{DateTime.Now:HHmmss}.jpg");
        File.WriteAllBytes(file, jpeg);
        return file;
    }
}
