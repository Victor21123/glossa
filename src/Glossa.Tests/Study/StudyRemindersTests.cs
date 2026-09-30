using Glossa.Core.Study;

namespace Glossa.Tests.Study;

public class StudyRemindersTests
{
    private static readonly TimeOnly[] Evening = [new(18, 0), new(20, 0)];
    private static DateTime At(int hour, int minute = 0) => new(2026, 10, 1, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void A_reminder_comes_at_its_time_once()
    {
        Assert.Null(StudyReminders.Due(At(17, 59), Evening, null, studiedToday: false, cardsToStudy: 5));
        Assert.Equal(new TimeOnly(18, 0), StudyReminders.Due(At(18, 0), Evening, null, false, 5));
        Assert.Null(StudyReminders.Due(At(18, 30), Evening, lastShownLocal: At(18, 0), false, 5));
        Assert.Equal(new TimeOnly(20, 0), StudyReminders.Due(At(20, 5), Evening, lastShownLocal: At(18, 0), false, 5));
        Assert.Null(StudyReminders.Due(At(21, 0), Evening, lastShownLocal: At(20, 5), false, 5));
    }

    [Fact]
    public void No_reminder_after_study_with_nothing_to_study_or_long_after_its_time()
    {
        Assert.Null(StudyReminders.Due(At(18, 10), Evening, null, studiedToday: true, cardsToStudy: 5));
        Assert.Null(StudyReminders.Due(At(18, 10), Evening, null, studiedToday: false, cardsToStudy: 0));
        Assert.Null(StudyReminders.Due(At(23, 0), Evening, null, false, 5)); // started late: 20:00 is past its window
        // Yesterday's reminder does not count today.
        Assert.Equal(new TimeOnly(20, 0), StudyReminders.Due(At(21, 30), Evening, lastShownLocal: At(20, 0).AddDays(-1), false, 5));
    }

    [Theory]
    [InlineData(1, "1 карточка")]
    [InlineData(3, "3 карточки")]
    [InlineData(11, "11 карточек")]
    [InlineData(22, "22 карточки")]
    public void The_words_count_cards_in_russian(int n, string expected) => Assert.Contains(expected, StudyReminders.Text(n));
}
