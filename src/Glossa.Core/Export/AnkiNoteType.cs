using System.Net;
using System.Text;
using Glossa.Core.Library;
using Glossa.Core.Lookup;
using SkiaSharp;

namespace Glossa.Core.Export;

/// <summary>
/// The "Glossa Word" note type shared by the .apkg export and AnkiConnect sync. The name carries a version:
/// Anki cannot change the fields of an existing note type, so a new layout means a new name.
/// </summary>
public static class AnkiNoteType
{
    public const string Name = "Glossa Word v1";
    public const long ModelId = 1727470000001;
    public const string RootDeck = "Glossa";
    public const string RemovedTag = "glossa::removed";

    public static readonly string[] Fields =
    [
        "GlossaId", "Headword", "Reading", "Translation", "Definition", "Context", "ContextTranslation",
        "Level", "PartOfSpeech", "Synonyms", "Forms", "Components", "Explanation", "Image", "Audio",
        "Language", "Source", "Reverse",
    ];

    public const string Css = """
        .card { font-family: "Segoe UI", "Yu Gothic UI", "Microsoft YaHei UI", sans-serif; font-size: 20px;
                text-align: center; color: #1f2328; background: #fafafa; }
        .nightMode.card, .night_mode .card { color: #ededed; background: #242629; }
        .img img { max-width: 100%; max-height: 360px; border-radius: 8px; }
        .ctx { font-size: 18px; margin: 12px auto; max-width: 720px; }
        .ctx b { background: #e9c46a; color: #000; padding: 0 3px; border-radius: 3px; font-weight: normal; }
        .word { font-size: 34px; font-weight: 600; margin-top: 8px; }
        .reading { color: #8a8f98; font-size: 20px; }
        .tr { color: #b07d1a; font-size: 26px; font-weight: 600; margin-top: 6px; }
        .nightMode .tr, .night_mode .tr { color: #e9c46a; }
        .def { margin-top: 6px; }
        .ctxtr { color: #6f757b; font-size: 17px; margin: 10px auto; max-width: 720px; }
        .badge { display: inline-block; font-size: 13px; padding: 1px 7px; border-radius: 4px; background: #2e6ba8; color: #fff; }
        details { margin-top: 12px; font-size: 16px; text-align: left; max-width: 720px; margin-left: auto; margin-right: auto; }
        """;

    public sealed record Template(string Name, string Front, string Back);

    public static readonly Template[] Templates =
    [
        new("Слово → значение",
            """
            <div class="img">{{Image}}</div>
            <div class="ctx">{{Context}}</div>
            <div class="word">{{Headword}}</div>
            {{Audio}}
            """,
            """
            {{FrontSide}}
            <hr id="answer">
            <div class="reading">{{Reading}}</div>
            <div class="tr">{{Translation}}</div>
            <div class="def">{{Definition}}</div>
            <div class="ctxtr">{{ContextTranslation}}</div>
            <details><summary>Подробнее</summary>
            {{#Level}}<span class="badge">{{Level}}</span> {{/Level}}{{PartOfSpeech}}<br>
            {{#Synonyms}}Синонимы: {{Synonyms}}<br>{{/Synonyms}}
            {{#Forms}}Формы: {{Forms}}<br>{{/Forms}}
            {{#Components}}{{Components}}<br>{{/Components}}
            {{#Explanation}}{{Explanation}}<br>{{/Explanation}}
            {{#Source}}<small>{{Source}}</small>{{/Source}}
            </details>
            """),
        // Generated only when the Reverse field is not empty (the "reverse cards" option).
        new("Значение → слово",
            """
            {{#Reverse}}
            <div class="tr">{{Translation}}</div>
            <div class="ctxtr">{{ContextTranslation}}</div>
            {{/Reverse}}
            """,
            """
            {{FrontSide}}
            <hr id="answer">
            <div class="word">{{Headword}}</div>
            <div class="reading">{{Reading}}</div>
            {{Audio}}
            <div class="img">{{Image}}</div>
            <div class="ctx">{{Context}}</div>
            """),
    ];

    public static string DeckFor(string language) => $"{RootDeck}::{Languages.RussianName(language) switch
    {
        { Length: > 0 } n => char.ToUpperInvariant(n[0]) + n[1..],
        _ => language,
    }}";

    /// <summary>Stable positive id derived from a deck name (FNV-1a), so re-imports land in the same deck.</summary>
    public static long DeckId(string deckName)
    {
        ulong h = 14695981039346656037;
        foreach (var b in Encoding.UTF8.GetBytes(deckName)) { h ^= b; h *= 1099511628211; }
        return (long)(h % 9_000_000_000_000) + 1_000_000_000_000;
    }

    public static string ImageFileName(SavedWord w) => $"glossa_{w.Id}.jpg";

    public static string AudioFileName(SavedWord w) => $"glossa_{w.Id}.wav";

    /// <summary>Field values in <see cref="Fields"/> order. HTML-escapes text; the context word is bolded.</summary>
    public static string[] FieldValues(SavedWord w, bool hasImage, bool hasAudio, bool reverse) =>
    [
        w.Id,
        E(w.Headword),
        E(w.Reading),
        E(w.Translation),
        E(w.Definition),
        ContextHtml(w),
        E(w.ContextTranslation),
        E(w.Level),
        E(w.PartOfSpeech),
        E(string.Join(", ", w.Synonyms)),
        E(string.Join(" · ", w.KeyForms)),
        string.Join("<br>", w.Components.Select(c => $"<b>{E(c.Part)}</b> — {E(c.Meaning)}")),
        E(w.Explanation),
        hasImage ? $"<img src=\"{ImageFileName(w)}\">" : "",
        hasAudio ? $"[sound:{AudioFileName(w)}]" : "",
        w.Language,
        E(string.Join(" · ", new[] { w.WindowTitle, w.AppExe }.Where(s => !string.IsNullOrWhiteSpace(s)))),
        reverse ? "y" : "",
    ];

    private static string ContextHtml(SavedWord w)
    {
        var ctx = w.Context ?? "";
        var len = w.Word.Length;
        if (w.ContextOffset < 0 || w.ContextOffset + len > ctx.Length) return E(ctx);
        return E(ctx[..w.ContextOffset]) + "<b>" + E(ctx.Substring(w.ContextOffset, len)) + "</b>"
               + E(ctx[(w.ContextOffset + len)..]);
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// The card picture: the saved scene scaled to at most <paramref name="maxWidth"/> with the word outlined.
    /// Returns null when the screenshot is missing.
    /// </summary>
    public static byte[]? CardImage(string dataRoot, SavedWord w, int maxWidth = 1280, int quality = 80)
    {
        if (w.ShotFile is null) return null;
        var path = Path.Combine(dataRoot, w.ShotFile);
        if (!File.Exists(path)) return null;

        using var src = SKBitmap.Decode(path);
        if (src is null) return null;
        var scale = Math.Min(1.0, maxWidth / (double)src.Width);
        var info = new SKImageInfo((int)(src.Width * scale), (int)(src.Height * scale));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        using (var img = SKImage.FromBitmap(src))
            canvas.DrawImage(img, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKFilterMode.Linear));
        if (w.WordBox is { } b)
        {
            using var pen = new SKPaint { Color = new SKColor(0xE9, 0xC4, 0x6A), IsStroke = true, StrokeWidth = 3, IsAntialias = true };
            var r = new SKRect((float)(b.Left * scale) - 4, (float)(b.Top * scale) - 3, (float)(b.Right * scale) + 4, (float)(b.Bottom * scale) + 3);
            canvas.DrawRoundRect(r, 4, 4, pen);
        }
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
