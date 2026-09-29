namespace Glossa.Core.Text;

/// <summary>
/// Живой перевод, frame by frame: when to recognize the text (the picture settled after a change: a new line typed
/// out; or every couple of seconds in a scene that never stops moving), and which dialogue line is new. The same line
/// read again, and text already in the user's language, are not translated twice.
/// </summary>
public sealed class LiveTextWatcher
{
    private byte[]? _last;
    private byte[]? _recognized;
    private DateTime _lastRecognized = DateTime.MinValue;
    private string? _lastKey;

    /// <summary>A scene that keeps moving is still read this often.</summary>
    public TimeSpan ForceEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A new frame of the game (its <see cref="ScreenFingerprint"/>): whether to recognize its text now.</summary>
    public bool ShouldRecognize(byte[] print, DateTime now)
    {
        var moving = ScreenFingerprint.Differs(print, _last);
        _last = print;
        if (moving && now - _lastRecognized < ForceEvery) return false; // still being drawn: wait until it settles
        if (!moving && !ScreenFingerprint.Differs(print, _recognized)) return false; // settled on what was already read
        _recognized = print;
        _lastRecognized = now;
        return true;
    }

    /// <summary>The dialogue to translate from the recognized paragraphs, or null when there is none or it is the line already shown.</summary>
    public TextBlock? NewLine(IReadOnlyList<TextBlock> blocks, Func<TextBlock, string> target)
    {
        var dialogue = TextBlocks.Dialogue(blocks.Where(b => TextBlocks.Translatable(b, target(b))).ToList());
        if (dialogue is null) return null;
        var key = TextBlocks.Key(dialogue.Text);
        if (key == _lastKey) return null;
        _lastKey = key;
        return dialogue;
    }

    /// <summary>Forget what was shown (live translation switched off and on again).</summary>
    public void Reset()
    {
        _last = _recognized = null;
        _lastRecognized = DateTime.MinValue;
        _lastKey = null;
    }
}
