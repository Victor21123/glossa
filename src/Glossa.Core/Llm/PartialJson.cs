using System.Text;

namespace Glossa.Core.Llm;

/// <summary>
/// Reads the top-level fields of a JSON object that is still being streamed.
/// Complete string values, string arrays and nested values are returned as soon as they close;
/// the string currently being written is returned as a partial value so the UI can type it out.
/// </summary>
public static class PartialJson
{
    public sealed record Snapshot(
        IReadOnlyDictionary<string, string> Strings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> StringArrays,
        IReadOnlyDictionary<string, string> RawValues,
        string? StreamingKey);

    public static Snapshot Read(string buffer)
    {
        var strings = new Dictionary<string, string>();
        var arrays = new Dictionary<string, IReadOnlyList<string>>();
        var raws = new Dictionary<string, string>();
        string? streaming = null;

        var i = SkipWs(buffer, 0);
        if (i >= buffer.Length || buffer[i] != '{') return new(strings, arrays, raws, null);
        i++;

        while (true)
        {
            i = SkipWs(buffer, i);
            if (i >= buffer.Length || buffer[i] == '}') break;
            if (buffer[i] == ',') { i++; continue; }
            if (buffer[i] != '"') break;

            var (key, keyEnd, keyDone) = ReadString(buffer, i);
            if (!keyDone) break;
            i = SkipWs(buffer, keyEnd);
            if (i >= buffer.Length || buffer[i] != ':') break;
            i = SkipWs(buffer, i + 1);
            if (i >= buffer.Length) break;

            switch (buffer[i])
            {
                case '"':
                {
                    var (value, end, done) = ReadString(buffer, i);
                    strings[key] = value;
                    if (!done) { streaming = key; goto finish; }
                    i = end;
                    break;
                }
                case '[':
                {
                    var (items, end, done, raw) = ReadArray(buffer, i);
                    if (items is not null) arrays[key] = items;
                    if (!done) { streaming = key; goto finish; }
                    if (items is null) raws[key] = raw;
                    i = end;
                    break;
                }
                default:
                {
                    var end = SkipValue(buffer, i);
                    if (end < 0) { streaming = key; goto finish; }
                    raws[key] = buffer[i..end].Trim();
                    i = end;
                    break;
                }
            }
        }
        finish:
        return new(strings, arrays, raws, streaming);
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>Reads a JSON string starting at the opening quote. Returns the decoded text so far.</summary>
    private static (string Value, int End, bool Done) ReadString(string s, int start)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') return (sb.ToString(), i + 1, true);
            if (c == '\\')
            {
                if (i + 1 >= s.Length) break;
                var e = s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); i += 2; continue;
                    case 't': sb.Append('\t'); i += 2; continue;
                    case 'r': sb.Append('\r'); i += 2; continue;
                    case 'b': sb.Append('\b'); i += 2; continue;
                    case 'f': sb.Append('\f'); i += 2; continue;
                    case 'u':
                        if (i + 6 > s.Length) return (sb.ToString(), s.Length, false);
                        if (int.TryParse(s.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                            sb.Append((char)code);
                        i += 6;
                        continue;
                    default: sb.Append(e); i += 2; continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), s.Length, false);
    }

    /// <summary>Reads an array; returns its strings when it only holds strings, otherwise null plus the raw text.</summary>
    private static (List<string>? Items, int End, bool Done, string Raw) ReadArray(string s, int start)
    {
        var items = new List<string>();
        var onlyStrings = true;
        var i = start + 1;
        while (true)
        {
            i = SkipWs(s, i);
            if (i >= s.Length) return (onlyStrings ? items : null, s.Length, false, s[start..]);
            var c = s[i];
            if (c == ']') return (onlyStrings ? items : null, i + 1, true, s[start..(i + 1)]);
            if (c == ',') { i++; continue; }
            if (c == '"')
            {
                var (v, end, done) = ReadString(s, i);
                if (!done) return (onlyStrings ? items : null, s.Length, false, s[start..]);
                items.Add(v);
                i = end;
                continue;
            }
            onlyStrings = false;
            var e = SkipValue(s, i);
            if (e < 0) return (null, s.Length, false, s[start..]);
            i = e;
        }
    }

    /// <summary>Index just past a complete value (object, array, literal, number), or -1 if it is unfinished.</summary>
    private static int SkipValue(string s, int start)
    {
        var depth = 0;
        var i = start;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                var (_, end, done) = ReadString(s, i);
                if (!done) return -1;
                i = end;
                if (depth == 0) return i;
                continue;
            }
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']')
            {
                if (depth == 0) return i; // closing bracket of the parent
                depth--;
                if (depth == 0) return i + 1;
            }
            else if (c == ',' && depth == 0) return i;
            i++;
        }
        return -1;
    }
}
