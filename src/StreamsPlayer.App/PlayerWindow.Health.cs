using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0045: the player's side of the signal-health stripe. The decision itself lives in
/// <see cref="SignalHealthMonitor"/>; this partial only forwards the observations the window already
/// makes and paints the answer.
///
/// <para>No timer and no poll is added: the stall, watchdog, recovery and failure signals are the ones
/// the player already raises, and the loss counters are read on the existing two-second stats tick.</para>
/// </summary>
public partial class PlayerWindow
{
    private readonly SignalHealthMonitor _health = new();
    private string _healthNameKey = "";

    /// <summary>
    /// The monotonic reading the rule is fed. <c>_sessionClock</c> spans the whole window, unlike
    /// <c>_playbackClock</c>, which restarts on every reconnect - a clean interval measured on a clock
    /// that restarts mid-interval would be no interval at all.
    /// </summary>
    private TimeSpan HealthNow => _sessionClock.Elapsed;

    /// <summary>
    /// Drops the loss baseline for a media that is being opened. <c>StartMedia</c> is the one feed point
    /// the window also calls off the UI thread (the recover path plays off-thread so a flapping stream
    /// cannot freeze WPF), and the monitor is single-threaded, so this hop is what keeps it so.
    /// </summary>
    private void NotifySignalHealthOpening()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(NotifySignalHealthOpening);
            return;
        }

        _health.NotifyOpening();
    }

    /// <summary>One observation of the decoder's loss counters, on the existing stats tick.</summary>
    private void SampleSignalHealth()
    {
        _health.NotifySample(HealthNow, _backend.ReadLossCounters());
        ApplySignalHealth();
    }

    /// <summary>
    /// Paints the current state. Cheap and idempotent, so every feed point can simply call it.
    ///
    /// <para>Assigning to a control that is merely <c>Collapsed</c> while the panel is auto-hidden is
    /// harmless and is what makes the state survive a hide/show cycle without extra bookkeeping;
    /// nothing here forces the panel visible or restarts its hide timer.</para>
    /// </summary>
    private void ApplySignalHealth()
    {
        var health = _health.Evaluate(HealthNow);
        BufferProgress.Foreground = SignalHealthAppearance.Fill(health);
        BufferProgress.Background = SignalHealthAppearance.Track(health);

        var key = SignalHealthAppearance.NameKey(health);
        if (key == _healthNameKey)
        {
            return;
        }

        _healthNameKey = key;
        RefreshSignalHealthText();
    }

    /// <summary>
    /// Re-reads the state name for the current language. The tooltip is assigned rather than bound to a
    /// <c>DynamicResource</c> so that the word and the colour are set by the same code path and cannot
    /// drift apart; the cost is that a live language swap has to replay it, as <c>WaitText</c> does.
    /// </summary>
    private void RefreshSignalHealthText()
    {
        if (_healthNameKey.Length == 0)
        {
            return;
        }

        BufferProgress.ToolTip = LocalizationService.Get(_healthNameKey);
    }
}
