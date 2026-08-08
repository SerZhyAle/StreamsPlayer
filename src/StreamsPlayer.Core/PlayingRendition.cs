using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0077: the rendition an engine says is on screen right now, as pixels.
/// </summary>
/// <remarks>
/// Resolution and nothing else, by the owner's decision. The ceiling reaches both engines as a
/// resolution (<c>:adaptive-maxwidth</c>/<c>-maxheight</c>, <c>MaxVerticalResolutionCustom</c>), so the
/// request and the result are measured with one ruler and can be compared without a translation step.
/// Bandwidth is deliberately absent rather than merely unused: libvlc reports zero for every adaptive
/// track, so a field for it would carry nothing but a zero that later reads as a fact.
/// </remarks>
public readonly record struct VideoRendition(int Width, int Height)
{
    /// <summary>How a rendition is spelled everywhere it is logged.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
}

/// <summary>Why a rendition line was written: the leg's first answer, or a change inside one leg.</summary>
public enum RenditionCause
{
    /// <summary>The first answer this open produced - including "none", when the engine had none.</summary>
    Opened,

    /// <summary>The engine moved to another rendition without re-opening the media.</summary>
    Switched
}

/// <summary>How what is on screen compares with the ceiling the media was opened with.</summary>
public enum CeilingCompliance
{
    /// <summary>The engine reported nothing, so nothing can be concluded - never read as compliance.</summary>
    Unknown,

    /// <summary>No ceiling was applied to this open, so there is nothing to break.</summary>
    NoCeiling,

    /// <summary>The rendition on screen fits inside the ceiling.</summary>
    Within,

    /// <summary>The rendition on screen exceeds the ceiling in at least one dimension.</summary>
    Above
}

/// <summary>One observation worth writing down; <see cref="To"/> is null for "the engine did not say".</summary>
public readonly record struct RenditionObservation(
    RenditionCause Cause,
    VideoRendition? From,
    VideoRendition? To);

/// <summary>
/// SP-0077: turns a stream of readings into the few that are worth a log line.
/// </summary>
/// <remarks>
/// <para>The player asks its engine what is on screen on the two-second stats tick it already runs, and
/// most of those readings repeat the previous one. This decides which of them says something new, so the
/// window can log a change rather than a sample.</para>
/// <para>Legs, not resets. Every reading carries the number of the open it belongs to - the counter
/// <c>PlayerWindow.StartMedia</c> already increments - instead of the caller resetting this object when
/// it opens a media. That is not a style choice: two of the three callers of <c>StartMedia</c> run on a
/// worker thread, so a reset would be the one piece of this object's state written off the UI thread,
/// while the leg number is an <c>int</c> that already crosses that boundary. It also makes the
/// distinction the ticket asks for fall out for free - a rendition that differs across a leg boundary is
/// a re-open, and only a change inside one leg is a switch.</para>
/// <para>Single-threaded by contract, like <see cref="AdaptiveQualityGovernor"/>: every call comes from
/// the player's stats tick.</para>
/// </remarks>
public sealed class PlayingRenditionTracker
{
    private int _leg = -1;
    private bool _known;
    private bool _silenceReported;

    /// <summary>The last rendition the engine actually named for the current leg, if any.</summary>
    public VideoRendition? Shown { get; private set; }

    /// <summary>
    /// One reading for the open numbered <paramref name="leg"/>. Returns what should be logged, or null
    /// when this reading says nothing new.
    /// </summary>
    public RenditionObservation? Observe(int leg, VideoRendition? reading)
    {
        if (leg != _leg)
        {
            // A different media. Its rendition is not comparable with the previous leg's, and reporting
            // the difference as a switch would be exactly the confusion criterion 2 forbids.
            _leg = leg;
            _known = false;
            _silenceReported = false;
            Shown = null;
        }

        // Both engines answer with zeroes before they have a picture, and a "0x0" in the log is worse
        // than an absent value: it reads as a rendition rather than as the absence of one.
        var named = reading is { Width: > 0, Height: > 0 } value ? value : (VideoRendition?)null;
        if (named is not { } rendition)
        {
            if (_known || _silenceReported)
            {
                // Either this leg already has an answer - the vout is briefly rebuilt during a stall and
                // answers nothing, which is not a rendition change and must not clear Shown - or the
                // silence has already been said out loud once.
                return null;
            }

            _silenceReported = true;
            return new RenditionObservation(RenditionCause.Opened, null, null);
        }

        if (_known && Shown == rendition)
        {
            return null; // the steady state, which is most of a session
        }

        var previous = Shown;
        Shown = rendition;
        // Still Opened after a silence line: this leg's opening rendition was never established, so
        // nothing switched. Only a value replacing a value is a switch.
        var cause = _known ? RenditionCause.Switched : RenditionCause.Opened;
        _known = true;
        return new RenditionObservation(cause, cause == RenditionCause.Switched ? previous : null, rendition);
    }

    /// <summary>
    /// How <paramref name="shown"/> stands against <paramref name="ceiling"/>.
    /// </summary>
    /// <remarks>
    /// Either dimension, because that is how the limit is enforced: libvlc's adaptive demuxer excludes a
    /// rendition whose width <em>or</em> height exceeds the option, so a rendition that is over in one of
    /// them is over. Equal to the ceiling is <see cref="CeilingCompliance.Within"/> - the ceiling is a
    /// rung of the ladder and the engine is meant to be able to play it.
    /// </remarks>
    public static CeilingCompliance Compare(VideoRendition? shown, StreamQualityRung? ceiling)
    {
        if (shown is not { } rendition)
        {
            return CeilingCompliance.Unknown;
        }

        if (ceiling is not { } limit)
        {
            return CeilingCompliance.NoCeiling;
        }

        return rendition.Width > limit.Width || rendition.Height > limit.Height
            ? CeilingCompliance.Above
            : CeilingCompliance.Within;
    }
}
