using System.Diagnostics;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0169: the radio's stall watchdog. A station that stops delivering without closing raises no error and no
/// end, so nothing else ever learns of it - "Now playing" stays on screen and the idle-sleep hold stays with it.
/// The LibVLC engine (SP-0104) keeps the bytes it has read from the source; while a connection plays they are
/// sampled, and a flow that stays still for <see cref="AudioStallDetector.StallAfter"/> enters the recovery a
/// failure enters.
/// <para>One timer per connection that went live, stopped when the connection is no longer the current one or
/// when the session stops. The rule is in Core; this is only its observation cadence.</para>
/// </summary>
public partial class MainWindow
{
    // The video watchdog's cadence (PlayerWindow): fine against a fifteen-second bound, cheap against a poll.
    private static readonly TimeSpan AudioStallPollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _audioStallTimer;

    // SP-0169: the silence the current leg spent stalled, taken off its playing time when the recovery decides
    // whether the leg played long enough to earn its budget back - the stall is not playing.
    private TimeSpan _audioLegSilence;

    private void WatchForAudioStall(PlaybackConnectionId connection)
    {
        StopWatchingAudioStall();
        var detector = new AudioStallDetector();
        var clock = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AudioStallPollInterval };
        timer.Tick += (_, _) => HandlerBoundary.Run(nameof(WatchForAudioStall), async () =>
        {
            if (!ReferenceEquals(_audioStallTimer, timer))
            {
                timer.Stop();
                return;
            }

            // Null: the connection was stopped or replaced, so there is nothing left to watch.
            if (_standardAudioPlayback.ReadInputBytes(connection) is not { } bytesRead)
            {
                StopWatchingAudioStall();
                return;
            }

            if (detector.Observe(Stopwatch.GetElapsedTime(clock), bytesRead))
            {
                await HandleAudioStallAsync(connection, bytesRead);
            }
        });
        _audioStallTimer = timer;
        timer.Start();
    }

    private void StopWatchingAudioStall()
    {
        _audioStallTimer?.Stop();
        _audioStallTimer = null;
    }

    private async Task HandleAudioStallAsync(PlaybackConnectionId connection, long bytesRead)
    {
        StopWatchingAudioStall(); // the next leg starts its own watch when it goes live
        if (!_standardAudioPlayback.IsCurrent(connection) || _playingAudio is not { } row)
        {
            return;
        }

        _log.Event("AUDIO STALL",
            $"silent_ms={AudioStallDetector.StallAfter.TotalMilliseconds:F0}",
            $"read_bytes={bytesRead}",
            $"url={row.Channel.Url}");
        // The hunt's probe is the hunt's to judge; once a station went live it is an ordinary one, and this
        // is the same intercept every other radio failure passes through first.
        if (YieldToRandomStationHunt(row.Channel, "stall"))
        {
            return;
        }

        _audioLegSilence = AudioStallDetector.StallAfter;
        _standardAudioPlayback.StopPlayback();
        await RecoverAudioAsync(row.Channel, "stall", stall: true);
    }
}
