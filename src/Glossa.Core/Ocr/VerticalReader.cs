using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOcrNet;
using SkiaSharp;

namespace Glossa.Core.Ocr;

/// <summary>A column of vertical text as read by <see cref="VerticalReader"/>: its characters top to bottom, each with its box.</summary>
public sealed record VerticalRead(string Text, IReadOnlyList<OcrWord> Words, float Score)
{
    public VerticalRead Offset(double dx, double dy) =>
        this with { Words = Words.Select(w => w with { Box = w.Box.Offset(dx, dy) }).ToList() };
}

/// <summary>One character of a CTC decoding: the timesteps it covers (first and last) and its mean probability.</summary>
internal readonly record struct CtcChar(string Text, int First, int Last, float Score);

/// <summary>
/// Reads one column of vertical Japanese with the recognizer of the lookup (PP-OCRv6 medium). RapidOcrNet rotates a
/// tall box clockwise, which turns a column into garbage; here the crop is rotated counter-clockwise (the column top
/// goes to the left, as in PaddleOCR) and read as a line: 0.90 mean character accuracy on the synthetic frames and a
/// real screenshot against 0.03 (tategaki experiment, 2026-09-30). The session is the engine's own second one, created
/// on the first column and disposed with the engine's idle trim, so horizontal-only users do not pay its memory.
/// </summary>
public sealed class VerticalReader : IDisposable
{
    private const int Height = 48;
    private const int MinWidth = 320;
    private const int Margin = 4;

    /// <summary>The widest model input PaddleOCR feeds the recognizer (3200 px at height 48); a longer column is read in chunks.</summary>
    internal const int MaxInputWidth = 3200;

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string[] _chars;

    /// <param name="modelPath">The recognizer the lookup reads with.</param>
    /// <param name="dictPath">Its character list, one per line (CRLF or LF); the blank and the space are not in it.</param>
    public VerticalReader(string modelPath, string dictPath, int threads, OcrMemory memory)
    {
        _chars = ReadDictionary(dictPath); // first: a missing list must not leave a session behind
        using var so = RapidOcr.GetDefaultSessionOptions(threads);
        // Same options as the library's sessions: no spinning workers burning cores a running game needs.
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        // The input width differs per column: a memory plan per width would only pile up.
        so.EnableMemoryPattern = false;
        if (memory == OcrMemory.None) so.EnableCpuMemArena = false;
        _session = new InferenceSession(modelPath, so);
        _input = _session.InputNames[0];
    }

    /// <summary>The character list, one per line (CRLF or LF); a blank line is an entry that keeps the numbering.</summary>
    internal static string[] ReadDictionary(string path) => File.ReadAllLines(path);

    /// <summary>
    /// The pieces a column of <paramref name="length"/> pixels and <paramref name="width"/> is read in: each no longer than
    /// what the model input takes (<see cref="MaxInputWidth"/> at height 48), equal, from the top.
    /// </summary>
    internal static List<(double From, double To)> Chunks(double length, double width)
    {
        var longest = MaxInputWidth * Math.Max(width, 1) / Height;
        var n = Math.Max(1, (int)Math.Ceiling(length / longest));
        return Enumerable.Range(0, n).Select(i => (length * i / n, length * (i + 1) / n)).ToList();
    }

    /// <summary>Reads the upright column inside <paramref name="box"/> (bitmap pixels); see <see cref="Read(SKBitmap, ColumnQuad)"/>.</summary>
    public VerticalRead? Read(SKBitmap bitmap, PixelRect box, CancellationToken ct = default) => Read(bitmap, ColumnQuad.FromRect(box), ct);

