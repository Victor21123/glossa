using System.Globalization;

namespace Glossa.Core.Ocr;

/// <summary>
/// The model's readings of pieces of screen kept for the next lookup of the same place (a menu looked up again needs no
/// second look). Starts over when full: the pieces of one session repeat, old ones rarely come back.
/// </summary>
public sealed class ReadingMemory(int limit = 100)
{
    private readonly Dictionary<string, string> _readings = [];

    public bool TryGet(string key, out string value)
    {
        lock (_readings)
        {
            if (_readings.TryGetValue(key, out var found)) { value = found; return true; }
            value = "";
            return false;
        }
    }

    public void Set(string key, string value)
    {
        lock (_readings)
        {
            if (_readings.Count >= limit && !_readings.ContainsKey(key)) _readings.Clear();
            _readings[key] = value;
        }
    }

    /// <summary>A key of its parts, numbers written the same way whatever the culture.</summary>
    public static string Key(params object[] parts) =>
        string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)));
}
