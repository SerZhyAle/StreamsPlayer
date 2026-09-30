using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0133: the player's evidence that a picture reached the screen. <c>PLAYBACK SHOWN</c> is logged on the
/// first displayed picture of each leg, and is the marker <c>scripts/smoke-playback.ps1</c> requires.
/// SP-0163 defers the LibVLC video leg's LIVE verdict until that same picture exists.
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
