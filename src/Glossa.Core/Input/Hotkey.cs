using System.Text.RegularExpressions;

namespace Glossa.Core.Input;

[Flags]
public enum KeyMods
{
    /// <summary>No modifier.</summary>
    None = 0,
    /// <summary>Either Ctrl key.</summary>
    Ctrl = 1,
    /// <summary>Either Alt key.</summary>
    Alt = 2,
    /// <summary>Either Shift key.</summary>
    Shift = 4,
    /// <summary>Either Windows key.</summary>
    Win = 8,
}

/// <summary>What a recorded or stored key means for the user.</summary>
public enum KeyVerdict
{
    /// <summary>Fine as is.</summary>
    Ok,
    /// <summary>A key that types (a letter, Enter, an arrow): works, but stops typing everywhere while Glossa runs.</summary>
    Typing,
    /// <summary>One of the card's own keys (Esc, S, P, Tab, F2, Space): the card would eat it.</summary>
    CardKey,
    /// <summary>Only Ctrl, Alt, Shift or Win.</summary>
    ModifiersOnly,
}

/// <summary>
/// A system-wide key: modifiers plus exactly one other key. The key is a WPF <c>Key</c> name ("Q", "F8", "Multiply";
/// digits are written "5"), so this stays free of WPF. Stored as "Ctrl+Alt+G", a single key is just "F8".
/// </summary>
public sealed record KeySpec(KeyMods Mods, string Key)
{
    /// <summary>The card's own keys: registered system-wide only while the card is open.</summary>
    public static readonly IReadOnlyList<string> CardKeys = ["Escape", "S", "P", "Tab", "F2", "Space"];

    private static readonly string[] NonTyping = ["Pause", "Scroll", "Snapshot", "Apps", "Sleep", "NumLock", "Capital", "CapsLock", "Play", "Zoom"];

    /// <summary>Media, browser, volume and launch keys do not type either.</summary>
    private static readonly string[] NonTypingPrefixes = ["Volume", "Media", "Browser", "Launch", "NextTrack", "PreviousTrack"];

    private static readonly Regex FunctionKey = new(@"^[fF](\d{1,2})$", RegexOptions.Compiled);

