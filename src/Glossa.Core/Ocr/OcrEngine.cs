using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace Glossa.Core.Ocr;

/// <summary>Which recognizer to run. Russian needs PP-OCRv5 ESLAV; the rest are for measuring.</summary>
public enum OcrModelFamily
{
    /// <summary>
    /// English, Japanese and Chinese: lines found by the fast PP-OCRv5 mobile detector and read by the PP-OCRv6 medium
    /// recognizer (v5 mobile when its file is not there). Sets of 2026-09-29: 30 of 32 hard lines against v5's 24,
    /// letter errors 1.4% against 6.6%, about 60 ms more per lookup.
    /// </summary>
    CjkLatin,
    Cyrillic,

    /// <summary>Measuring: PP-OCRv5 mobile alone, the reader before 2026-09-29.</summary>
    V5Mobile,

    /// <summary>Measuring: PP-OCRv6 small pair.</summary>
    V6Multi,

    /// <summary>Measuring: PP-OCRv6 medium pair (det 59 MB, rec 73 MB).</summary>
    V6Medium,

    /// <summary>Measuring: lines found by the v6 small detector, read by the v6 medium recognizer.</summary>
    V6SmallDetMediumRec,
}

/// <summary>
/// ONNX Runtime's buffer reuse: Arena keeps an arena and per-size memory plans (fastest, but holds the largest
/// buffers ever needed and plans per image size); NoPatterns keeps the arena only; None allocates per run.
/// </summary>
public enum OcrMemory { Arena, NoPatterns, None }

