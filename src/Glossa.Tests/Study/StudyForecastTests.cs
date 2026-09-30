using Glossa.Core.Study;

namespace Glossa.Tests.Study;

public class StudyForecastTests
{
    private static readonly StudyClock Utc = new(TimeZoneInfo.Utc);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static ReviewState Review(string id, int inDays, CardDirection direction = CardDirection.Forward) =>
        new() { WordId = id, Direction = direction, Queue = CardQueue.Review, IntervalDays = 3, DueDay = Today.AddDays(inDays), Ease = 2.5f };

    [Fact]
    public void Cards_fall_on_their_due_days_overdue_ones_today()
    {
        var states = new[]
        {
            Review("a", 0), Review("b", -3), Review("c", 2), Review("d", 2), Review("e", 9), // e: past the week
            new ReviewState { WordId = "f", Queue = CardQueue.Learning, DueAt = new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc) },
            new ReviewState { WordId = "g", Queue = CardQueue.Relearning, DueAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc) },
            ReviewState.New("h"), // not scheduled yet
        };

        Assert.Equal([3, 1, 2, 0, 0, 0, 0], StudyForecast.Due(states, StudyDirection.Forward, Utc, Today));
    }

    [Fact]
    public void A_direction_switched_off_is_not_counted()
    {
        var states = new[] { Review("a", 1), Review("a", 1, CardDirection.Reverse) };

        Assert.Equal(1, StudyForecast.Due(states, StudyDirection.Forward, Utc, Today)[1]);
        Assert.Equal(1, StudyForecast.Due(states, StudyDirection.Reverse, Utc, Today)[1]);
        Assert.Equal(2, StudyForecast.Due(states, StudyDirection.Both, Utc, Today)[1]);
    }
}
