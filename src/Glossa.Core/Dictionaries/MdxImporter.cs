using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// MDict .mdx, engine versions 1.2 and 2.0: zlib or uncompressed blocks, key index encrypted or not.
/// Record-encrypted (registered) dictionaries and LZO blocks are reported as unsupported.
/// Layout as documented by the readmdict project (github.com/csarron/mdict-analysis).
/// </summary>
public sealed class MdxImporter : IDictionaryImporter
{
    private sealed record Header(Dictionary<string, string> Attributes, long DataStart)
    {
        public string? Get(string key) => Attributes.GetValueOrDefault(key);
    }

    public SourceInfo Probe(string path)
    {
        using var fs = File.OpenRead(path);
        var h = ReadHeader(fs);
        var title = h.Get("Title");
        if (string.IsNullOrWhiteSpace(title) || title.Contains("Title (No HTML code allowed)"))
            title = Path.GetFileNameWithoutExtension(path);
        var description = h.Get("Description") is { } d ? DictMarkup.ToPlain(HtmlMarkup.Convert(d), 300) : null;
        return new SourceInfo(title, null, null, description);
    }

    public void Import(string path, DictionaryPackWriter writer, string workDir, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            ImportCore(path, writer, progress, ct);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            // Offsets and sizes are read from the file; a damaged one must fail as "bad file", not as a crash.
            throw new InvalidDataException("Файл MDX повреждён или имеет неизвестную структуру.", ex);
        }
    }

    private static void ImportCore(string path, DictionaryPackWriter writer, IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        var h = ReadHeader(fs);
        var version = double.TryParse(h.Get("GeneratedByEngineVersion"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 2.0;
        var encrypted = h.Get("Encrypted") switch
        {
            null or "" or "No" => 0,
            "Yes" => 1,
            var e => int.TryParse(e, out var x) ? x : 0,
        };
        if ((encrypted & 1) != 0)
            throw new NotSupportedException("Словарь MDX зашифрован (нужен ключ регистрации) - такой импорт не поддерживается.");

        var encoding = EncodingOf(h.Get("Encoding"));
        var utf16 = encoding.CodePage == Encoding.Unicode.CodePage;
        var v2 = version >= 2.0;
        var width = v2 ? 8 : 4;

        fs.Position = h.DataStart;
        // Key section header.
        long numKeyBlocks, keyInfoSize, keyBlockSize;
        if (v2)
        {
            var head = ReadBytes(fs, 40);
            numKeyBlocks = Num(head, 0, 8);
            keyInfoSize = Num(head, 24, 8);
            keyBlockSize = Num(head, 32, 8);
            ReadBytes(fs, 4); // adler32 of the header
        }
        else
        {
            var head = ReadBytes(fs, 16);
            numKeyBlocks = Num(head, 0, 4);
            keyInfoSize = Num(head, 8, 4);
            keyBlockSize = Num(head, 12, 4);
        }

        var keyInfo = ReadBytes(fs, keyInfoSize);
        if (v2)
        {
            if ((encrypted & 2) != 0) keyInfo = DecryptKeyInfo(keyInfo);
            keyInfo = Decompress(keyInfo, -1);
        }
        var blockSizes = KeyBlockInfo(keyInfo, numKeyBlocks, v2, width, utf16);

        var keys = new List<(long Offset, string Key)>();
        foreach (var (compressed, decompressed) in blockSizes)
        {
            var block = Decompress(ReadBytes(fs, compressed), decompressed);
            ReadKeys(block, width, encoding, utf16, keys);
        }
        _ = keyBlockSize;
        keys.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Record section.
        var recHead = ReadBytes(fs, width * 4);
        var numRecordBlocks = Num(recHead, 0, width);
        var recInfo = ReadBytes(fs, Num(recHead, width * 2, width));
        var records = new List<(long Compressed, long Decompressed)>();
        for (var i = 0; i < numRecordBlocks; i++)
            records.Add((Num(recInfo, i * width * 2, width), Num(recInfo, i * width * 2 + width, width)));

        var k = 0;
        long blockStart = 0;
        for (var b = 0; b < records.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)b / records.Count);
            var (compressed, decompressed) = records[b];
            var data = Decompress(ReadBytes(fs, compressed), decompressed);
            var blockEnd = blockStart + data.Length;

            while (k < keys.Count && keys[k].Offset < blockEnd)
            {
                var start = keys[k].Offset - blockStart;
                var end = k + 1 < keys.Count && keys[k + 1].Offset < blockEnd ? keys[k + 1].Offset - blockStart : data.Length;
                if (start >= 0 && end > start)
                {
                    var text = encoding.GetString(data, (int)start, (int)(end - start)).TrimEnd('\0', '\r', '\n');
                    AddRecord(writer, keys[k].Key, text);
                }
                k++;
            }
            blockStart = blockEnd;
        }
    }

    private static void AddRecord(DictionaryPackWriter writer, string key, string text)
    {
        if (text.StartsWith("@@@LINK=", StringComparison.Ordinal))
        {
            writer.AddLink(key, text[8..].Trim());
            return;
        }
        var body = HtmlMarkup.Convert(text);
        if (body.Length == 0) return;
        writer.Add(key, null, body, [new DictKey(key, 0)]);
    }

    private static Header ReadHeader(FileStream fs)
    {
        var lenBytes = ReadBytes(fs, 4);
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
        if (len <= 0 || len > 1 << 24) throw new InvalidDataException("Это не файл MDX.");
        var text = Encoding.Unicode.GetString(ReadBytes(fs, len)).TrimEnd('\0');
        ReadBytes(fs, 4); // adler32
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "(\\w+)=\"(.*?)\"", RegexOptions.Singleline))
            attrs[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
        if (!text.StartsWith("<Dictionary", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Это не словарь MDX (возможно, MDD с ресурсами).");
        return new Header(attrs, fs.Position);
    }

    private static List<(long Compressed, long Decompressed)> KeyBlockInfo(byte[] info, long count, bool v2, int width, bool utf16)
    {
        var list = new List<(long, long)>();
        var byteWidth = v2 ? 2 : 1;
        var term = v2 ? 1 : 0;
        var i = 0;
        for (var n = 0; n < count; n++)
        {
            i += width; // entries in the block
            var headSize = byteWidth == 2 ? BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(i)) : info[i];
            i += byteWidth;
            i += (headSize + term) * (utf16 ? 2 : 1);
            var tailSize = byteWidth == 2 ? BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(i)) : info[i];
            i += byteWidth;
            i += (tailSize + term) * (utf16 ? 2 : 1);
            var compressed = Num(info, i, width);
            i += width;
            var decompressed = Num(info, i, width);
            i += width;
            list.Add((compressed, decompressed));
        }
        return list;
    }

    private static void ReadKeys(byte[] block, int width, Encoding encoding, bool utf16, List<(long, string)> keys)
    {
        var i = 0;
        while (i + width <= block.Length)
        {
            var offset = Num(block, i, width);
            i += width;
            int end;
            if (utf16)
            {
                end = i;
                while (end + 1 < block.Length && (block[end] != 0 || block[end + 1] != 0)) end += 2;
            }
            else
            {
                end = Array.IndexOf(block, (byte)0, i);
                if (end < 0) end = block.Length;
            }
            keys.Add((offset, encoding.GetString(block, i, end - i)));
            i = end + (utf16 ? 2 : 1);
        }
    }

    /// <summary>A block: 4-byte type (0 none, 1 LZO, 2 zlib), 4-byte checksum, payload.</summary>
    private static byte[] Decompress(byte[] block, long expected)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(block);
        switch (type)
        {
            case 0:
                return block[8..];
            case 2:
            {
                using var z = new ZLibStream(new MemoryStream(block, 8, block.Length - 8), CompressionMode.Decompress);
                using var ms = expected is > 0 and < 1 << 28 ? new MemoryStream((int)expected) : new MemoryStream();
                z.CopyTo(ms);
                return ms.ToArray();
            }
            case 1:
                throw new NotSupportedException("Словарь MDX сжат LZO - такой формат не поддерживается, пересохраните его с zlib.");
            default:
                throw new InvalidDataException($"Неизвестное сжатие блока MDX: {type}.");
        }
    }

    /// <summary>Key index encryption (Encrypted="2"): a byte-wise cipher keyed by RIPEMD-128 of the checksum.</summary>
    private static byte[] DecryptKeyInfo(byte[] block)
    {
        var seed = new byte[8];
        Array.Copy(block, 4, seed, 0, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(4), 0x3695);
        var key = Ripemd128.Hash(seed);
        var result = (byte[])block.Clone();
        byte previous = 0x36;
        for (var i = 8; i < block.Length; i++)
        {
            var b = block[i];
            var t = (byte)((b >> 4) | (b << 4));
            t = (byte)(t ^ previous ^ ((i - 8) & 0xff) ^ key[(i - 8) % key.Length]);
            previous = b;
            result[i] = t;
        }
        return result;
    }

    private static long Num(byte[] data, int at, int width) =>
        width == 8 ? (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

    private static long Num(byte[] data, long at, int width) => Num(data, (int)at, width);

    /// <summary>Sizes come from the file itself: anything beyond its end means a damaged dictionary.</summary>
    private static byte[] ReadBytes(Stream s, long count)
    {
        if (count < 0 || count > s.Length - s.Position || count > int.MaxValue)
            throw new InvalidDataException("Файл MDX повреждён: размер блока выходит за конец файла.");
        var buf = new byte[count];
        s.ReadExactly(buf);
        return buf;
    }

    private static Encoding EncodingOf(string? name)
    {
        switch ((name ?? "").Trim().ToUpperInvariant())
        {
            case "" or "UTF-8" or "UTF8": return Encoding.UTF8;
            case "UTF-16" or "UTF16": return Encoding.Unicode;
            case "GBK" or "GB2312" or "GB18030":
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(54936);
            case "BIG5" or "BIG-5":
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(950);
            default:
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(name!);
        }
    }
}