/// <summary>
/// PP-OCR (RapidOcrNet / ONNX Runtime on CPU). Sessions are created on first use per model family
/// and kept warm; inference is serialized because ORT sessions here are used from one thread at a time.
/// ORT's arena keeps the largest buffers any recognition needed (about 1 GB after a reading session); after
/// <see cref="TrimAfter"/> without recognitions the sessions are rebuilt, which returns that memory while keeping
/// the next lookup as fast (selftest 2026-09-29: no arena at all costs ~90 ms per lookup instead).
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly string _modelsDir;
    private readonly OcrMemory _memory;
    private int _threads;
    private readonly Dictionary<OcrModelFamily, RapidOcr> _engines = [];
    private VerticalReader? _vertical;
    private bool _verticalFailed;
    private volatile bool _disposed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer? _trimTimer;
    private DateTime _lastUse = DateTime.UtcNow;
    private bool _used;

    /// <param name="memory">How ONNX Runtime keeps buffers between recognitions (see <see cref="OcrMemory"/>).</param>
    /// <param name="trimAfter">Idle time after which the sessions are rebuilt to give back their buffers; null — never.</param>
    public OcrEngine(string modelsDir, int threads = 4, OcrMemory memory = OcrMemory.Arena, TimeSpan? trimAfter = null)
    {
        _modelsDir = modelsDir;
        _threads = threads;
        _memory = memory;
        TrimAfter = trimAfter;
        if (trimAfter is { } t)
        {
            var tick = TimeSpan.FromTicks(Math.Clamp(t.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(30).Ticks));
            _trimTimer = new Timer(_ => TrimIfIdle(), null, tick, tick);
        }
    }

    public TimeSpan? TrimAfter { get; }

    /// <summary>For measuring: how far the detector's line boxes are widened (DBNet unclip); null keeps the preset's.</summary>
    public float? UnClipRatio { get; init; }

    /// <summary>For measuring: the detector's pixel threshold on its text map (DBNet); null keeps the preset's.</summary>
    public float? BoxThresh { get; init; }

    /// <summary>For measuring: the mean text-map score a box needs to be kept; null keeps the preset's.</summary>
    public float? BoxScoreThresh { get; init; }

    /// <summary>For measuring: the recognizer's score a line needs to be kept; null keeps the preset's.</summary>
    public float? TextScore { get; init; }

    /// <summary>
    /// Read tall detector boxes as vertical Japanese (see <see cref="VerticalReader"/>); on by default. For measuring: off
    /// gives the library's lines alone.
    /// </summary>
    public bool VerticalReading { get; init; } = true;

    /// <summary>How many times the vertical reader's session was created (for measuring and tests: pages without columns create none).</summary>
    public int VerticalSessionsCreated { get; private set; }

    /// <summary>Raised once when the vertical reader cannot be created (until the next trim), with the reason (for the log).</summary>
    public event Action<string>? VerticalFailed;

    /// <summary>Raised when reading one column failed (not the lookup's cancellation): the library's reading of its box stays. For the log.</summary>
    public event Action<string>? ColumnReadFailed;

    /// <summary>
    /// Whether a lookup in <paramref name="language"/> may read columns of vertical text: Japanese and Chinese do, and so
    /// does an unknown or automatic language; English and Russian never have columns worth a second session.
    /// </summary>
    public static bool ColumnsAllowed(string? language) => language is not ("en" or "ru");

    /// <summary>Raised after an idle trim (for the log).</summary>
    public event Action? Trimmed;

    /// <summary>Rebuilds the sessions in use when recognition has been idle long enough; skipped while one is running.</summary>
    private void TrimIfIdle()
    {
        if (_disposed || !_used || TrimAfter is not { } after || DateTime.UtcNow - _lastUse < after) return;
        bool taken;
        try { taken = _gate.Wait(0); }
        catch (ObjectDisposedException) { return; }
        if (!taken) return;
        try
        {
            var families = _engines.Keys.ToList();
            foreach (var e in _engines.Values) e.Dispose();
            _engines.Clear();
            DisposeVertical(); // created again by the next column, so its memory goes back meanwhile
            foreach (var family in families) Get(family);
            _used = false;
        }
        catch (Exception)
        {
            // A failed rebuild is retried on the next lookup (Get creates what is missing).
        }
        finally
        {
            try { _gate.Release(); }
            catch (ObjectDisposedException) { } // disposed while this ran
        }
        if (!_disposed) Trimmed?.Invoke();
    }

    /// <summary>Processor threads per recognition. A change takes effect from the next lookup (sessions are rebuilt).</summary>
    public int Threads
    {
        get => _threads;
        set
        {
            value = Math.Clamp(value, 1, Environment.ProcessorCount);
            if (value == _threads) return;
            _gate.Wait();
            try
            {
                _threads = value;
                foreach (var e in _engines.Values) e.Dispose();
                _engines.Clear();
                DisposeVertical();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public static bool ModelsPresent(string modelsDir) =>
        File.Exists(Path.Combine(modelsDir, "v5", "ch_PP-OCRv5_mobile_det.onnx"));

    /// <summary>Loads the models ahead of the first lookup so it does not pay the start-up cost.</summary>
    public void Warm(OcrModelFamily family)
    {
        _gate.Wait();
        try { Get(family); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Recognizes a BGRA image captured from <paramref name="region"/> (screen pixels) and returns
    /// lines and words in screen coordinates. <paramref name="screenWidth"/> is the width of the screen the image is a part of
    /// (0: the image is the whole screen or it is not known): the second look at a page with columns is sized by it.
    /// <paramref name="language"/> is the language of the text on screen as the lookup knows it (null: not known); columns
    /// are read only where <see cref="ColumnsAllowed"/>.
    /// </summary>
    public async Task<OcrPage> RecognizeAsync(
        byte[] bgra, int width, int height, int stride, PixelRect region, OcrModelFamily family, CancellationToken ct, int screenWidth = 0, string? language = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _used = true;
            return await Task.Run(() => Recognize(bgra, width, height, stride, region, family, screenWidth, ColumnsAllowed(language), ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    private OcrPage Recognize(byte[] bgra, int width, int height, int stride, PixelRect region, OcrModelFamily family, int screenWidth, bool columns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var engine = Get(family);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var source = new SKBitmap();
            source.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                handle.AddrOfPinnedObject(), stride);
            source.CopyTo(bitmap);
        }
        finally
        {
            handle.Free();
        }

        // Python-rapidocr preprocessing: the detector upscales the short side to 736 px. Screen crops are
        // short and game text is small; without the upscale DBNet splits lines and drops characters.
        // PP-OCRv6 has its own preset (RapidOcrNet 4.2): with v5's options it drops text and garbles boxes.
        var options = (family is OcrModelFamily.V6Multi or OcrModelFamily.V6Medium or OcrModelFamily.V6SmallDetMediumRec
            ? RapidOcrOptions.PPOCRv6 : RapidOcrOptions.PythonCompat) with
        {
            ReturnWordBox = true,
            DoAngle = false,
        };
        if (UnClipRatio is { } unclip) options = options with { UnClipRatio = unclip };
        if (BoxThresh is { } box) options = options with { BoxThresh = box };
        if (BoxScoreThresh is { } boxScore) options = options with { BoxScoreThresh = boxScore };
        if (TextScore is { } textScore) options = options with { TextScore = textScore };

        // The library drops every block whose read scores under TextScore, and a clockwise-read column always does: its
        // box would never reach the vertical pass. So it keeps them all here and the same filter runs below.
        var vertical = VerticalReading && columns && family == OcrModelFamily.CjkLatin;
        var minScore = options.TextScore;
        if (vertical) options = options with { TextScore = 0f };

        var result = engine.Detect(bitmap, options, ct);
        var lines = new List<OcrLine>(result.TextBlocks.Length);
        var quads = new Dictionary<PixelRect, SKPointI[]>(); // the detector's quadrilateral of each box (bitmap pixels)
        foreach (var block in result.TextBlocks)
        {
            var lineBox = ToRect(block.BoxPoints, region);
            quads.TryAdd(lineBox, block.BoxPoints);
            // A blank block is kept for now: a tall one is a column the library could not read, a small one under a
            // column is its final period; the vertical pass needs their boxes.
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                if (vertical) lines.Add(new OcrLine("", lineBox, [], 0f));
                continue;
            }
            var words = block.WordResults is { Length: > 0 } wr
                ? wr.Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => new OcrWord(w.Text, ToRect(w.BoxPoints, region), w.Score)).ToList()
                : [new OcrWord(block.Text, lineBox, Average(block.CharScores))];
            lines.Add(new OcrLine(block.Text, lineBox, words, Average(block.CharScores)));
        }
        if (vertical)
        {
            Func<PixelRect, IReadOnlyList<PixelRect>, VerticalRead?> read = (box, pieces) => ReadColumn(bitmap, region, box, pieces, quads, family, ct);
            var tried = new HashSet<OcrLine>(ReferenceEqualityComparer.Instance);
            lines = VerticalColumns.Apply(lines, read, minScore, tried);
            // The second look: a page that has a column (it may have more the first pass lost) or a column shredded into
            // glyphs (nothing of it reached the page as a column at all).
            // (a page with no Chinese or Japanese on it has neither: the shape alone is not enough.)
            if (lines.Any(l => l.Vertical) || (VerticalColumns.PageHasCjk(lines, minScore) && VerticalColumns.LooksShredded(lines)))
                lines = AddMissedColumns(engine, bitmap, options, region, lines, minScore, read, quads, screenWidth, tried, ct);
            // The library's own filter, which TextScore = 0 switched off for this call: the lines it would have dropped.
            lines.RemoveAll(l => !l.Vertical && (string.IsNullOrWhiteSpace(l.Text) || l.Score < minScore));
        }
        return new OcrPage(MergeFragments(lines), region, sw.Elapsed);
    }


    /// <summary>
    /// A page with a column often has more of them that the first pass lost. One more detector-only pass at
    /// <see cref="VerticalColumns.PassSide"/> finds their boxes: at the size of the lookup (the short side blown up to 736
    /// px, or a whole 1080p frame as it is) the detector cuts a thin column into glyphs, which the library reads as
    /// nothing and drops, so its box never reaches the page; shrunk to half it gives one box per column (tategaki
    /// experiment: book frame 1 of 4 columns lost at native size, none at 960 of 1920) in about a fifth of the time of the
    /// first pass. A tall box that no line of the page already covers is a candidate; one that is the same column as a
    /// line the first look cut short, but longer, replaces it when it reads. Pages without a column never pay for it.
    /// </summary>
    private static List<OcrLine> AddMissedColumns(RapidOcr engine, SKBitmap bitmap, RapidOcrOptions options, PixelRect region,
        List<OcrLine> lines, float minScore, Func<PixelRect, IReadOnlyList<PixelRect>, VerticalRead?> read,
        Dictionary<PixelRect, SKPointI[]> quads, int screenWidth, ISet<OcrLine> tried, CancellationToken ct)
    {
        var found = new List<OcrLine>();
        var outgrown = new List<(OcrLine Old, PixelRect Box)>(); // columns the first look cut short, and the longer boxes of them
        foreach (var box in engine.DetectBoxes(bitmap, options with { ImgResize = VerticalColumns.PassSide(Math.Max(bitmap.Width, bitmap.Height), screenWidth) }, ct))
        {
            var rect = ToRect(box.BoxPoints, region);
            if (!VerticalColumns.IsColumn(rect)) continue;
            if (tried.Any(l => VerticalColumns.SameBox(l.Box, rect))) continue; // the first look read this box and it was not a column
            var old = lines.FirstOrDefault(l => VerticalColumns.Outgrows(rect, l));
            if (old is not null)
            {
                if (outgrown.Any(o => ReferenceEquals(o.Old, old))) continue;
                outgrown.Add((old, rect));
            }
            else if (lines.Any(l => (l.Vertical || l.Score >= minScore) && VerticalColumns.Covers(l.Box, rect)))
            {
                continue;
            }
            quads.TryAdd(rect, box.BoxPoints);
            found.Add(new OcrLine("", rect, [], 0f));
        }
        if (found.Count == 0) return lines;

        return VerticalColumns.ReplaceOutgrown(lines, outgrown, found, read, minScore, tried);
    }

    /// <summary>
    /// Reads one column (screen box) with the vertical reader, created on the first one; null when it cannot be read. The
    /// column is cropped along the detector's quadrilaterals of its <paramref name="pieces"/> (a leaning column), the box
    /// when a piece has none.
    /// </summary>
    private VerticalRead? ReadColumn(SKBitmap bitmap, PixelRect region, PixelRect box, IReadOnlyList<PixelRect> pieces,
        Dictionary<PixelRect, SKPointI[]> quads, OcrModelFamily family, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_verticalFailed) return null;
        try
        {
            if (_vertical is null)
            {
                var (_, rec, keys) = ModelPaths(family);
                _vertical = new VerticalReader(rec, keys, _threads, _memory);
                VerticalSessionsCreated++;
            }
            var corners = pieces
                .Select(p => quads.TryGetValue(p, out var q) ? q.Select(c => ((double)c.X, (double)c.Y)).ToArray() : QuadOf(p.Offset(-region.Left, -region.Top)))
                .ToList();
            var column = ColumnQuad.Fit(corners) ?? ColumnQuad.FromRect(box.Offset(-region.Left, -region.Top));
            return _vertical.Read(bitmap, column, ct)?.Offset(region.Left, region.Top);
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested(); // a read stopped by the lookup's token
            if (_vertical is null)
            {
                // The session could not be made: no retry per column until the next trim or change of threads.
                _verticalFailed = true;
                VerticalFailed?.Invoke(e.Message);
            }
            else
            {
                ColumnReadFailed?.Invoke(e.GetType().Name + ": " + e.Message);
            }
            return null; // the library's reading of the box stays
        }
    }

    private static (double X, double Y)[] QuadOf(PixelRect r) => [(r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom)];

    private void DisposeVertical()
    {
        _vertical?.Dispose();
        _vertical = null;
        _verticalFailed = false;
    }

    /// <summary>The widest gap, in line heights, between two pieces that are still one line.</summary>
    private const double PieceGap = 1.5;

    /// <summary>
    /// Whether two pieces are parts of one visual line: they share a baseline (the vertical overlap is most of the lower
    /// box) and the gap between them is short.
    /// </summary>
    internal static bool SameRow(PixelRect a, PixelRect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var minH = Math.Min(a.Height, b.Height);
        var gap = Math.Max(b.Left - a.Right, a.Left - b.Right);
        return overlap > minH * 0.6 && gap < Math.Max(a.Height, b.Height) * PieceGap;
    }

    /// <summary>
    /// The detector sometimes cuts one visual line into pieces (wide letter gaps, CJK punctuation).
    /// Pieces that share a baseline and sit close together are joined back, left to right.
    /// </summary>
    internal static List<OcrLine> MergeFragments(List<OcrLine> lines)
    {
        // Rows from the top; columns last, right to left (the order tategaki is read in).
        var pending = lines.OrderBy(l => l.Vertical).ThenBy(l => l.Vertical ? -l.Box.Right : l.Box.Top).ThenBy(l => l.Box.Left).ToList();
        var result = new List<OcrLine>();
        while (pending.Count > 0)
        {
            var group = new List<OcrLine> { pending[0] };
            pending.RemoveAt(0);
            bool grew;
            do
            {
                grew = false;
                var box = group.Select(g => g.Box).Aggregate((a, b) => a.Union(b));
                for (var i = 0; i < pending.Count; i++)
                {
                    var c = pending[i];
                    if (c.Vertical || group[0].Vertical) continue; // columns are not a row: they never merge here
                    if (SameRow(box, c.Box))
                    {
                        group.Add(c);
                        pending.RemoveAt(i);
                        grew = true;
                        break;
                    }
                }
            } while (grew);

            if (group.Count == 1) { result.Add(group[0]); continue; }
            group.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
            var cjk = group.Any(g => Text.Scripts.ContainsCjk(g.Text));
            var text = string.Join(cjk ? "" : " ", group.Select(g => g.Text));
            result.Add(new OcrLine(
                text,
                group.Select(g => g.Box).Aggregate((a, b) => a.Union(b)),
                group.SelectMany(g => g.Words).ToList(),
                group.Average(g => g.Score),
                group[0].Vertical));
        }
        return result;
    }

    /// <summary>The detector, recognizer and character list of a family.</summary>
    private (string Det, string Rec, string Keys) ModelPaths(OcrModelFamily family)
    {
        var v5 = Path.Combine(_modelsDir, "v5");
        var v6 = Path.Combine(_modelsDir, "v6");
        // v6 medium reads with the same 18 708 characters as small (checked 2026-09-29), so it uses small's list.
        var v6Keys = Path.Combine(v6, "ppocrv6_small_dict.txt");
        var v6MediumRec = Path.Combine(v6, "PP-OCRv6_rec_medium.onnx");
        var v5Det = Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx");
        return family switch
        {
            OcrModelFamily.CjkLatin when File.Exists(v6MediumRec) && File.Exists(v6Keys) => (v5Det, v6MediumRec, v6Keys),
            OcrModelFamily.Cyrillic => (Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(v5, "eslav_PP-OCRv5_rec_mobile.onnx"), Path.Combine(v5, "ppocrv5_eslav_dict.txt")),
            OcrModelFamily.V6Multi => (Path.Combine(v6, "PP-OCRv6_det_small.onnx"),
                Path.Combine(v6, "PP-OCRv6_rec_small.onnx"), Path.Combine(v6, "ppocrv6_small_dict.txt")),
            OcrModelFamily.V6Medium => (Path.Combine(v6, "PP-OCRv6_det_medium.onnx"), v6MediumRec, v6Keys),
            OcrModelFamily.V6SmallDetMediumRec => (Path.Combine(v6, "PP-OCRv6_det_small.onnx"), v6MediumRec, v6Keys),
            _ => (Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(v5, "ch_PP-OCRv5_rec_mobile.onnx"), Path.Combine(v5, "ppocrv5_ch_dict.txt")),
        };
    }

    private RapidOcr Get(OcrModelFamily family)
    {
        if (_engines.TryGetValue(family, out var e)) return e;

        var (det, rec, keys) = ModelPaths(family);
        var cls = Path.Combine(_modelsDir, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var so = RapidOcr.GetDefaultSessionOptions(_threads);
        // ORT worker threads spin after each run by default and burn cores a running game needs.
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (_memory != OcrMemory.Arena) so.EnableMemoryPattern = false;
        if (_memory == OcrMemory.None) so.EnableCpuMemArena = false;

        var engine = new RapidOcr();
        engine.InitModels(det, cls, rec, keys, so);
        _engines[family] = engine;
        return engine;
    }

    private static PixelRect ToRect(SKPointI[] pts, PixelRect region) =>
        PixelRect.FromPoints(pts.Select(p => ((double)p.X, (double)p.Y))).Offset(region.Left, region.Top);

    private static float Average(float[]? scores) => scores is { Length: > 0 } ? scores.Average() : 0f;

    public void Dispose()
    {
        _disposed = true;
        _trimTimer?.Dispose();
        // A recognition in flight is using the sessions natively: wait for it (a session disposed under a run is a crash).
        var held = _gate.Wait(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var e in _engines.Values) e.Dispose();
            _engines.Clear();
            DisposeVertical();
        }
        finally
        {
            if (held) _gate.Release();
        }
        _gate.Dispose();
    }
}
