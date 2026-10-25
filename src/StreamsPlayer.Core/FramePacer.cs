namespace StreamsPlayer.Core;

/// <summary>
/// A fixed-rate frame schedule for a capture loop (SP-0243). The next frame is due one interval after the previous one
/// was due, not one interval after it was finished: a loop that waits a full interval after every read runs at one frame
/// per (read time + interval), which is what held a 4K screen broadcast to 10 frames per second. A loop that falls
/// behind by less than an interval keeps the grid; one that stalled for longer starts again from there and never replays
/// the frames it missed.
/// </summary>
public sealed class FramePacer(long intervalTicks)
{
    private readonly long _interval = intervalTicks > 0 ? intervalTicks : throw new ArgumentOutOfRangeException(nameof(intervalTicks));
    private long _due;

    /// <summary>How long to wait before the next frame may be taken; zero when it is due.</summary>
    public long Delay(long now) => Math.Max(0, _due - now);

    /// <summary>Records that the frame due now was started at <paramref name="started"/> and schedules the one after it.</summary>
    public void Started(long started) => _due = started - _due >= _interval ? started + _interval : _due + _interval;
}
