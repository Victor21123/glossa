using Glossa.Core.Ocr;
using SkiaSharp;

namespace Glossa.Core.Export;

/// <summary>How an export went: frames written, frames that could not be read, and whether the user stopped it.</summary>
public sealed record FrameWriteResult(int Written, int Skipped, bool Cancelled);

/// <summary>
/// A scene frame with the word outlined: white on a dark halo, as on the still frame, so it shows on bright and dark
/// scenes alike. Skia only, so it can run off the UI thread.
/// </summary>
public static class FrameOutline
{
    /// <summary>JPEG quality of the copies (the stored frames are 85).</summary>
    public const int Quality = 92;

    /// <summary>Longest side of a frame that is decoded; a damaged header must not make the app allocate gigabytes.</summary>
    public const int MaxSide = 16384;

    /// <summary>Most pixels of a frame that is decoded (64 Mpx; a 4K screen is 8.3).</summary>
    public const int MaxPixels = 64_000_000;

    /// <summary>Throws <see cref="InvalidDataException"/> for sizes that are empty or beyond the limits.</summary>
    public static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new InvalidDataException($"Frame size {width}x{height} is not accepted");
    }

    /// <summary>
    /// The frame as a new JPEG with the word's ring, at the frame's own size (the ring grows with frames wider than
    /// 1200 px). It is always re-encoded, so nothing hidden in the stored file (metadata) travels with the copy.
    /// Throws <see cref="InvalidDataException"/> for a file that is not a plain JPEG or is too big.
    /// </summary>
    public static byte[] Jpeg(string path, PixelRect? box)
    {
        if (!FrameFiles.IsUsable(path)) throw new InvalidDataException("Not a JPEG frame: " + path);
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Not an image: " + path);
        CheckSize(codec.Info.Width, codec.Info.Height);
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Cannot decode: " + path);
        if (box is { } b)
        {
            var scale = Math.Max(1, bitmap.Width / 1200.0);
            var ring = new SKRect((float)(b.Left - 6 * scale), (float)(b.Top - 4 * scale), (float)(b.Right + 6 * scale), (float)(b.Bottom + 4 * scale));
            var radius = (float)(5 * scale);
            using var canvas = new SKCanvas(bitmap);
            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(7 * scale), Color = new SKColor(0, 0, 0, 150) };
            using var white = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(3 * scale), Color = SKColors.White };
            canvas.DrawRoundRect(ring, radius, radius, halo);
            canvas.DrawRoundRect(ring, radius, radius, white);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data.ToArray();
    }

    /// <summary>
    /// Writes the jobs into <paramref name="folder"/> (made if missing). A frame that cannot be read (any reason but
    /// cancelling) is counted as skipped and reported to <paramref name="onSkip"/>; the rest go on. A file already in
    /// the folder is never overwritten, the copy gets " (2)", " (3)". Write errors (disk full, no access) are thrown,
    /// with the half-written file removed. Cancelling returns what was written, flagged.
    /// </summary>
    public static FrameWriteResult WriteAll(IReadOnlyList<FrameJob> jobs, string folder, IProgress<(int Done, int Total)>? progress,
        CancellationToken ct, Action<FrameJob, Exception>? onSkip = null)
    {
        Directory.CreateDirectory(folder);
        int written = 0, skipped = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            if (ct.IsCancellationRequested) return new FrameWriteResult(written, skipped, true);
            byte[]? bytes = null;
            try
            {
                bytes = Jpeg(jobs[i].Source, jobs[i].Box);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                onSkip?.Invoke(jobs[i], ex);
            }
            if (bytes is not null)
            {
                WriteNew(folder, jobs[i].Name, bytes);
                written++;
            }
            progress?.Report((i + 1, jobs.Count));
        }
        return new FrameWriteResult(written, skipped, false);
    }

    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <summary>Only "the file is already there" means the name is taken; a full disk or a pulled drive is a real failure.</summary>
    internal static bool IsNameTaken(IOException ex) => ex.HResult is ErrorFileExists or ErrorAlreadyExists;

    /// <summary>
    /// Creates the file under a free name (CreateNew is the guard: even a file that appears between the check and the
    /// write is not overwritten) and writes the bytes; a failed write removes the partial file and throws.
    /// Returns the name used. <paramref name="write"/> is a seam for tests.
    /// </summary>
    internal static string WriteNew(string folder, string name, byte[] bytes, Action<Stream, byte[]>? write = null)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var candidate = FrameNames.Unique(name, n => taken.Contains(n) || File.Exists(Path.Combine(folder, n)));
            var path = Path.Combine(folder, candidate);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (IsNameTaken(ex))
            {
                taken.Add(candidate); // taken a moment ago: the next free name
                continue;
            }

            try
            {
                using (stream)
                {
                    if (write is null) stream.Write(bytes);
                    else write(stream, bytes);
                    stream.Flush(flushToDisk: true);
                }
                return candidate;
            }
            catch
            {
                try { File.Delete(path); }
                catch (Exception) { } // best effort: the original error is the one to report
                throw;
            }
        }
    }
}
