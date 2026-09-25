using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0133: the player's evidence that a picture reached the screen. <c>PLAYBACK LIVE</c> is logged when the buffer
/// fills, which is before any frame has been decoded or shown - a package missing its video output or its codec
/// plugins still reaches it. <c>PLAYBACK SHOWN</c> is logged once per window, the first time the engine's own
/// displayed-picture counter moves, and it is the marker <c>scripts/smoke-playback.ps1</c> requires.
/// <para>No timer and no poll is added: the counter is the one the watchdog already reads for the freeze rule, on
/// its existing tick. An engine without counters (FlyleafLib) never logs the line, which the gate reads as a
/// failure - the gate runs on a fresh profile, whose engine is the default LibVLC.</para>
/// </summary>
public partial class PlayerWindow
{
    private bool _frameShownLogged;

    private void NoteFirstDisplayedFrame(PlaybackProgressCounters? progress)
    {
        if (_frameShownLogged || progress is not { DisplayedPictures: > 0 } counters)
        {
            return;
        }

        _frameShownLogged = true;
        _log.Event("PLAYBACK SHOWN", $"frames={counters.DisplayedPictures}",
            $"at_ms={_playbackClock.ElapsedMilliseconds}", $"url={_channel.Url}");
    }
}
