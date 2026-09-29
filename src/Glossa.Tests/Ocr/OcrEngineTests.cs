using Glossa.Core.Ocr;
using Glossa.Core.Text;
using SkiaSharp;
using Xunit.Abstractions;

namespace Glossa.Tests.Ocr;

public class OcrEngineTests(ITestOutputHelper output)
{
    private const string ModelsDir = @"D:\GlossaData\models\ocr";

    /// <summary>Renders light-on-dark "game dialogue" lines, the way they typically look on screen.</summary>
    private static (byte[] Bgra, int W, int H, int Stride) Render(params (string Font, string Text)[] lines)
    {
        const int w = 900, lineH = 75;
        var h = 20 + lines.Length * lineH;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(40, 34, 30));
            for (var i = 0; i < lines.Length; i++)
            {
                using var font = new SKFont(SKTypeface.FromFamilyName(lines[i].Font), 34);
                using var paint = new SKPaint { Color = new SKColor(245, 230, 200), IsAntialias = true };
                canvas.DrawText(lines[i].Text, 20, 55 + i * lineH, font, paint);
            }
        }
        return (bmp.Bytes, w, h, bmp.RowBytes);
    }

    [Fact]
    public async Task Reads_english_japanese_chinese_with_screen_coordinates()
    {
        if (!OcrEngine.ModelsPresent(ModelsDir)) return;
        var (bgra, w, h, stride) = Render(
            ("Segoe UI", "A new neighbor...? Hello, stranger."),
            ("Yu Gothic", "薄暗いじめじめした所でニャーニャー泣いていた。"),
            ("Microsoft YaHei", "我不知道自己是在哪里出生的。"));
        var region = new PixelRect(1000, 500, 1000 + w, 500 + h);

        using var ocr = new OcrEngine(ModelsDir);
        ocr.Warm(OcrModelFamily.CjkLatin);
        var page = await ocr.RecognizeAsync(bgra, w, h, stride, region, OcrModelFamily.CjkLatin, CancellationToken.None);

        output.WriteLine($"{page.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var l in page.Lines) output.WriteLine($"{l.Box} {l.Text} [{string.Join('|', l.Words.Select(x => x.Text))}]");

        Assert.Equal(3, page.Lines.Count);
        Assert.Contains("neighbor", page.Lines[0].Text);
        Assert.Contains("薄暗い", page.Lines[1].Text);
        Assert.Contains("出生", page.Lines[2].Text);
        Assert.True(page.Lines[0].Box.Left >= region.Left && page.Lines[0].Box.Top >= region.Top);

        // End to end with the hit tester: cursor over "neighbor".
        var neighbor = page.Lines[0].Words.First(x => x.Text.StartsWith("neighbor"));
        var hit = new HitTester().Hit(page, neighbor.Box.CenterX, neighbor.Box.CenterY);
        Assert.Equal("neighbor", hit?.Word);
    }

    [Fact]
    public async Task Reads_russian_with_cyrillic_model()
    {
        if (!OcrEngine.ModelsPresent(ModelsDir)) return;
        var (bgra, w, h, stride) = Render(("Segoe UI", "Новый сосед...? Привет, незнакомец."));

        using var ocr = new OcrEngine(ModelsDir);
        var page = await ocr.RecognizeAsync(bgra, w, h, stride, new PixelRect(0, 0, w, h), OcrModelFamily.Cyrillic, CancellationToken.None);

        output.WriteLine(string.Join(" / ", page.Lines.Select(l => l.Text)));
        Assert.Contains("сосед", string.Concat(page.Lines.Select(l => l.Text)));
    }
}
