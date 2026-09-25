using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class SleepTimerPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 22, 30, 0, TimeSpan.FromHours(3));

    // A zone with no clock changes, so the ordinary cases read exactly as they did before SP-0132.
    private static readonly TimeZoneInfo Fixed =
        TimeZoneInfo.CreateCustomTimeZone("Test+03", Now.Offset, "Test+03", "Test+03");

    // The author's zone. "W. Europe Standard Time" follows the same EU rule, for a Windows host without
    // the IANA mapping.
    private static readonly TimeZoneInfo Malta =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Malta", out var malta)
            ? malta
            : TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static readonly TimeSpan Summer = TimeSpan.FromHours(2);
    private static readonly TimeSpan Winter = TimeSpan.FromHours(1);

    [Fact]
    public void FromDuration_AddsThePreset()
    {
        var deadline = SleepTimerPlan.FromDuration(Now, TimeSpan.FromMinutes(45));

        Assert.Equal(Now.AddMinutes(45), deadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void FromDuration_RejectsNonPositive(int minutes)
    {
        Assert.Null(SleepTimerPlan.FromDuration(Now, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void FromLocalTime_LaterToday_StaysToday()
    {
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(23, 15), Fixed);

        Assert.Equal(new DateTimeOffset(2026, 7, 24, 23, 15, 0, Now.Offset), deadline);
    }

    [Fact]
    public void FromLocalTime_AlreadyPassed_RollsToTomorrow()
    {
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(7, 0), Fixed);

        Assert.Equal(new DateTimeOffset(2026, 7, 25, 7, 0, 0, Now.Offset), deadline);
        Assert.True(deadline - Now <= SleepTimerPlan.ClockHorizon);
    }

    [Fact]
    public void FromLocalTime_ExactlyNow_RollsToTomorrow()
    {
        // A timer resolved to "right now" would fire instantly, which is never what the user meant.
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(22, 30), Fixed);

        Assert.Equal(Now.AddDays(1), deadline);
    }

    // SP-0132: "stop at 07:00" set the evening before a clock change. Resolving with the offset in force
    // at the moment of setting fired at 06:00 (October) or 08:00 (March) by the clock on the wall.
    [Fact]
    public void FromLocalTime_AcrossTheOctoberChange_LandsOnTheRequestedWallTime()
    {
        var evening = new DateTimeOffset(2026, 10, 24, 23, 0, 0, Summer);

        var deadline = SleepTimerPlan.FromLocalTime(evening, new TimeOnly(7, 0), Malta);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 7, 0, 0, Winter), deadline);
        Assert.Equal(new TimeOnly(7, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(deadline, Malta).DateTime));
        Assert.Equal(TimeSpan.FromHours(9), deadline - evening);
    }

    [Fact]
    public void FromLocalTime_AcrossTheMarchChange_LandsOnTheRequestedWallTime()
    {
        var evening = new DateTimeOffset(2026, 3, 28, 23, 0, 0, Winter);

        var deadline = SleepTimerPlan.FromLocalTime(evening, new TimeOnly(7, 0), Malta);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 7, 0, 0, Summer), deadline);
        Assert.Equal(TimeSpan.FromHours(7), deadline - evening);
    }

    // 02:30 on the last Sunday of March is never shown: the clock goes from 01:59 to 03:00. The timer
    // stops when the clock jumps past the time asked for, not an hour after.
    [Fact]
    public void FromLocalTime_ATimeTheSpringChangeSkips_StopsWhenTheClockJumpsPastIt()
    {
        var evening = new DateTimeOffset(2026, 3, 28, 23, 0, 0, Winter);

        var deadline = SleepTimerPlan.FromLocalTime(evening, new TimeOnly(2, 30), Malta);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, Summer), deadline);
    }

    // 02:30 on the last Sunday of October is shown twice. The first reading still ahead is the one.
    [Fact]
    public void FromLocalTime_ATimeTheAutumnChangeRepeats_TakesTheFirstReadingStillAhead()
    {
        var evening = new DateTimeOffset(2026, 10, 24, 23, 0, 0, Summer);
        var insideTheFirstPass = new DateTimeOffset(2026, 10, 25, 2, 45, 0, Summer);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, Summer),
            SleepTimerPlan.FromLocalTime(evening, new TimeOnly(2, 30), Malta));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, Winter),
            SleepTimerPlan.FromLocalTime(insideTheFirstPass, new TimeOnly(2, 30), Malta));
    }

    // The longest a wall-clock choice can be: a minute short of one clock day, on the day that is 25 hours long.
    [Fact]
    public void FromLocalTime_OnTheLongDay_StaysWithinTheHorizon()
    {
        var justAfterMidnight = new DateTimeOffset(2026, 10, 25, 0, 30, 0, Summer);

        var deadline = SleepTimerPlan.FromLocalTime(justAfterMidnight, new TimeOnly(0, 29), Malta);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 29, 0, Winter), deadline);
        Assert.Equal(TimeSpan.FromHours(25) - TimeSpan.FromMinutes(1), deadline - justAfterMidnight);
        Assert.True(deadline - justAfterMidnight <= SleepTimerPlan.ClockHorizon);
    }

    [Fact]
    public void Remaining_IsClampedAtZeroAndExpiryIsIdempotent()
    {
        var deadline = Now.AddMinutes(10);

        Assert.Equal(TimeSpan.FromMinutes(10), SleepTimerPlan.Remaining(Now, deadline));
        Assert.False(SleepTimerPlan.HasExpired(Now, deadline));

        // Machine slept through the deadline: still expired, still no negative countdown.
        var afterSleep = deadline.AddHours(3);
        Assert.Equal(TimeSpan.Zero, SleepTimerPlan.Remaining(afterSleep, deadline));
        Assert.True(SleepTimerPlan.HasExpired(afterSleep, deadline));
        Assert.True(SleepTimerPlan.HasExpired(deadline, deadline));
    }

    [Theory]
    [InlineData(0, 0, "0:00")]
    [InlineData(0, 59, "0:59")]
    [InlineData(14, 59, "14:59")]
    [InlineData(60, 0, "1:00:00")]
    [InlineData(125, 5, "2:05:05")]
    public void FormatRemaining_SwitchesToHoursPastAnHour(int minutes, int seconds, string expected)
    {
        var text = SleepTimerPlan.FormatRemaining(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void FormatRemaining_RoundsPartialSecondsUpAndNeverGoesNegative()
    {
        // A ticking timer must not read 0:00 while it is still running.
        Assert.Equal("1:00", SleepTimerPlan.FormatRemaining(TimeSpan.FromSeconds(59.2)));
        Assert.Equal("0:00", SleepTimerPlan.FormatRemaining(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData("07:05", 7, 5)]
    [InlineData("7:05", 7, 5)]
    [InlineData(" 23:59 ", 23, 59)]
    public void ParseLocalTime_AcceptsTwentyFourHourText(string text, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), SleepTimerPlan.ParseLocalTime(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("-1:30")]
    [InlineData("noon")]
    [InlineData("12")]
    [InlineData("12:30:00")]
    public void ParseLocalTime_RejectsAnythingElse(string? text)
    {
        Assert.Null(SleepTimerPlan.ParseLocalTime(text));
    }
}
