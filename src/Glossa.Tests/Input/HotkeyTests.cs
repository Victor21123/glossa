using Glossa.Core.Input;

namespace Glossa.Tests.Input;

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Q", "Alt+Q")]
    [InlineData("ctrl+alt+g", "Ctrl+Alt+G")]
    [InlineData("Control+Shift+d", "Ctrl+Shift+D")]
    [InlineData("Win+Ctrl+Multiply", "Ctrl+Win+Multiply")]
    [InlineData("f8", "F8")]
    [InlineData("1", "1")]
    [InlineData("D5", "5")]
    public void Spec_round_trips_old_settings(string stored, string canonical)
    {
        var spec = KeySpec.Parse(stored);
        Assert.NotNull(spec);
        Assert.Equal(canonical, spec!.Format());
        Assert.Equal(spec, KeySpec.Parse(spec.Format()));
    }

    [Theory]
    [InlineData("Q+W")]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Spec_refuses_two_plain_keys_and_no_key(string? stored) => Assert.Null(KeySpec.Parse(stored));

    [Theory]
    [InlineData("Q", KeyVerdict.Typing)]
    [InlineData("Shift+Q", KeyVerdict.Typing)]
    [InlineData("5", KeyVerdict.Typing)]
    [InlineData("Enter", KeyVerdict.Typing)]
    [InlineData("OemComma", KeyVerdict.Typing)]
    [InlineData("Left", KeyVerdict.Typing)]
    [InlineData("Back", KeyVerdict.Typing)]
    [InlineData("Delete", KeyVerdict.Typing)]
    [InlineData("NumPad5", KeyVerdict.Typing)]
    [InlineData("Multiply", KeyVerdict.Typing)]
    [InlineData("VolumeUp", KeyVerdict.Ok)]
    [InlineData("MediaPlayPause", KeyVerdict.Ok)]
    [InlineData("BrowserBack", KeyVerdict.Ok)]
    [InlineData("LaunchMail", KeyVerdict.Ok)]
    [InlineData("Snapshot", KeyVerdict.Ok)]
    [InlineData("F8", KeyVerdict.Ok)]
    [InlineData("F24", KeyVerdict.Ok)]
    [InlineData("Pause", KeyVerdict.Ok)]
    [InlineData("Scroll", KeyVerdict.Ok)]
    [InlineData("Ctrl+Q", KeyVerdict.Ok)]
    [InlineData("Alt+Q", KeyVerdict.Ok)]
    [InlineData("Win+Left", KeyVerdict.Ok)]
    [InlineData("Shift+F5", KeyVerdict.Ok)]
    public void Typing_keys_alone_or_with_shift_are_allowed_with_a_warning(string stored, KeyVerdict expected)
        => Assert.Equal(expected, KeySpec.Parse(stored)!.Check());

    [Theory]
    [InlineData("Escape")]
    [InlineData("Tab")]
    [InlineData("S")]
    [InlineData("P")]
    [InlineData("F2")]
    [InlineData("Space")]
    public void The_cards_own_keys_cannot_be_the_lookup_key_alone(string key)
    {
        Assert.Contains(key, KeySpec.CardKeys);
        Assert.Equal(KeyVerdict.CardKey, KeySpec.Parse(key)!.Check());
    }

    [Fact]
    public void The_cards_keys_with_a_modifier_are_fine()
    {
        Assert.Equal(KeyVerdict.Ok, KeySpec.Parse("Ctrl+S")!.Check());
        Assert.Equal(KeyVerdict.Ok, KeySpec.Parse("Alt+Space")!.Check());
    }

    [Fact]
    public void Recording_takes_a_lone_key_when_it_is_released()
    {
        var r = new KeyRecorder();
        Assert.Null(r.Down("F8"));
        var result = r.Up("F8");
        Assert.Equal(KeyRecordKind.Taken, result!.Kind);
        Assert.Equal("F8", result.Spec!.Format());
        Assert.Equal(KeyVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void Recording_hands_the_verdict_with_the_key()
    {
        var r = new KeyRecorder();
        r.Down("Q");
        Assert.Equal(KeyVerdict.Typing, r.Up("Q")!.Verdict);
        r.Down("S");
        Assert.Equal(KeyVerdict.CardKey, r.Up("S")!.Verdict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recording_takes_modifiers_and_a_key_in_any_release_order(bool keyFirst)
    {
        var r = new KeyRecorder();
        Assert.Null(r.Down("LeftCtrl"));
        Assert.Null(r.Down("RightAlt"));
        Assert.Null(r.Down("G"));
        if (keyFirst)
        {
            Assert.Null(r.Up("G"));
            Assert.Null(r.Up("LeftCtrl"));
        }
        else
        {
            Assert.Null(r.Up("LeftCtrl"));
            Assert.Null(r.Up("RightAlt"));
        }
        var result = keyFirst ? r.Up("RightAlt") : r.Up("G");
        Assert.Equal("Ctrl+Alt+G", result!.Spec!.Format());
    }

    [Fact]
    public void Recording_stays_open_while_a_key_is_held()
    {
        var r = new KeyRecorder();
        r.Down("LeftShift");
        r.Down("D4");
        Assert.Null(r.Up("D4"));
        Assert.Equal("Shift+4", r.Up("LeftShift")!.Spec!.Format());
    }

    [Fact]
    public void Escape_alone_cancels_the_recording()
    {
        var r = new KeyRecorder();
        r.Down("Escape");
        Assert.Equal(KeyRecordKind.Cancelled, r.Up("Escape")!.Kind);
    }

    [Fact]
    public void Escape_with_a_modifier_is_a_key()
    {
        var r = new KeyRecorder();
        r.Down("LeftCtrl");
        r.Down("Escape");
        r.Up("Escape");
        Assert.Equal("Ctrl+Escape", r.Up("LeftCtrl")!.Spec!.Format());
    }

    [Fact]
    public void Modifiers_alone_are_refused()
    {
        var r = new KeyRecorder();
        r.Down("LeftCtrl");
        r.Down("LeftShift");
        r.Up("LeftShift");
        var result = r.Up("LeftCtrl");
        Assert.Equal(KeyRecordKind.Refused, result!.Kind);
        Assert.Equal(RecordRefusal.ModifiersOnly, result.Refusal);
    }

    [Fact]
    public void Two_plain_keys_together_are_refused_for_now()
    {
        var r = new KeyRecorder();
        r.Down("Q");
        r.Down("W");
        r.Up("Q");
        var result = r.Up("W");
        Assert.Equal(KeyRecordKind.Refused, result!.Kind);
        Assert.Equal(RecordRefusal.TwoKeys, result.Refusal);
    }

    [Fact]
    public void Key_repeat_is_ignored_and_a_release_without_a_press_is_taken()
    {
        var r = new KeyRecorder();
        r.Down("F9");
        r.Down("F9");
        r.Down("F9");
        Assert.Equal("F9", r.Up("F9")!.Spec!.Format());

        // PrintScreen sends only the key-up
        var fresh = new KeyRecorder();
        Assert.Equal("Snapshot", fresh.Up("Snapshot")!.Spec!.Format());
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Return")]
    [InlineData("Space")]
    [InlineData("LeftCtrl")]
    [InlineData("LeftAlt")]
    [InlineData("Q")]
    public void A_release_without_a_press_is_ignored(string key)
    {
        // The Enter that clicked "Изменить" is released after the recorder took over.
        var r = new KeyRecorder();
        Assert.Null(r.Up(key));
        r.Down("F9");
        Assert.Equal("F9", r.Up("F9")!.Spec!.Format());
    }

    [Fact]
    public void A_stale_release_does_not_end_a_recording_in_progress()
    {
        var r = new KeyRecorder();
        r.Down("G");
        Assert.Null(r.Up("Enter"));
        Assert.Equal("G", r.Up("G")!.Spec!.Format());
    }

    [Fact]
    public void A_recorder_starts_over_after_a_result()
    {
        var r = new KeyRecorder();
        r.Down("Q");
        Assert.NotNull(r.Up("Q"));
        r.Down("W");
        Assert.Equal("W", r.Up("W")!.Spec!.Format());
    }

    [Fact]
    public void Held_keys_read_as_caps_while_recording()
    {
        var r = new KeyRecorder();
        Assert.Equal("", r.Held);
        r.Down("G");
        r.Down("LeftAlt");
        Assert.Equal("Alt+G", r.Held);
        r.Down("RightCtrl");
        Assert.Equal("Ctrl+Alt+G", r.Held);
        r.Up("G");
        Assert.Equal("Ctrl+Alt", r.Held);
    }

    [Theory]
    [InlineData("x1")]
    [InlineData("x2")]
    public void A_side_mouse_button_is_taken_as_the_mouse_binding(string button)
    {
        var r = new KeyRecorder();
        r.Down("LeftCtrl");
        var result = r.Mouse(button);
        Assert.Equal(KeyRecordKind.Mouse, result!.Kind);
        Assert.Equal(button, result.MouseButton);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("middle")]
    [InlineData("")]
    public void The_main_mouse_buttons_are_not_taken(string button)
        => Assert.Null(new KeyRecorder().Mouse(button));
}
