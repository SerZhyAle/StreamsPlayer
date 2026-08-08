using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0072: the player's side of the caption drawn over the video while there is no picture. The rule
/// lives in <see cref="PlaybackInterruptionTracker"/>; this partial only forwards the interruptions the
/// window already observes and paints the answer.
///
/// <para>No timer and no poll is added (ticket boundary): the causes are the events the window already
/// raises, and the appear delay expires on the existing two-second stats tick and on the backend's own
/// buffering events, which arrive continuously during exactly the state being reported.</para>
///
/// <para>This caption exists because the status line it mirrors lives inside a panel that hides itself
/// after ten seconds of no mouse. A viewer who is only watching therefore had no explanation at the one
/// moment an explanation was worth having.</para>
/// </summary>
public partial class PlayerWindow
{
    private readonly PlaybackInterruptionTracker _interruption = new();
    private PlaybackInterruptionNotice _shownNotice = PlaybackInterruptionNotice.Silent;

    /// <summary>
    /// The picture is gone, for this reason. Marshals like <see cref="NotifySignalHealthOpening"/> does:
    /// the tracker is single-threaded and two of the window's re-open paths run off the UI thread.
    /// </summary>
    private void NotifyInterrupted(PlaybackInterruptionKind kind, int attempt = 0, int budget = 0)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => NotifyInterrupted(kind, attempt, budget));
            return;
        }

        _interruption.NotifyInterrupted(HealthNow, kind, attempt, budget);
        ApplyInterruptionNotice();
    }

    /// <summary>The picture is back. Always reached from the UI thread, on the buffer-full path.</summary>
    private void NotifyPictureLive()
    {
        _interruption.NotifyLive();
        ApplyInterruptionNotice();
    }

    /// <summary>
    /// Paints the current answer. Cheap and idempotent, so every feed point can simply call it - and the
    /// stats tick calls it too, which is what lets the appear delay elapse when nothing else fires.
    /// </summary>
    private void ApplyInterruptionNotice()
    {
        var notice = _interruption.Evaluate(HealthNow);
        if (notice == _shownNotice)
        {
            return; // same words already on screen; re-assigning them would restart nothing and cost layout
        }

        _shownNotice = notice;
        if (notice.Kind == PlaybackInterruptionKind.None)
        {
            InterruptionNotice.Visibility = Visibility.Collapsed;
            return;
        }

        InterruptionNoticeText.Text = DescribeInterruption(notice);
        InterruptionNotice.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Re-renders the caption for the current language. The window is non-modal, so the language can
    /// change while it is open, and this text is assigned rather than bound to a <c>DynamicResource</c>
    /// because one of its five states carries numbers. Clearing the memo first is what makes the repaint
    /// happen: the state has not changed, only the words for it.
    /// </summary>
    private void RefreshInterruptionNotice()
    {
        _shownNotice = PlaybackInterruptionNotice.Silent;
        ApplyInterruptionNotice();
    }

    /// <summary>
    /// Every key is a literal here so the SP-0057 call-site gate can check each one against the shipped
    /// string, which is also what forces the two-argument reconnect form to keep its two arguments.
    /// </summary>
    private static string DescribeInterruption(PlaybackInterruptionNotice notice) => notice.Kind switch
    {
        PlaybackInterruptionKind.Connecting => LocalizationService.Get("PlayerNoticeConnecting"),
        PlaybackInterruptionKind.SignalLost => LocalizationService.Get("PlayerNoticeSignalLost"),
        PlaybackInterruptionKind.Reconnecting =>
            LocalizationService.Format("PlayerNoticeReconnecting", notice.Attempt, notice.Budget),
        PlaybackInterruptionKind.SwitchingQuality => LocalizationService.Get("PlayerNoticeQuality"),
        // The terminal state reuses the status line's own word: it is the same sentence about the same
        // channel, and a second translation of it would only be a second thing to keep in step.
        _ => LocalizationService.Get("PlayerUnavailable")
    };
}
