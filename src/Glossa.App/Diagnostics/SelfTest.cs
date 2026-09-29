using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glossa.App.Ai;
using Glossa.App.Capture;
using Glossa.App.Lookup;
using Glossa.Core.Logging;
using Glossa.Core.Ocr;

namespace Glossa.App.Diagnostics;

/// <summary>
/// Glossa.exe --selftest &lt;cases.json&gt; &lt;out folder&gt; [count]: the real lookup path — recognition, dictionaries,
/// the AI card — on saved game screenshots, pointing at a known word, with the card kept off screen and nothing saved
/// to the library. Writes stage times and what it cost (memory, processor, video memory) before and after, so a change
/// can be judged by numbers, not by feel.
/// </summary>
internal static class SelfTest
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private sealed record Case(string Id, string? Image, string Word);

    private sealed record Result(string Id, string Word, string Outcome, bool Ok, LookupStages? Stages, double GlossaCpu, double ServerCpu,
        double GlossaMb, double ServerMb, int VramFreeMb)
    {
        public double CpuSeconds => GlossaCpu + ServerCpu;
    }

    public static async Task RunAsync(string casesPath, string outDir, int count, LookupController controller, LlamaServerHost host,
        ILog log)
    {
        Directory.CreateDirectory(outDir);
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var cases = JsonSerializer.Deserialize<List<Case>>(await File.ReadAllTextAsync(casesPath), json)!
            .Where(c => c.Image is not null && File.Exists(c.Image)).Take(count).ToList();
        log.Info($"selftest: {cases.Count} cases");

        // Where each word is, found with a separate recognizer over the whole frame (the app itself reads only the
        // strip around the cursor); it is freed before measuring, so its memory does not count against the app.
        var points = new Dictionary<string, (int X, int Y)>();
        using (var locator = new OcrEngine(Glossa.Core.Config.DataPaths.OcrModels))
        {
            foreach (var c in cases)
            {
                var frame = Load(c.Image!);
                var page = await locator.RecognizeAsync(frame.Bgra, frame.Width, frame.Height, frame.Stride, frame.Bounds,
                    OcrModelFamily.CjkLatin, CancellationToken.None);
                var line = page.Lines.FirstOrDefault(l => l.Text.Contains(c.Word, StringComparison.Ordinal));
                if (line is null) continue;
                // Point at the middle of the word's first character, as a hovering user would.
                var at = line.Text.IndexOf(c.Word, StringComparison.Ordinal);
                points[c.Id] = ((int)(line.Box.Left + line.Box.Width * (at + 0.5) / line.Text.Length), (int)line.Box.CenterY);
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Task.Delay(3000); // let warm-up settle before the idle sample
        var idle = LoadMeter.Sample(host);
        await Task.Delay(5000);
        var idleEnd = LoadMeter.Sample(host);

        LookupReport? last = null;
        controller.Reported += r => last = r;
        var results = new List<Result>();
        var minFree = idle.VramFreeMb;
        foreach (var c in cases)
        {
            if (!points.TryGetValue(c.Id, out var point))
            {
                results.Add(new Result(c.Id, c.Word, "слово не найдено на кадре", false, null, 0, 0, 0, 0, 0));
                continue;
            }
            var frame = Load(c.Image!);
            var (x, y) = point;

            last = null;
            var before = LoadMeter.Sample(host);
            try
            {
                await controller.RunAsync(frame, x, y, "selftest", "selftest.exe", Stopwatch.StartNew());
            }
            catch (Exception ex)
            {
                log.Error("selftest case " + c.Id, ex);
            }
            var after = LoadMeter.Sample(host);
            minFree = Math.Min(minFree, after.VramFreeMb);
            results.Add(new Result(c.Id, c.Word, last?.Result ?? "нет отчёта", last?.Ok ?? false, last?.Stages,
                after.GlossaCpu - before.GlossaCpu, after.ServerCpu - before.ServerCpu, after.GlossaMb, after.ServerMb, after.VramFreeMb));
        }
        // The first words again: «Помнить готовые карточки» must open them at once, without the AI.
        foreach (var c in cases.Where(c => points.ContainsKey(c.Id)).Take(3))
        {
            var (x, y) = points[c.Id];
            last = null;
            var before = LoadMeter.Sample(host);
            await controller.RunAsync(Load(c.Image!), x, y, "selftest", "selftest.exe", Stopwatch.StartNew());
            var after = LoadMeter.Sample(host);
            results.Add(new Result(c.Id + " (повтор)", c.Word, last?.Result ?? "нет отчёта", last?.Ok ?? false, last?.Stages,
                after.GlossaCpu - before.GlossaCpu, after.ServerCpu - before.ServerCpu, after.GlossaMb, after.ServerMb, after.VramFreeMb));
        }

        var end = LoadMeter.Sample(host);
        // What of it is garbage the collector has not got to yet, and what is really held.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var collected = LoadMeter.Sample(host);
        // Recognition's idle trim (GLOSSA_OCR_TRIM seconds in a selftest): memory once the reading pauses.
        var trimWait = int.TryParse(Environment.GetEnvironmentVariable("GLOSSA_OCR_TRIM"), out var t) ? t : 0;
        LoadSample? trimmed = null;
        if (trimWait is > 0 and <= 60)
        {
            await Task.Delay(TimeSpan.FromSeconds(trimWait * 2 + 2));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            trimmed = LoadMeter.Sample(host);
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "selftest.json"),
            JsonSerializer.Serialize(new { idle, idleEnd, end, collected, trimmed, results }, json));
        await File.WriteAllTextAsync(Path.Combine(outDir, "selftest.txt"), Summary(idle, idleEnd, end, collected, trimmed, minFree, results));
        log.Info("selftest: done");
    }

    private static string Summary(LoadSample idle, LoadSample idleEnd, LoadSample end, LoadSample collected, LoadSample? trimmed, int minFree,
        List<Result> results)
    {
        var sb = new StringBuilder();
        string F(double v, string fmt = "0") => v.ToString(fmt, Russian);
        double Median(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(v => v).ToArray();
            return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        var done = results.Where(r => r.Stages is { AiMs: not null } && !r.Id.EndsWith("(повтор)", StringComparison.Ordinal)).ToList();
        var warm = done.Skip(1).ToList(); // the first one also loads the model
        sb.AppendLine($"Самопроверка: {results.Count} кадров, с ИИ-карточкой {done.Count}, ошибок {results.Count(r => !r.Ok)}");
        foreach (var r in results.Where(r => !r.Ok)) sb.AppendLine($"  ✗ {r.Id} «{r.Word}»: {r.Outcome}");
        sb.AppendLine();
        sb.AppendLine("В простое (8 с после запуска):");
        sb.AppendLine($"  ОЗУ Glossa {F(idleEnd.GlossaMb)} МБ, процессор {F(idleEnd.CpuPercentSince(idle), "0.0")}%, свободно видеопамяти {F(idle.VramFreeMb)} из {F(idle.VramTotalMb)} МБ");
        if (done.Count > 0)
        {
            var first = done[0].Stages!;
            sb.AppendLine($"Первый поиск (с загрузкой модели): карточка {first.CardMs} мс, перевод {first.FirstFieldMs} мс, вся ИИ-карточка {first.AiMs} мс");
        }
        if (warm.Count > 0)
        {
            sb.AppendLine($"Остальные {warm.Count} (медиана):");
            sb.AppendLine($"  распознавание {F(Median(warm.Select(r => (double)(r.Stages!.OcrMs - r.Stages.CaptureMs))))} мс, " +
                          $"справочники {F(Median(warm.Select(r => (double)(r.Stages!.DictionariesMs - r.Stages.OcrMs))))} мс, " +
                          $"карточка на экране {F(Median(warm.Select(r => (double)r.Stages!.CardMs)))} мс");
            sb.AppendLine($"  перевод {F(Median(warm.Select(r => (double)(r.Stages!.FirstFieldMs ?? 0))))} мс, " +
                          $"вся ИИ-карточка {F(Median(warm.Select(r => (double)r.Stages!.AiMs!)))} мс, " +
                          $"процессор на поиск {F(Median(warm.Select(r => r.CpuSeconds)), "0.00")} с " +
                          $"(Glossa {F(Median(warm.Select(r => r.GlossaCpu)), "0.00")}, llama-server {F(Median(warm.Select(r => r.ServerCpu)), "0.00")})");
        }
        sb.AppendLine("После прогона:");
        sb.AppendLine($"  ОЗУ Glossa {F(end.GlossaMb)} МБ (после сборки мусора {F(collected.GlossaMb)}" +
                      (trimmed is { } tr ? $", после паузы в чтении {F(tr.GlossaMb)}" : "") + $"), llama-server {F(end.ServerMb)} МБ; " +
                      $"видеопамять под ИИ ≈ {F(idle.VramFreeMb - minFree)} МБ " +
                      $"(свободно было {F(idle.VramFreeMb)}, минимум {F(minFree)})");
        sb.AppendLine();
        var repeats = results.Where(r => r.Id.EndsWith("(повтор)", StringComparison.Ordinal)).ToList();
        if (repeats.Count > 0)
            sb.AppendLine($"Повтор {repeats.Count} слов: " + string.Join("; ", repeats.Select(r => $"«{r.Word}» {r.Outcome}, {r.Stages?.AiMs} мс")));
        sb.AppendLine();
        foreach (var r in results)
            sb.AppendLine(r.Stages is { } s
                ? $"{r.Id,-22} {r.Word,-14} ocr {s.OcrMs,5}  dict {s.DictionariesMs - s.OcrMs,4}  card {s.CardMs,5}  first {s.FirstFieldMs,6}  ai {s.AiMs,6}  cpu {F(r.GlossaCpu, "0.00")}+{F(r.ServerCpu, "0.00")} с"
                : $"{r.Id,-22} {r.Word,-14} {r.Outcome}");
        return sb.ToString();
    }

    /// <summary>A screenshot as a captured frame whose screen position is its own pixel grid.</summary>
    private static CapturedFrame Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return new CapturedFrame(pixels, bgra.PixelWidth, bgra.PixelHeight, stride, new PixelRect(0, 0, bgra.PixelWidth, bgra.PixelHeight));
    }
}
