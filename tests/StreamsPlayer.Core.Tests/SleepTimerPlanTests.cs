using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class SleepTimerPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 22, 30, 0, TimeSpan.FromHours(3));

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
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(23, 15));

        Assert.Equal(new DateTimeOffset(2026, 7, 24, 23, 15, 0, Now.Offset), deadline);
    }

    [Fact]
    public void FromLocalTime_AlreadyPassed_RollsToTomorrow()
    {
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(7, 0));

        Assert.Equal(new DateTimeOffset(2026, 7, 25, 7, 0, 0, Now.Offset), deadline);
        Assert.True(deadline - Now <= SleepTimerPlan.ClockHorizon);
    }

    [Fact]
    public void FromLocalTime_ExactlyNow_RollsToTomorrow()
    {
        // A timer resolved to "right now" would fire instantly, which is never what the user meant.
        var deadline = SleepTimerPlan.FromLocalTime(Now, new TimeOnly(22, 30));

        Assert.Equal(Now.AddDays(1), deadline);
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
