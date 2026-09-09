using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0084: the player window opens where this channel's window was left, and at that size.
/// </summary>
/// <remarks>
/// A separate partial because none of it is playback. Nothing here touches the buffer, the recovery
/// budget, the quality ceiling or the control panel; the whole feature is two moments - place before the
/// window is seen, record after the user is done moving it - and keeping them out of the playback file
/// is what stops the next reader having to decide whether a window rectangle can affect a stream.
/// </remarks>
public partial class PlayerWindow
{
    // Set only when a remembered rectangle was applied. It gates the second pass: a window placed by
    // WindowStartupLocation must not be dragged around by a correction meant for a restored one.
    private bool _placedFromMemory;

    /// <summary>
    /// Places the window on this channel's remembered rectangle, if there is one, before it is shown.
    /// </summary>
    /// <remarks>
    /// Called instead of awaiting the memory inside the window, because a window that is already visible
    /// when its placement arrives is one the user watches jump. A channel with no memory is left alone
    /// and keeps <c>WindowStartupLocation="CenterOwner"</c> - the behaviour every channel has today, and
    /// the one acceptance criterion 2 asks for a never-opened channel to keep.
    /// <para>
    /// <paramref name="dpiSource"/> is the catalog window, lent because this one has no presentation
    /// source of its own until it is shown. If the remembered rectangle is on a monitor scaled
    /// differently from the catalog's, the work area comes back off by that ratio and so does the fit;
    /// <see cref="OnSourceInitialized"/> repeats the fit against this window's own transform, which is
    /// the pass that is actually right. Two passes rather than one for the same reason the compact panel
    /// needs two.
    /// </para>
    /// </remarks>
    internal void ApplyRememberedPlacement(Visual dpiSource)
    {
        if (PlayerGeometryFile.Recall(_channel.Url) is not { } remembered)
        {
            return;
        }

        _placedFromMemory = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Apply(Fit(remembered, dpiSource));
    }

    /// <summary>
    /// The second placement pass, once the window has a transform of its own.
    /// </summary>
    /// <remarks>
    /// This is where acceptance criteria 3 and 4 are actually met. The first pass measured the monitor
    /// through the catalog's scaling; by here the window knows its own, so a rectangle remembered at one
    /// DPI and restored at another is corrected before anything is drawn, and a rectangle whose monitor
    /// is gone has already been moved onto the nearest surviving one by
    /// <see cref="MonitorWorkArea.Around"/>. Both happen silently: the channel opens either way, which is
    /// what those criteria ask for.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_placedFromMemory)
        {
            return;
        }

        Apply(Fit(new ScreenRect(Left, Top, Width, Height), this));
    }

    /// <summary>
    /// Records where the window was left, on the way out.
    /// </summary>
    /// <remarks>
    /// Closing and not a drag: acceptance criterion 6 asks that moving and resizing cost no writes at
    /// all, and recording once per window satisfies that far more plainly than any debounce would. It is
    /// also what the ticket means by the memory being created by the fact of closing - a channel opened
    /// and never touched still records the rectangle it was closed at, and a channel never opened records
    /// nothing.
    /// <para>
    /// A window that is maximized or full-screen has no ordinary rectangle to record, and writing one
    /// would overwrite a good placement with a screen-sized one. Per the ticket's decision, such a close
    /// leaves the previous rectangle exactly as it was. <c>RestoreBounds</c> is deliberately not used to
    /// rescue it: full-screen here is entered as <c>Maximized</c>, and a window opened straight into it
    /// has never had a restored size the user chose, so what it would hand back is the default placement
    /// dressed up as a preference.
    /// </para>
    /// <para>
    /// The cache inside <see cref="PlayerGeometryFile"/> is updated synchronously by this call, so the
    /// last window to close wins even when several of this channel's windows close together on the way
    /// out of the application - the ticket's decision - and the next open in this session is placed
    /// correctly whether or not the disk write has landed.
    /// </para>
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_fullscreen || WindowState != WindowState.Normal)
        {
            return;
        }

        var rectangle = MonitorWorkArea.Placement(this);
        if (!PlayerWindowGeometry.IsUsable(rectangle))
        {
            return;
        }

        // Fire and forget on purpose: the disk write must not hold up a window the user has closed, and
        // its failure costs one placement. The in-memory half of the record has already happened
        // synchronously inside the call, which is the half this session still needs.
        _ = PlayerGeometryFile.RecordAsync(_channel.Url, rectangle, DateTimeOffset.UtcNow);
    }

    private ScreenRect Fit(ScreenRect wanted, Visual dpiSource) =>
        ScreenPlacement.Fit(wanted, MonitorWorkArea.Around(wanted, dpiSource), MinWidth, MinHeight);

    private void Apply(ScreenRect rectangle)
    {
        Left = rectangle.Left;
        Top = rectangle.Top;
        Width = rectangle.Width;
        Height = rectangle.Height;
    }
}
