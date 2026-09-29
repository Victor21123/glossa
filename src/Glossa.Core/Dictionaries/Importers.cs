using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Glossa.Core.Text;

namespace Glossa.Core.Dictionaries;

/// <summary>What a source file says about itself; null languages are guessed from the content.</summary>
public sealed record SourceInfo(string Title, string? SourceLanguage, string? TargetLanguage, string? Description = null);

public interface IDictionaryImporter
{
    SourceInfo Probe(string path);

    void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Guesses dictionary languages from the scripts of headwords and bodies.</summary>
public sealed class LanguageStats
{
    private long _kana, _han, _latin, _cyrillic, _hangul;
    private long _bodyLatin, _bodyCyrillic, _bodyCjk;
    private int _samples;

    public void Add(string headword, string body)
    {
        if (_samples++ > 20000) return;
        foreach (var c in headword)
        {
            switch (Scripts.Of(c))
            {
                case Script.Kana: _kana++; break;
                case Script.Han: _han++; break;
                case Script.Latin: _latin++; break;
                case Script.Cyrillic: _cyrillic++; break;
                case Script.Hangul: _hangul++; break;
            }
        }
        foreach (var c in body.Length > 400 ? body[..400] : body)
        {
            switch (Scripts.Of(c))
            {
                case Script.Latin: _bodyLatin++; break;
                case Script.Cyrillic: _bodyCyrillic++; break;
                case Script.Kana or Script.Han or Script.Hangul: _bodyCjk++; break;
            }
        }
    }

    public string Source
    {
        get
        {
            var cjk = _kana + _han;
            if (cjk > _latin + _cyrillic)
                return _kana > cjk * 0.05 ? "ja" : "zh";
            if (_hangul > _latin + _cyrillic) return "ko";
            return _cyrillic > _latin ? "ru" : "en";
        }
    }

    public string Target => _bodyCyrillic > _bodyLatin ? "ru" : _bodyCjk > _bodyLatin ? Source : "en";
}

/// <summary>ABBYY Lingvo DSL (.dsl, .dsl.dz) in UTF-16, UTF-8 or Windows-1251.</summary>
public sealed class DslImporter : IDictionaryImporter
{
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en", ["Russian"] = "ru", ["Japanese"] = "ja", ["Chinese"] = "zh", ["ChinesePRC"] = "zh",
        ["ChineseTaiwan"] = "zh", ["Korean"] = "ko", ["German"] = "de", ["French"] = "fr", ["Spanish"] = "es",
    };

    private static readonly Regex Pinyin = new(@"^[a-zA-ZāáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜüÜĀÁǍÀĒÉĚÈĪÍǏÌŌÓǑÒŪÚǓÙ’',·\s\-]+$", RegexOptions.Compiled);

