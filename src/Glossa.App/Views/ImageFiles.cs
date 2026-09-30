using System.IO;
using System.Windows.Media.Imaging;

namespace Glossa.App.Views;

/// <summary>Pictures for the screen that never hold their file: it can be replaced or deleted while shown.</summary>
public static class ImageFiles
{
    /// <summary>The picture in the file, or null when it is missing or not a picture.</summary>
    public static BitmapImage? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (Unreadable(e))
        {
            return null;
        }
    }

    /// <summary>What WPF's decoders throw for a file that is locked, gone or not quite a picture.</summary>
    private static bool Unreadable(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException
            or InvalidOperationException or OverflowException or System.Runtime.InteropServices.ExternalException;

    /// <summary>A picture from bytes (a thumbnail from the internet), decoded no wider than <paramref name="width"/>.</summary>
    public static BitmapImage? Decode(byte[]? bytes, int width = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            if (width > 0) img.DecodePixelWidth = width;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (Unreadable(e))
        {
            return null;
        }
    }
}
