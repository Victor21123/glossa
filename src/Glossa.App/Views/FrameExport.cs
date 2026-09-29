using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // do not lock the shot
        image.UriSource = new Uri(shot);
        image.EndInit();

        // The same ring as in the Dictionary, sized to the screenshot.
        var scale = Math.Max(1, image.PixelWidth / 1200.0);
        var ring = new Rect(box.Left - 6 * scale, box.Top - 4 * scale, box.Width + 12 * scale, box.Height + 8 * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 7 * scale), ring, 5 * scale, 5 * scale);
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 3 * scale), ring, 5 * scale, 5 * scale);
        }
        var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // A new name each time: the previous copy may still be open in the viewer.
        var file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(shot)}-{DateTime.Now:HHmmss}.jpg");
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
        return file;
    }
}