    /// <summary>
    /// Reads the column inside <paramref name="column"/> (bitmap pixels): the characters top to bottom, each with a box as
    /// wide as the column and as tall as its share of the timesteps, following the column's lean. Null when there is
    /// nothing to read. The column is sampled along its quadrilateral (a column leaning 4-6 degrees reads 0.87 that way
    /// against 0.5-0.75 cropped as a rectangle), laid on its side with the top to the left. (RapidOcrNet's own crop,
    /// <c>OcrUtils.GetRotateCropImage</c>, is internal to the library: this is the same affine map for a rectangle.)
    /// </summary>
    public VerticalRead? Read(SKBitmap bitmap, ColumnQuad column, CancellationToken ct = default)
    {
        if (column.Length < 1 || column.Width < 1) return null;
        var chunks = Chunks(column.Length, column.Width);
        if (chunks.Count == 1) return ReadOne(bitmap, column, ct);
        // a column longer than the model takes is read piecewise and joined
        var reads = chunks.Select(c => ReadOne(bitmap, column.Part(c.From, c.To), ct)).Where(r => r is not null).Select(r => r!).ToList();
        if (reads.Count == 0) return null;
        var words = reads.SelectMany(r => r.Words).ToList();
        return new VerticalRead(string.Concat(reads.Select(r => r.Text)), words, words.Average(w => w.Score));
    }