    /// <summary>"Ctrl+Alt+G": modifiers in a fixed order, then the key.</summary>
    public string Format()
    {
        var parts = new List<string>();
        if (Mods.HasFlag(KeyMods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(KeyMods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(KeyMods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(KeyMods.Win)) parts.Add("Win");
        if (Key.Length > 0) parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>Parses "Alt+Q", "ctrl+alt+g", "F8"; null when empty, without a key or with two plain keys.</summary>
    public static KeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = KeyMods.None;
        string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ModOf(part) is var m and not KeyMods.None) mods |= m;
            else if (key is null) key = NormalizeKey(part);
            else return null;
        }
        return key is null ? null : new KeySpec(mods, key);
    }

    /// <summary>Does this key need a warning, or can it not be used at all.</summary>
    public KeyVerdict Check()
    {
        if (Key.Length == 0) return KeyVerdict.ModifiersOnly;
        // Only the bare key clashes with the card: the card registers it without modifiers.
        if (Mods == KeyMods.None && CardKeys.Contains(Key, StringComparer.OrdinalIgnoreCase)) return KeyVerdict.CardKey;
        if ((Mods & (KeyMods.Ctrl | KeyMods.Alt | KeyMods.Win)) != 0) return KeyVerdict.Ok;
        if (FunctionKey.IsMatch(Key) || NonTyping.Contains(Key, StringComparer.OrdinalIgnoreCase)
            || NonTypingPrefixes.Any(p => Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return KeyVerdict.Ok;
        return KeyVerdict.Typing;
    }

    /// <summary>The modifier a name stands for (Ctrl, LeftCtrl, RWin ...), None for any other key.</summary>
    internal static KeyMods ModOf(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "leftctrl" or "rightctrl" => KeyMods.Ctrl,
        "alt" or "leftalt" or "rightalt" => KeyMods.Alt,
        "shift" or "leftshift" or "rightshift" => KeyMods.Shift,
        "win" or "lwin" or "rwin" => KeyMods.Win,
        _ => KeyMods.None,
    };

    /// <summary>One spelling per key: letters and F-keys upper case, D5 as 5, the rest as typed with a capital first letter.</summary>
    internal static string NormalizeKey(string name)
    {
        if (name.Length == 1) return name.ToUpperInvariant();
        if (name.Length == 2 && (name[0] is 'D' or 'd') && char.IsDigit(name[1])) return name[1].ToString();
        if (FunctionKey.IsMatch(name)) return "F" + FunctionKey.Match(name).Groups[1].Value;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}

/// <summary>How a recording ended.</summary>
public enum KeyRecordKind
{
    /// <summary>A key or combination was recorded.</summary>
    Taken,
    /// <summary>A side mouse button (x1 or x2) was pressed.</summary>
    Mouse,
    /// <summary>Esc alone.</summary>
    Cancelled,
    /// <summary>Nothing usable was pressed, see <see cref="RecordRefusal"/>.</summary>
    Refused,
}

/// <summary>Why a recording was refused.</summary>
public enum RecordRefusal
{
    /// <summary>Not refused.</summary>
    None,
    /// <summary>Only Ctrl, Alt, Shift or Win.</summary>
    ModifiersOnly,
    /// <summary>Two plain keys together (not supported yet).</summary>
    TwoKeys,
}

/// <summary>Result of <see cref="KeyRecorder"/>: a key (with its verdict), a side mouse button, a cancel or a refusal.</summary>
/// <param name="Kind">How the recording ended.</param>
/// <param name="Spec">The recorded key (Taken only).</param>
/// <param name="Verdict">What the key means for the user (Taken only).</param>
/// <param name="MouseButton">"x1" or "x2" (Mouse only).</param>
/// <param name="Refusal">Why it was refused (Refused only).</param>
public sealed record KeyRecord(KeyRecordKind Kind, KeySpec? Spec = null, KeyVerdict Verdict = KeyVerdict.Ok, string? MouseButton = null,
    RecordRefusal Refusal = RecordRefusal.None);

/// <summary>
/// Records the key the user presses, one key or a combination: it ends when everything is released, so a lone key
/// and "Ctrl+key" in any release order both work. Fed with WPF key names; the window does the WPF part.
/// </summary>
public sealed class KeyRecorder
{
    private readonly List<string> _held = [];
    private KeyMods _mods;
    private readonly List<string> _plain = [];

    /// <summary>The keys held right now as "Ctrl+Alt+G", for the live caps ("" when none).</summary>
    public string Held
    {
        get
        {
            var mods = _held.Aggregate(KeyMods.None, (m, k) => m | KeySpec.ModOf(k));
            var plain = _held.Where(k => KeySpec.ModOf(k) == KeyMods.None).Select(KeySpec.NormalizeKey);
            return new KeySpec(mods, string.Join("+", plain)).Format();
        }
    }

    /// <summary>A key went down (auto-repeat is ignored); never ends the recording.</summary>
    public KeyRecord? Down(string key)
    {
        Press(key);
        return null;
    }

    /// <summary>A key went up: the recording ends once nothing is held. A release with no press (PrintScreen) counts.</summary>
    public KeyRecord? Up(string key)
    {
        if (!_held.Contains(key))
        {
            // PrintScreen sends no key-down; any other lone release is left over from before the recording (the Enter that
            // clicked the button, the Alt that woke the window).
            if (key != "Snapshot") return null;
            Press(key);
        }
        _held.Remove(key);
        return _held.Count == 0 ? Finish() : null;
    }

    /// <summary>A mouse button went down: only the side buttons x1 and x2 are taken.</summary>
    public KeyRecord? Mouse(string button)
    {
        if (button is not ("x1" or "x2")) return null;
        Reset();
        return new KeyRecord(KeyRecordKind.Mouse, MouseButton: button);
    }

    private void Press(string key)
    {
        if (_held.Contains(key)) return;
        _held.Add(key);
        var mod = KeySpec.ModOf(key);
        if (mod != KeyMods.None) _mods |= mod;
        else if (!_plain.Contains(KeySpec.NormalizeKey(key))) _plain.Add(KeySpec.NormalizeKey(key));
    }

    private KeyRecord Finish()
    {
        var mods = _mods;
        var plain = _plain.ToList();
        Reset();
        if (plain.Count == 0) return new KeyRecord(KeyRecordKind.Refused, Refusal: RecordRefusal.ModifiersOnly);
        if (plain.Count > 1) return new KeyRecord(KeyRecordKind.Refused, Refusal: RecordRefusal.TwoKeys);
        if (mods == KeyMods.None && plain[0] == "Escape") return new KeyRecord(KeyRecordKind.Cancelled);
        var spec = new KeySpec(mods, plain[0]);
        return new KeyRecord(KeyRecordKind.Taken, spec, spec.Check());
    }

    private void Reset()
    {
        _held.Clear();
        _plain.Clear();
        _mods = KeyMods.None;
    }
}
