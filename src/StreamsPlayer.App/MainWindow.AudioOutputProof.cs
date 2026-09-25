using System.Diagnostics;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0133: the evidence that a station was heard. <c>AUDIO LIVE</c> is logged when the engine reports Playing,
/// which is before any audio is decoded or reaches the output - a package missing its audio output or codec plugins
/// still reaches it. From that moment the connection's own statistics are read until the audio output has played a
/// buffer (<c>AUDIO HEARD</c>) or a deadline passes without one (<c>AUDIO SILENT</c>). The first is the marker
/// <c>scripts/smoke-playback.ps1</c> requires; both are diagnostics in an ordinary session.
/// <para>One short-lived timer per connection that went live, stopped at its answer, on a superseded connection,
/// or at the deadline - never a standing poll.</para>
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan AudioOutputPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AudioOutputDeadline = TimeSpan.FromSeconds(20);

    private DispatcherTimer? _audioOutputTimer;

    private void WatchForAudibleOutput(PlaybackConnectionId connection)
    {
        StopWatchingAudibleOutput();
        var url = _playingAudio?.Channel.Url ?? "n/a";
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AudioOutputPollInterval };
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_audioOutputTimer, timer))
            {
                timer.Stop();
                return;
            }

            // Null: the connection was stopped or replaced, so there is nothing left to prove about it.
            if (_standardAudioPlayback.ReadOutput(connection) is not { } output)
            {
                StopWatchingAudibleOutput();
                return;
            }

            var heard = output.PlayedBuffers > 0;
            if (!heard && clock.Elapsed < AudioOutputDeadline)
            {
                return;
            }

            _log.Event(heard ? "AUDIO HEARD" : "AUDIO SILENT",
                $"played_buffers={output.PlayedBuffers}", $"decoded_blocks={output.DecodedBlocks}",
                $"lost_buffers={output.LostBuffers}", $"after_live_ms={clock.ElapsedMilliseconds}", $"url={url}");
            StopWatchingAudibleOutput();
        };
        _audioOutputTimer = timer;
        timer.Start();
    }

    private void StopWatchingAudibleOutput()
    {
        _audioOutputTimer?.Stop();
        _audioOutputTimer = null;
    }
}