/// <summary>RIPEMD-128 (Dobbertin, Bosselaers, Preneel). .NET has no implementation; MDX needs it for key indexes.</summary>
public static class Ripemd128
{
    private static readonly int[] R =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
    ];

    private static readonly int[] Rp =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
    ];

    private static readonly int[] S =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
    ];

    private static readonly int[] Sp =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
    ];

    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476;

        var padded = new byte[(message.Length + 8) / 64 * 64 + 64];
        message.CopyTo(padded);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), (ulong)message.Length * 8);

        Span<uint> x = stackalloc uint[16];
        for (var block = 0; block < padded.Length; block += 64)
        {
            for (var i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4));

            uint a = h0, b = h1, c = h2, d = h3;
            uint ap = h0, bp = h1, cp = h2, dp = h3;
            for (var j = 0; j < 64; j++)
            {
                var round = j >> 4;
                var t = uint.RotateLeft(a + F(round, b, c, d) + x[R[j]] + K(round), S[j]);
                a = d; d = c; c = b; b = t;
                t = uint.RotateLeft(ap + F(3 - round, bp, cp, dp) + x[Rp[j]] + Kp(round), Sp[j]);
                ap = dp; dp = cp; cp = bp; bp = t;
            }
            var tmp = h1 + c + dp;
            h1 = h2 + d + ap;
            h2 = h3 + a + bp;
            h3 = h0 + b + cp;
            h0 = tmp;
        }

        var result = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), h0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), h1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), h2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), h3);
        return result;
    }

    private static uint F(int round, uint x, uint y, uint z) => round switch
    {
        0 => x ^ y ^ z,
        1 => (x & y) | (~x & z),
        2 => (x | ~y) ^ z,
        _ => (x & z) | (y & ~z),
    };

    private static uint K(int round) => round switch { 0 => 0x00000000, 1 => 0x5A827999, 2 => 0x6ED9EBA1, _ => 0x8F1BBCDC };

    private static uint Kp(int round) => round switch { 0 => 0x50A28BE6, 1 => 0x5C4DD124, 2 => 0x6D703EF3, _ => 0x00000000 };
}
