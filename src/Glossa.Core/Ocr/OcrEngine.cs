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

    /// <summary>Raised after an idle trim (for the log).</summary>
    public event Action? Trimmed;

    /// <summary>Rebuilds the sessions in use when recognition has been idle long enough; skipped while one is running.</summary>
    private void TrimIfIdle()
    {
        if (!_used || TrimAfter is not { } after || DateTime.UtcNow - _lastUse < after) return;
        if (!_gate.Wait(0)) return;
        try
        {
            var families = _engines.Keys.ToList();
            foreach (var e in _engines.Values) e.Dispose();
            _engines.Clear();
            foreach (var family in families) Get(family);
            _used = false;
        }
        catch (Exception)
        {
            // A failed rebuild is retried on the next lookup (Get creates what is missing).
        }
        finally
        {
            _gate.Release();
        }
        Trimmed?.Invoke();
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
    /// lines and words in screen coordinates.
    /// </summary>
    public async Task<OcrPage> RecognizeAsync(
        byte[] bgra, int width, int height, int stride, PixelRect region, OcrModelFamily family, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _used = true;
            return await Task.Run(() => Recognize(bgra, width, height, stride, region, family, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    private OcrPage Recognize(byte[] bgra, int width, int height, int stride, PixelRect region, OcrModelFamily family, CancellationToken ct)
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

        var result = engine.Detect(bitmap, options, ct);
        var lines = new List<OcrLine>(result.TextBlocks.Length);
        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var lineBox = ToRect(block.BoxPoints, region);
            var words = block.WordResults is { Length: > 0 } wr
                ? wr.Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => new OcrWord(w.Text, ToRect(w.BoxPoints, region), w.Score)).ToList()
                : [new OcrWord(block.Text, lineBox, Average(block.CharScores))];
            lines.Add(new OcrLine(block.Text, lineBox, words, Average(block.CharScores)));
        }
        return new OcrPage(MergeFragments(lines), region, sw.Elapsed);
    }

    /// <summary>
    /// The detector sometimes cuts one visual line into pieces (wide letter gaps, CJK punctuation).
    /// Pieces that share a baseline and sit close together are joined back, left to right.
    /// </summary>
    internal static List<OcrLine> MergeFragments(List<OcrLine> lines)
    {
        var pending = lines.OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).ToList();
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
                    var overlap = Math.Min(box.Bottom, c.Box.Bottom) - Math.Max(box.Top, c.Box.Top);
                    var minH = Math.Min(box.Height, c.Box.Height);
                    var gap = Math.Max(c.Box.Left - box.Right, box.Left - c.Box.Right);
                    if (overlap > minH * 0.6 && gap < Math.Max(box.Height, c.Box.Height) * 1.5)
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
                group.Average(g => g.Score)));
        }
        return result;
    }

    private RapidOcr Get(OcrModelFamily family)
    {
        if (_engines.TryGetValue(family, out var e)) return e;

        var v5 = Path.Combine(_modelsDir, "v5");
        var v6 = Path.Combine(_modelsDir, "v6");
        var cls = Path.Combine(v5, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        // v6 medium reads with the same 18 708 characters as small (checked 2026-09-29), so it uses small's list.
        var v6Keys = Path.Combine(v6, "ppocrv6_small_dict.txt");
        var v6MediumRec = Path.Combine(v6, "PP-OCRv6_rec_medium.onnx");
        var v5Det = Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx");
        var (det, rec, keys) = family switch
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
        _trimTimer?.Dispose();
        foreach (var e in _engines.Values) e.Dispose();
        _engines.Clear();
        _gate.Dispose();
    }
}
