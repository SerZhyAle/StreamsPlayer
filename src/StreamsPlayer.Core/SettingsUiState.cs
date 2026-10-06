using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0191: the settings window's private navigation context, under <c>WINDOWS-UI</c> section 3.3 -
/// the last page, each group's expansion and each page's viewport, keyed by stable internal IDs.
/// </summary>
/// <remarks>
/// <para>UI context is private navigation state, not an exported preference: it lives in its own
/// small file and never in <see cref="CatalogState"/>, for the same two reasons the player geometry
/// and the quality memory are separate (a save there rewrites the whole ~15 MB catalog document, and
/// the refresh path owns that document's contents).</para>
///
/// <para>The viewport is remembered as the stable ID of the topmost visible group plus a pixel
/// offset <em>within</em> that group, never as a bare pixel offset into the page: translated
/// captions change every height above the anchor, so only a group ID survives a language change
/// (WINDOWS-UI section 3.3). A group ID the current markup no longer has falls back to the page
/// top, and a page index past the page count falls back to the first page - the markup may have
/// changed under an older file.</para>
///
/// <para>The window rectangle is the user's normal (restore) rectangle only. A maximized close
/// writes nothing, so the remembered normal rectangle is never overwritten by a screen-sized one
/// (<c>APP-BEHAVIOUR</c> rule 10). Restoring caps it to the surviving monitor's work area.</para>
/// </remarks>
public sealed record SettingsUiState
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Index of the page last selected, or 0. Past-the-end values fall back to 0 at restore.</summary>
    public int LastPageIndex { get; init; }

    /// <summary>Group expansion by stable group ID; absent reads as expanded.</summary>
    public Dictionary<string, bool> Groups { get; init; } = [];

    /// <summary>Per page: the ID of the group that was topmost and the offset into it, in device-independent pixels.</summary>
    public Dictionary<string, SettingsViewportAnchor> Viewports { get; init; } = [];

    /// <summary>The window's last normal rectangle, or null when it was never remembered.</summary>
    public SettingsWindowRectangle? Window { get; init; }
}

/// <summary>A viewport anchor: the topmost visible group's stable ID and the offset into that group.</summary>
public sealed record SettingsViewportAnchor(string GroupId, double OffsetWithinGroup);

/// <summary>The remembered normal rectangle of a window, in device-independent pixels.</summary>
public sealed record SettingsWindowRectangle(double Left, double Top, double Width, double Height)
{
    /// <summary>A rectangle a display no longer offers is still a rectangle; usability is decided against the work area at restore.</summary>
    [JsonIgnore]
    public bool IsUsable => Width >= 200 && Height >= 200;
}
