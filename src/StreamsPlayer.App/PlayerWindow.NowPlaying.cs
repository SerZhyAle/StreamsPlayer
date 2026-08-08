using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0073: the player's side of the line that says what is on air. The rule lives in
/// <see cref="NowPlayingTracker"/>; this partial only takes the reading the open stream already offers
/// and paints the answer under the channel name.
///
/// <para>No timer, no poll and no second connection is added (ticket boundary): the media engine parses
/// the broadcast's own service fields as part of playing it, and the reading rides the same two-second
/// stats tick the loss counters and the quality probe already ride.</para>
///
/// <para>This line exists because the player named the channel and nothing else. Once the player became
/// the window for everything that is not plain radio, "which channel" stopped being the same question as
/// "what is on it".</para>
/// </summary>
public partial class PlayerWindow
{
    private readonly NowPlayingTracker _nowPlaying = new();

    /// <summary>
    /// One reading, on the existing stats tick. Repaints and logs only on a real change, so a session log
    /// answers "did this stream announce anything, and how often" without a line per tick.
    /// </summary>
    private void SampleNowPlaying()
    {
        if (!_nowPlaying.Observe(HealthNow, _backend.ReadNowPlaying()))
        {
            return;
        }

        _log.Event("NOW PLAYING", $"engine={_backend.EngineName}", $"text={_nowPlaying.Text}", $"url={_channel.Url}");
        ApplyNowPlaying();
    }

    /// <summary>
    /// The broadcast this line described is over. Called from the terminal-failure funnel: a channel
    /// change is a different window and a stop closes this one, so that funnel is the only moment the
    /// window survives its own broadcast.
    /// </summary>
    private void ClearNowPlaying()
    {
        if (_nowPlaying.Clear())
        {
            ApplyNowPlaying();
        }
    }

    /// <summary>
    /// Paints the current answer. Cheap and idempotent, so every feed point can simply call it.
    ///
    /// <para>Assigning to a control that is merely <c>Collapsed</c> while the panel is auto-hidden is
    /// harmless and is what makes the line survive a hide/show cycle without extra bookkeeping; nothing
    /// here forces the panel visible or restarts its hide timer.</para>
    /// </summary>
    private void ApplyNowPlaying()
    {
        if (_nowPlaying.Text is not { } text)
        {
            OnAirText.Text = string.Empty;
            OnAirText.Visibility = Visibility.Collapsed;
            return;
        }

        // The key is a literal here so the SP-0057 call-site gate can check it against the shipped
        // string, which is what keeps this one-argument call and its one placeholder in step.
        OnAirText.Text = LocalizationService.Format("PlayerOnAir", text);
        OnAirText.Visibility = Visibility.Visible;
    }
}