    public SourceInfo Probe(string path)
    {
        string? name = null, from = null, to = null;
        using var reader = Open(path);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (line[0] != '#') break;
            var value = HeaderValue(line);
            if (line.StartsWith("#NAME", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (line.StartsWith("#INDEX_LANGUAGE", StringComparison.OrdinalIgnoreCase)) from = Code(value);
            else if (line.StartsWith("#CONTENTS_LANGUAGE", StringComparison.OrdinalIgnoreCase)) to = Code(value);
        }
        return new SourceInfo(name ?? DslBaseName(path), from, to);
    }

    public void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var reader = Open(path, file);
        var length = Math.Max(1, file.Length);
        var chinese = writer.Meta.SourceLanguage == "zh";

        var headwords = new List<string>();
        var body = new List<string>();
        var inBody = false;
        var n = 0;

        void Flush()
        {
            if (headwords.Count > 0 && body.Count > 0) AddCard(writer, headwords, body, chinese);
            headwords.Clear();
            body.Clear();
            inBody = false;
        }

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (line[0] == '#' && headwords.Count == 0 && !inBody) continue;
            if (line[0] is ' ' or '\t')
            {
                inBody = true;
                body.Add(line);
                continue;
            }
            if (inBody) Flush();
            headwords.Add(line);
            if (++n % 20000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)file.Position / length);
            }
        }
        Flush();
    }

    private static void AddCard(DictionaryPackWriter writer, List<string> rawHeadwords, List<string> rawBody, bool chinese)
    {
        var keys = new List<DictKey>();
        string? display = null;
        foreach (var raw in rawHeadwords)
        {
            var (shown, variants) = ParseHeadword(raw);
            display ??= shown;
            foreach (var v in variants) keys.Add(new DictKey(v, 0));
        }
        if (display is null || keys.Count == 0) return;

        string? reading = null;
        var first = 0;
        // БКРС and similar Chinese DSLs put untagged pinyin on the first body line.
        if (chinese && rawBody.Count > 1)
        {
            var line = rawBody[0].Trim();
            if (line.Length > 0 && !line.Contains('[') && Pinyin.IsMatch(line))
            {
                reading = line;
                first = 1;
            }
        }

        var w = new MarkupBuilder();
        for (var i = first; i < rawBody.Count; i++)
        {
            BodyLine(w, rawBody[i], display);
            w.NewLine();
        }
        var body = w.Finish();
        if (body.Length == 0) return;
        writer.Add(display, reading, body, keys);
    }

    /// <summary>One DSL body line into markup. [mN] sets the indent and, mid-line, starts a new line.</summary>
    internal static void BodyLine(MarkupBuilder w, string line, string headword)
    {
        var s = line.AsSpan().TrimStart([' ', '\t']);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                w.Text(s[++i].ToString());
                continue;
            }
            if (c == '~')
            {
                w.Text(headword);
                continue;
            }
            if (c == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                var end = s[i..].IndexOf("}}");
                if (end < 0) break;
                i += end + 1;
                continue;
            }
            if (c == '<' && i + 1 < s.Length && s[i + 1] == '<')
            {
                var end = s[(i + 2)..].IndexOf(">>");
                if (end >= 0)
                {
                    w.OpenTag("ref");
                    w.Text(s.Slice(i + 2, end).ToString());
                    w.CloseTag("ref");
                    i += end + 3;
                    continue;
                }
            }
            if (c == '[')
            {
                var end = s[i..].IndexOf(']');
                if (end > 1)
                {
                    var tag = s.Slice(i + 1, end - 1).ToString();
                    i += end;
                    if (tag == "s")
                    {
                        // Media reference: skip to [/s].
                        var close = s[(i + 1)..].IndexOf("[/s]");
                        i = close < 0 ? s.Length : i + 1 + close + 3;
                        continue;
                    }
                    Tag(w, tag);
                    continue;
                }
            }
            w.Text(c.ToString());
        }
    }

    private static void Tag(MarkupBuilder w, string tag)
    {
        var closing = tag.StartsWith('/');
        var name = (closing ? tag[1..] : tag).Trim();
        var space = name.IndexOf(' ');
        if (space > 0) name = name[..space];

        if (!closing && name.Length >= 1 && name[0] == 'm' && (name.Length == 1 || char.IsDigit(name[1])))
        {
            w.NewLine();
            w.Indent = name.Length > 1 ? Math.Min(name[1] - '0', 3) : 0;
            return;
        }
        var mapped = name switch
        {
            "b" or "i" or "u" or "p" or "ex" or "ref" or "c" => name,
            "com" => "c",
            _ => null,
        };
        if (mapped is null) return;
        if (closing) w.CloseTag(mapped);
        else w.OpenTag(mapped);
    }

    /// <summary>Display form and search variants: "{...}" is shown but not indexed, "(...)" is optional.</summary>
    internal static (string Display, List<string> Variants) ParseHeadword(string raw)
    {
        var display = new StringBuilder();
        var variants = new List<StringBuilder> { new() };
        var inBraces = 0;
        var optional = -1; // index in variants list where the optional group started
        var optionalGroups = 0;
        var s = raw.Trim();

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                c = s[++i];
                display.Append(c);
                if (inBraces == 0) foreach (var v in variants) v.Append(c);
                continue;
            }
            if (c == '{') { inBraces++; continue; }
            if (c == '}') { inBraces = Math.Max(0, inBraces - 1); continue; }
            if (inBraces > 0)
            {
                // Tags inside braces are formatting, text is shown only.
                if (c == '[')
                {
                    var end = s.IndexOf(']', i);
                    if (end > 0) { i = end; continue; }
                }
                display.Append(c);
                continue;
            }
            if (c == '(' && optionalGroups < 2)
            {
                display.Append(c);
                // Duplicate current variants: one set continues with the optional text, the other skips it.
                optional = variants.Count;
                variants.AddRange(variants.Select(v => new StringBuilder(v.ToString())).ToList());
                optionalGroups++;
                continue;
            }
            if (c == ')' && optional >= 0)
            {
                display.Append(c);
                optional = -1;
                continue;
            }
            display.Append(c);
            if (optional >= 0)
            {
                for (var k = optional; k < variants.Count; k++) variants[k].Append(c);
            }
            else
            {
                foreach (var v in variants) v.Append(c);
            }
        }
        var list = variants.Select(v => v.ToString().Trim()).Where(v => v.Length > 0).Distinct().ToList();
        return (display.ToString().Trim(), list);
    }

    private static string? HeaderValue(string line)
    {
        var q1 = line.IndexOf('"');
        var q2 = line.LastIndexOf('"');
        return q1 >= 0 && q2 > q1 ? line[(q1 + 1)..q2] : null;
    }

    private static string? Code(string? language) =>
        language is not null && LanguageCodes.TryGetValue(language.Trim(), out var code) ? code : null;

    private static bool IsGzip(string path) =>
        path.EndsWith(".dz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    private static string DslBaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ".dsl.dz", ".dsl.gz", ".dsl", ".dz", ".gz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }

    private static StreamReader Open(string path, FileStream? file = null)
    {
        var encoding = DetectEncoding(path);
        Stream stream = file ?? File.OpenRead(path);
        if (IsGzip(path)) stream = new GZipStream(stream, CompressionMode.Decompress);
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
    }

    internal static Encoding DetectEncoding(string path)
    {
        var buf = new byte[1 << 16];
        int n;
        using (Stream s = IsGzip(path) ? new GZipStream(File.OpenRead(path), CompressionMode.Decompress) : File.OpenRead(path))
        {
            n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        }
        var head = buf.AsSpan(0, n);
        if (head.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (head.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8;
        var zeros = 0;
        for (var i = 1; i < Math.Min(n, 4096); i += 2) if (head[i] == 0) zeros++;
        if (zeros > Math.Min(n, 4096) / 8) return Encoding.Unicode;

        // Cut at the last newline so a multi-byte character is not split, then check UTF-8 strictly.
        var cut = head.LastIndexOf((byte)'\n');
        try
        {
            new UTF8Encoding(false, true).GetString(cut > 0 ? head[..cut] : head);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }
}

/// <summary>
/// Warodai (warodai.ru) in its source text form: blank-line separated entries whose first line is
/// "かな, かな【漢字･漢字】(транскрипция) [помета]〔id〕", body lines with &lt;i&gt; and &lt;a href&gt;.
/// </summary>
public sealed class WarodaiImporter : IDictionaryImporter
{
    private static readonly Regex Header = new(
        @"^(?<kana>[^【(〔\[]+?)\s*(?:【(?<kanji>[^】]*)】)?\s*\((?<tr>[^)]*)\)\s*(?:\[(?<note>[^\]]*)\])?\s*〔(?<id>[^〕]+)〕\s*$",
        RegexOptions.Compiled);

    // "得/エ/る": the reading of the kanji before it, shown dimmed in parentheses.
    private static readonly Regex InlineReading = new(@"/([぀-ヿー]+)/", RegexOptions.Compiled);
    private static readonly char[] Separators = [',', '･', '・'];

    public SourceInfo Probe(string path) =>
        new("Warodai - большой японско-русский словарь", "ja", "ru", "warodai.ru, CC BY-NC-ND 3.0");

    public void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct)
    {
        var text = File.ReadAllText(path, Encoding.Unicode).Replace("\r\n", "\n");
        var blocks = text.Split("\n\n");
        for (var b = 0; b < blocks.Length; b++)
        {
            if (b % 5000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)b / blocks.Length);
            }
            var block = blocks[b].Trim('\n', '﻿');
            if (block.Length == 0 || block[0] == '*') continue;
            var nl = block.IndexOf('\n');
            var head = nl < 0 ? block : block[..nl];
            var m = Header.Match(head);
            if (!m.Success) continue;

            var kana = Split(m.Groups["kana"].Value);
            var kanji = m.Groups["kanji"].Success ? Split(m.Groups["kanji"].Value) : [];
            if (kana.Count == 0) continue;

            var keys = new List<DictKey>();
            var kanaRank = kanji.Count > 0 ? 1 : 0;
            foreach (var k in kanji) keys.Add(new DictKey(k.Trim('…'), k.Contains('…') ? 2 : 0));
            foreach (var k in kana) keys.Add(new DictKey(k.Trim('…'), k.Contains('…') ? 2 : kanaRank));

            var bodyHtml = nl < 0 ? "" : block[(nl + 1)..];
            var body = InlineReading.Replace(HtmlMarkup.Convert(bodyHtml, newlinesAreBreaks: true), "[c]($1)[/c]");
            if (m.Groups["note"].Success) body = $"[p]{DictMarkup.Escape(m.Groups["note"].Value)}[/p]\n" + body;
            if (body.Length == 0) continue;

            var headword = kanji.Count > 0 ? $"{kana[0]}【{string.Join("・", kanji)}】" : kana[0];
            writer.Add(headword, kana[0], body, keys);
        }
    }

    /// <summary>"ああ, あゝI" → ああ, あゝ; homonym numerals (I, II…) are dropped.</summary>
    private static List<string> Split(string s) =>
        s.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.TrimEnd('I', 'V', 'X').Trim())
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();
}

