namespace StreamsPlayer.App;

/// <summary>
/// SP-0110: which animated backdrop (<c>WAVE-PARTICLES</c>) the playing station carries, and where it is
/// shown. One session per station: it runs while the station plays, freezes on Stop, resumes on Play,
/// and a different station starts a new one that inherits the old picture so the change washes in.
/// </summary>
public partial class MainWindow
{
    private WaveParticlesBackdropSession? _backdrop;

    /// <param name="resumes">
    /// True when this start is the same station coming back from Stop, which keeps its session (rule 3:
    /// a resume keeps the rolls); anything else rolls a new one.
    /// </param>
    private void StartBackdrop(Guid channelId, bool resumes)
    {
        if (!resumes || _backdrop?.ChannelId != channelId)
        {
            _backdrop = new WaveParticlesBackdropSession(channelId, _backdrop);
        }

        _backdrop.IsRunning = true;
        ApplyBackdrop();
    }

    private void StopBackdrop()
    {
        _playingAudio?.SetBackdrop(null);
        if (_backdrop is not null)
        {
            _backdrop.IsRunning = false;
        }
    }

    /// <summary>Hands the session to the playing card or tile, honouring the Settings switch.</summary>
    private void ApplyBackdrop()
    {
        _playingAudio?.SetBackdrop(_state.AnimatedBackdrop ? _backdrop : null);
        UpdateCompactPanel();
    }

    /// <summary>
    /// The panel shows the backdrop for the station it is showing - playing, or stopped and remembered -
    /// and nothing when there is no station, so an idle panel looks as it always has.
    /// </summary>
    private WaveParticlesBackdropSession? CompactPanelBackdrop(ChannelRow? row) =>
        _state.AnimatedBackdrop && row is not null && _backdrop?.ChannelId == row.Channel.Id ? _backdrop : null;
}
