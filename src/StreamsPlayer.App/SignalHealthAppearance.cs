using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0045: how each signal-health state looks and what it is called. One place, so a colour can never
/// ship without the word that goes with it - colour is never the only carrier of the state.
///
/// <para>The colours are fixed rather than taken from the app theme: this stripe lives on the player's
/// own dark translucent panel, floating over arbitrary video, which the theme does not reach. They are
/// picked to stay apart for the common colour-vision deficiencies by lightness as well as hue, and the
/// tooltip carries the state in words regardless.</para>
///
/// <para>Every state has two brushes. The fill shows the buffer level as it always did; the track shows
/// the same state in a darker tone of the same hue, so an empty buffer still reports the state instead
/// of going blank at the moment that matters most (decision 3), while staying dark enough that the
/// buffer level is still readable against it.</para>
/// </summary>
internal static class SignalHealthAppearance
{
    // Frozen and cached: these are re-assigned on every observation sample, on a control that repaints
    // over live video, so allocating a brush per tick would be a per-frame cost for no reason.
    private static readonly SolidColorBrush UnknownFill = Frozen("#FF8A94A6");
    private static readonly SolidColorBrush UnknownTrack = Frozen("#FF3A414D");
    private static readonly SolidColorBrush GoodFill = Frozen("#FF34C759");
    private static readonly SolidColorBrush GoodTrack = Frozen("#FF1E6E33");
    private static readonly SolidColorBrush DegradedFill = Frozen("#FFFFC531");
    private static readonly SolidColorBrush DegradedTrack = Frozen("#FF8A6A12");
    private static readonly SolidColorBrush LostFill = Frozen("#FFFF5B50");
    private static readonly SolidColorBrush LostTrack = Frozen("#FFB3231C");

    /// <summary>The buffer fill for this state.</summary>
    internal static SolidColorBrush Fill(SignalHealth health) => health switch
    {
        SignalHealth.Good => GoodFill,
        SignalHealth.Degraded => DegradedFill,
        SignalHealth.Lost => LostFill,
        _ => UnknownFill
    };

    /// <summary>The stripe's track - the part that carries the state when the buffer is empty.</summary>
    internal static SolidColorBrush Track(SignalHealth health) => health switch
    {
        SignalHealth.Good => GoodTrack,
        SignalHealth.Degraded => DegradedTrack,
        SignalHealth.Lost => LostTrack,
        _ => UnknownTrack
    };

    /// <summary>The localization key of this state's name, shown as the stripe's tooltip.</summary>
    internal static string NameKey(SignalHealth health) => health switch
    {
        SignalHealth.Good => "SignalHealthGood",
        SignalHealth.Degraded => "SignalHealthDegraded",
        SignalHealth.Lost => "SignalHealthLost",
        _ => "SignalHealthUnknown"
    };

    private static SolidColorBrush Frozen(string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brush;
    }
}