/// <summary>StarDict (.ifo + .idx[.gz] + .dict[.dz] + optional .syn).</summary>
public sealed class StarDictImporter : IDictionaryImporter
{
    public SourceInfo Probe(string path)
    {
        var ifo = ReadIfo(path);
        return new SourceInfo(ifo.GetValueOrDefault("bookname") ?? Path.GetFileNameWithoutExtension(path), null, null,
            ifo.GetValueOrDefault("description"));
    }

    public void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct)
    {
        var ifo = ReadIfo(path);
        var sameType = ifo.GetValueOrDefault("sametypesequence");
        var offset64 = ifo.GetValueOrDefault("idxoffsetbits") == "64";
        var basePath = path[..^4];

        var idx = ReadMaybeGz(basePath + ".idx");
        var words = new List<(string Word, long Offset, int Size)>();
        for (var p = 0; p < idx.Length;)
        {
            var end = Array.IndexOf(idx, (byte)0, p);
            if (end < 0) break;
            var word = Encoding.UTF8.GetString(idx, p, end - p);
            p = end + 1;
            if (p + (offset64 ? 12 : 8) > idx.Length) throw new InvalidDataException("Файл StarDict .idx повреждён (обрезан).");
            long off;
            if (offset64) { off = (long)BinaryPrimitives.ReadUInt64BigEndian(idx.AsSpan(p)); p += 8; }
            else { off = BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(p)); p += 4; }
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(p));
            p += 4;
            words.Add((word, off, size));
        }

        var synonyms = new Dictionary<int, List<string>>();
        var synPath = File.Exists(basePath + ".syn") ? basePath + ".syn" : File.Exists(basePath + ".syn.gz") ? basePath + ".syn.gz" : null;
        if (synPath is not null)
        {
            var syn = ReadMaybeGz(synPath.EndsWith(".gz") ? synPath[..^3] : synPath);
            for (var p = 0; p < syn.Length;)
            {
                var end = Array.IndexOf(syn, (byte)0, p);
                if (end < 0 || end + 4 > syn.Length) break;
                var word = Encoding.UTF8.GetString(syn, p, end - p);
                var index = (int)BinaryPrimitives.ReadUInt32BigEndian(syn.AsSpan(end + 1));
                p = end + 5;
                if (!synonyms.TryGetValue(index, out var list)) synonyms[index] = list = [];
                list.Add(word);
            }
        }

        var dictPath = OpenDict(basePath, workDir, out var temp);
        try
        {
            using var dict = File.OpenRead(dictPath);
            var order = Enumerable.Range(0, words.Count).OrderBy(i => words[i].Offset).ToList();
            var buf = Array.Empty<byte>();
            for (var n = 0; n < order.Count; n++)
            {
                if (n % 20000 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report((double)n / order.Count);
                }
                var i = order[n];
                var (word, off, size) = words[i];
                if (size <= 0 || off < 0 || off + size > dict.Length) continue; // damaged index entry
                if (buf.Length < size) buf = new byte[Math.Max(size, buf.Length * 2)];
                dict.Position = off;
                dict.ReadExactly(buf, 0, size);
                var body = Body(buf.AsSpan(0, size), sameType);
                if (body.Length == 0) continue;

                var keys = new List<DictKey> { new(word, 0) };
                if (synonyms.TryGetValue(i, out var syn)) keys.AddRange(syn.Select(s => new DictKey(s, 1)));
                writer.Add(word, null, body, keys);
            }
        }
        finally
        {
            if (temp) File.Delete(dictPath);
        }
    }

    private static string Body(ReadOnlySpan<byte> data, string? sameType)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(sameType))
        {
            var p = 0;
            for (var t = 0; t < sameType.Length && p <= data.Length; t++)
            {
                var type = sameType[t];
                var last = t == sameType.Length - 1;
                if (char.IsLower(type))
                {
                    var nul = last ? -1 : data[p..].IndexOf((byte)0);
                    var end = nul < 0 ? data.Length : p + nul;
                    parts.Add(Field(type, data[p..end]));
                    p = end + 1;
                }
                else
                {
                    var size = last ? data.Length - p : (int)BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
                    if (!last) p += 4;
                    p += size;
                }
            }
        }
        else
        {
            var p = 0;
            while (p < data.Length)
            {
                var type = (char)data[p++];
                if (char.IsLower(type))
                {
                    var len = data[p..].IndexOf((byte)0);
                    var end = len < 0 ? data.Length : p + len;
                    parts.Add(Field(type, data[p..end]));
                    p = end + 1;
                }
                else
                {
                    if (p + 4 > data.Length) break;
                    var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
                    p += 4 + size;
                }
            }
        }
        return string.Join("\n", parts.Where(x => x.Length > 0));
    }

    private static string Field(char type, ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return type switch
        {
            'h' or 'g' => HtmlMarkup.Convert(text),
            'x' => HtmlMarkup.Convert(text, xdxf: true, newlinesAreBreaks: true), // XDXF keeps its line breaks
            't' or 'y' => $"[c]\\[{DictMarkup.Escape(text)}\\][/c]",
            'm' or 'l' or 'k' or 'w' => HtmlMarkup.Convert(System.Net.WebUtility.HtmlEncode(text), newlinesAreBreaks: true),
            _ => "",
        };
    }

    private static Dictionary<string, string> ReadIfo(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    private static byte[] ReadMaybeGz(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        if (!File.Exists(path + ".gz")) throw new FileNotFoundException("StarDict file not found", path);
        using var gz = new GZipStream(File.OpenRead(path + ".gz"), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The .dict file itself, or a .dict.dz unpacked to the work folder for random access.</summary>
    private static string OpenDict(string basePath, string workDir, out bool temp)
    {
        temp = false;
        if (File.Exists(basePath + ".dict")) return basePath + ".dict";
        var dz = basePath + ".dict.dz";
        if (!File.Exists(dz)) throw new FileNotFoundException("StarDict .dict not found", basePath + ".dict");
        Directory.CreateDirectory(workDir);
        var tmp = Path.Combine(workDir, Path.GetFileName(basePath) + $".{Guid.NewGuid():N}.dict");
        using (var gz = new GZipStream(File.OpenRead(dz), CompressionMode.Decompress))
        using (var outFile = File.Create(tmp))
            gz.CopyTo(outFile);
        temp = true;
        return tmp;
    }
}
