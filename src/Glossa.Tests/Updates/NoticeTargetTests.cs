using Glossa.Core.Notices;

namespace Glossa.Tests.Updates;

public class NoticeTargetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Eight = TimeSpan.FromSeconds(8);

    [Fact]
    public void A_click_on_an_update_notice_gives_its_address_once()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Update, "https://github.com/Victor21123/glossa/releases/tag/v0.1.0", Eight);
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal((NoticeKind.Update, "https://github.com/Victor21123/glossa/releases/tag/v0.1.0"), notice.TakeClick());
        Assert.Equal(NoticeKind.Nothing, notice.TakeClick().Kind);
    }

    [Fact]
    public void A_plain_notice_opens_nothing_even_after_an_update_notice_was_shown_before()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Update, "https://github.com/Victor21123/glossa/releases/tag/v0.1.0", Eight);
        clock.Advance(TimeSpan.FromSeconds(2));
        notice.Show(NoticeKind.Nothing, null, TimeSpan.FromSeconds(6)); // a hotkey notice replaced it
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal((NoticeKind.Nothing, (string?)null), notice.TakeClick());
    }

    [Fact]
    public void The_late_end_of_the_replaced_balloon_does_not_clear_the_new_target()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Study, null, Eight);
        clock.Advance(TimeSpan.FromSeconds(4));
        notice.Show(NoticeKind.Update, "u", Eight);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        notice.Closed(); // the first balloon's end arrives after the second was shown

        Assert.Equal(NoticeKind.Update, notice.TakeClick().Kind);
    }

    [Fact]
    public void The_real_end_of_a_notice_clears_the_target()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Update, "u", Eight);
        clock.Advance(TimeSpan.FromSeconds(1));
        notice.Closed();
        Assert.Equal(NoticeKind.Nothing, notice.TakeClick().Kind);
    }

    [Fact]
    public void An_end_that_never_comes_does_not_leave_the_target_alive_for_ever()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Update, "u", Eight);

        clock.Advance(Eight + TimeSpan.FromSeconds(2)); // the notice's time and the grace
        Assert.Equal(NoticeKind.Update, notice.TakeClick().Kind);

        notice.Show(NoticeKind.Update, "u", Eight);
        clock.Advance(Eight + TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
        Assert.Equal(NoticeKind.Nothing, notice.TakeClick().Kind);
    }

    [Fact]
    public void Nothing_shown_means_a_click_opens_nothing()
    {
        Assert.Equal(NoticeKind.Nothing, new NoticeTarget(new FakeTime(T0)).TakeClick().Kind);
    }

    [Fact]
    public void The_study_reminder_keeps_its_kind_and_has_no_address()
    {
        var clock = new FakeTime(T0);
        var notice = new NoticeTarget(clock);
        notice.Show(NoticeKind.Study, null, Eight);
        Assert.Equal((NoticeKind.Study, (string?)null), notice.TakeClick());
    }

    [Fact]
    public void Many_threads_showing_and_clicking_do_not_break_it()
    {
        var notice = new NoticeTarget();
        Parallel.For(0, 2000, i =>
        {
            if (i % 3 == 0) notice.Show(i % 2 == 0 ? NoticeKind.Study : NoticeKind.Update, "u", Eight);
            else if (i % 3 == 1) notice.Closed();
            else notice.TakeClick();
        });
    }
}