    private VerticalRead? ReadOne(SKBitmap bitmap, ColumnQuad column, CancellationToken ct)
    {
        if (column.Length < 1 || column.Width < 1) return null;
        using var converted = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = converted ?? bitmap;
        var (grown, topMargin, bottomMargin) = column.GrowWithin(Margin, source.Width, source.Height);
        if (grown.Length < 1 || grown.Width < 1) return null;

        // The strip is as long as the grown column and Height pixels high: it is read at that height, so its width in input
        // pixels is the column's length scaled by Height / its width.
        var rw = Math.Max(1, (int)Math.Ceiling(Height * grown.Length / grown.Width));
        var inputW = Math.Max(MinWidth, rw);
        var tensor = new DenseTensor<float>([1, 3, Height, inputW]);
        Fill(tensor.Buffer.Span, source.GetPixelSpan(), source.RowBytes, source.Width, source.Height, grown, rw, inputW);

        // a long read stops when the lookup is cancelled
        using var run = new RunOptions();
        using var stop = ct.Register(() => run.Terminate = true);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)], _session.OutputNames, run);
        var output = results[0].AsTensor<float>();
        var steps = output.Dimensions[^2];
        var classes = output.Dimensions[^1];
        var logits = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
        var decoded = Decode(logits, steps, classes, _chars).Where(c => c.Text.Trim().Length > 0).ToList();
        if (decoded.Count == 0) return null;

        // The model sees inputW pixels in `steps` timesteps; one input pixel is Length / rw pixels along the column, which
        // are measured from its (grown) top.
        var stepLength = (double)inputW / steps * grown.Length / rw;
        var spans = Spans(decoded, stepLength, 0, topMargin, grown.Length - bottomMargin);
        // The boxes sit on the column as given (the margin only fed the model): positions move up by the margin that was
        // really given at the top (none where the column touches the bitmap's edge).
        var words = decoded.Select((c, i) => new OcrWord(c.Text, column.Slice(spans[i].Top - topMargin, spans[i].Bottom - topMargin), c.Score)).ToList();
        return new VerticalRead(string.Concat(decoded.Select(c => c.Text)), words, decoded.Average(c => c.Score));
    }

    /// <summary>
    /// Samples the column straight into the model's input: counter-clockwise on its side (the column top at the left, the
    /// column's right edge at the top), deskewed along its quadrilateral and scaled to the model height, bilinear with
    /// OpenCV's pixel centres; written BGR, normalized to [-1, 1], as CHW; the padding to the right stays 0.
    /// </summary>
    internal static void Fill(Span<float> dst, ReadOnlySpan<byte> src, int rowBytes, int width, int height, ColumnQuad q, int rw, int inputW)
    {
        var (ox, oy) = (q.TopRight.X, q.TopRight.Y);
        var (ax, ay) = (q.BottomRight.X - ox, q.BottomRight.Y - oy); // along the column, down
        var (bx, by) = (q.TopLeft.X - ox, q.TopLeft.Y - oy); // across it, to the left
        var plane = Height * inputW;
        for (var y = 0; y < Height; y++)
        {
            var b = (y + 0.5) / Height;
            for (var x = 0; x < rw; x++)
            {
                var a = (x + 0.5) / rw;
                var fx = Math.Clamp(ox + a * ax + b * bx - 0.5, 0, width - 1);
                var fy = Math.Clamp(oy + a * ay + b * by - 0.5, 0, height - 1);
                var ix = (int)Math.Floor(fx);
                var iy = (int)Math.Floor(fy);
                var (wx, wy) = ((float)(fx - ix), (float)(fy - iy));
                int ix2 = Math.Min(ix + 1, width - 1), iy2 = Math.Min(iy + 1, height - 1);
                int p00 = iy * rowBytes + ix * 4, p10 = iy * rowBytes + ix2 * 4;
                int p01 = iy2 * rowBytes + ix * 4, p11 = iy2 * rowBytes + ix2 * 4;
                for (var c = 0; c < 3; c++) // BGRA bytes are already in B, G, R order
                {
                    var upper = src[p00 + c] * (1 - wx) + src[p10 + c] * wx;
                    var lower = src[p01 + c] * (1 - wx) + src[p11 + c] * wx;
                    var v = MathF.Round(upper * (1 - wy) + lower * wy); // OpenCV keeps the resized picture as bytes
                    dst[c * plane + y * inputW + x] = (v / 255f - 0.5f) / 0.5f;
                }
            }
        }
    }

    /// <summary>
    /// CTC decoding as PaddleOCR does it: the best class per timestep, repeats collapsed (a blank between two equal
    /// classes keeps both), blank (class 0) dropped. Class i is dictionary line i - 1; the class after the last line is
    /// the space; any other class has no character and only separates runs.
    /// </summary>
    internal static List<CtcChar> Decode(ReadOnlySpan<float> logits, int steps, int classes, IReadOnlyList<string> dictionary)
    {
        var result = new List<CtcChar>();
        var previous = -1;
        var first = 0;
        var sum = 0f;
        var count = 0;
        for (var t = 0; t <= steps; t++)
        {
            var best = -1;
            if (t < steps)
            {
                var row = logits.Slice(t * classes, classes);
                best = 0;
                for (var c = 1; c < classes; c++) if (row[c] > row[best]) best = c;
                if (best == previous)
                {
                    sum += row[best];
                    count++;
                    continue;
                }
            }
            // a run ends: previous class held timesteps first .. t - 1
            var text = previous > 0 && previous <= dictionary.Count ? dictionary[previous - 1]
                : previous == dictionary.Count + 1 ? " " : null;
            if (text is not null && count > 0) result.Add(new CtcChar(text, first, t - 1, sum / count));
            if (t == steps) break;
            previous = best;
            first = t;
            sum = logits[t * classes + best];
            count = 1;
        }
        return result;
    }

    /// <summary>
    /// The vertical range of each character: its centre is the middle of its timesteps (<paramref name="stepLength"/> pixels
    /// each, from <paramref name="origin"/>); neighbours split at the midpoint between their centres; the first starts at
    /// <paramref name="top"/> and the last ends at <paramref name="bottom"/>.
    /// </summary>
    internal static List<(double Top, double Bottom)> Spans(IReadOnlyList<CtcChar> chars, double stepLength, double origin, double top, double bottom)
    {
        var n = chars.Count;
        // Every character keeps a height: its centre stays at least `room` from the previous one and from the ends, so a
        // tail of characters whose timesteps lie past the bottom is spread over the end instead of collapsing on it.
        var room = n == 0 ? 0 : 0.3 * Math.Max(bottom - top, 0) / n;
        var centres = new double[n];
        for (var i = 0; i < n; i++)
        {
            var c = origin + (chars[i].First + chars[i].Last + 1) / 2.0 * stepLength;
            double lo = top + (i + 0.5) * room, hi = bottom - (n - i - 0.5) * room;
            if (hi < lo) lo = hi = (top + bottom) / 2;
            if (i > 0) c = Math.Max(c, centres[i - 1] + room);
            centres[i] = Math.Clamp(c, lo, hi);
        }
        var spans = new List<(double, double)>(n);
        for (var i = 0; i < n; i++)
        {
            var start = i == 0 ? top : (centres[i - 1] + centres[i]) / 2;
            var end = i == n - 1 ? bottom : (centres[i] + centres[i + 1]) / 2;
            spans.Add((start, end));
        }
        return spans;
    }

    public void Dispose() => _session.Dispose();
}
