using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// The sidebar's date headers. These are all boundary tests, because the label is
/// only ever wrong at a boundary: midnight, the seventh day, and a session with
/// no date at all.
/// </summary>
public sealed class SessionGroupingTests
{
    /// <summary>A Wednesday, mid-afternoon, as the reference "now".</summary>
    private static readonly DateTimeOffset Now = At(2026, 8, 12, 15, 30);

    /// <summary>
    /// A wall-clock instant in this machine's own zone, which is how
    /// SessionIndex builds StartedAt from a folder name. Using a fixed UTC offset
    /// here instead would shift every value by the tester's zone and make the
    /// midnight cases pass or fail depending on where the test ran.
    /// </summary>
    private static DateTimeOffset At(int year, int month, int day, int hour = 12, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static SessionListing Session(DateTimeOffset? startedAt, string id = "2026.08.12-1200") =>
        new(Directory: $"/recordings/{id}", Id: id, Status: SessionStatus.Ready,
            Name: null, StartedAt: startedAt, DurationSeconds: 92, CleanStop: true,
            Words: 10, FirstLine: "hello");

    [Fact]
    public void ASessionFromThisMorningIsToday() =>
        Assert.Equal("Today", SessionGrouping.TitleFor(At(2026, 8, 12, 9), Now));

    [Fact]
    public void JustAfterMidnightIsStillToday()
    {
        // Fifteen hours before "now", so an elapsed-hours comparison would call
        // this yesterday. A person reading the list would not.
        Assert.Equal("Today", SessionGrouping.TitleFor(At(2026, 8, 12, 0, 30), Now));
    }

    [Fact]
    public void LateLastNightIsYesterday()
    {
        // Sixteen hours before the session above, and one calendar day earlier.
        Assert.Equal("Yesterday", SessionGrouping.TitleFor(At(2026, 8, 11, 23, 30), Now));
    }

    [Theory]
    [InlineData(2, "Monday")]
    [InlineData(3, "Sunday")]
    [InlineData(6, "Thursday")]
    public void InsideTheLastWeekTheHeaderIsAWeekdayName(int daysAgo, string expected)
    {
        var day = At(2026, 8, 12 - daysAgo, 15, 30);
        Assert.Equal(expected, SessionGrouping.TitleFor(day, Now));
    }

    [Fact]
    public void AtSevenDaysTheHeaderBecomesADateBeforeAWeekdayCouldRepeat()
    {
        // Seven days back is the same weekday as today. "Wednesday" would then
        // appear twice in one list, meaning two different weeks.
        Assert.Equal("Aug 5, 2026", SessionGrouping.TitleFor(At(2026, 8, 5, 15, 30), Now));
    }

    [Fact]
    public void AnOlderSessionGetsAnAbsoluteDate() =>
        Assert.Equal("Mar 3, 2026", SessionGrouping.TitleFor(At(2026, 3, 3), Now));

    [Fact]
    public void ASessionWithNoDateIsUndatedRatherThanDropped() =>
        Assert.Equal("Undated", SessionGrouping.TitleFor(null, Now));

    [Fact]
    public void ASessionDatedInTheFutureGetsADateNotAWeekday()
    {
        // A clock change can produce this. Calling it by weekday name would put
        // it in the same bucket as a session from the recent past.
        Assert.Equal("Aug 20, 2026", SessionGrouping.TitleFor(At(2026, 8, 20), Now));
    }

    [Fact]
    public void GroupingKeepsTheOrderTheSessionsArriveIn()
    {
        var groups = SessionGrouping.Group(
            [
                Session(At(2026, 8, 12, 14), "2026.08.12-1400"),
                Session(At(2026, 8, 12, 9), "2026.08.12-0900"),
                Session(At(2026, 8, 11, 16), "2026.08.11-1600"),
                Session(At(2026, 3, 3), "2026.03.03-1200"),
            ],
            Now);

        Assert.Equal(["Today", "Yesterday", "Mar 3, 2026"], groups.Select(group => group.Title));
        // The list arrives newest first, and grouping must not reorder inside a
        // bucket.
        Assert.Equal(
            ["2026.08.12-1400", "2026.08.12-0900"],
            groups[0].Sessions.Select(session => session.Id));
    }

    [Fact]
    public void SessionsFromOneDayShareOneHeaderEvenWhenSeparated()
    {
        // Two sessions from today with one from yesterday between them. A
        // grouping that only compared with the previous row would emit "Today"
        // twice.
        var groups = SessionGrouping.Group(
            [
                Session(At(2026, 8, 12, 14), "a"),
                Session(At(2026, 8, 11, 16), "b"),
                Session(At(2026, 8, 12, 9), "c"),
            ],
            Now);

        Assert.Equal(["Today", "Yesterday"], groups.Select(group => group.Title));
        Assert.Equal(["a", "c"], groups[0].Sessions.Select(session => session.Id));
    }

    [Fact]
    public void AnEmptyListHasNoGroups() =>
        Assert.Empty(SessionGrouping.Group([], Now));
}
