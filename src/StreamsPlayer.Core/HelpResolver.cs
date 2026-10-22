namespace StreamsPlayer.Core;

/// <summary>Where a help request lands: a help id and, optionally, an anchor inside its page.</summary>
public sealed record HelpTarget(string SurfaceId, string HelpId, string? Anchor);

/// <summary>
/// SP-0230: the one resolver of CONTEXT-HELP rule 17. Every F1 handler and every help button asks this and
/// nothing else, so the key and the button cannot disagree (rule 3).
/// </summary>
public static class HelpResolver
{
    /// <summary>
    /// A chain of <c>inherit</c> rows longer than this is a cycle or a mistake; resolving gives up rather than loop.
    /// </summary>
    private const int MaximumInheritHops = 8;

    /// <summary>
    /// The target for a surface, or null when the surface has no help (form <c>none</c>, an unknown id, a broken chain).
    /// </summary>
    /// <param name="map">The help map.</param>
    /// <param name="surfaceId">The surface the user asked from.</param>
    /// <param name="elementAnchor">
    /// The anchor of the element that has the focus, or of the part of the surface on screen (a settings page),
    /// when it has its own entry. It wins over the row's own anchor: the element is what the user pointed at.
    /// </param>
    public static HelpTarget? Resolve(HelpMap map, string surfaceId, string? elementAnchor = null)
    {
        var current = map.Find(surfaceId);
        string? inheritedAnchor = null;
        for (var hop = 0; current is not null && hop <= MaximumInheritHops; hop++)
        {
            switch (current.Form)
            {
                case HelpForm.None:
                    return null;
                case HelpForm.Inherit:
                    // The first row in the chain that names an anchor decides where the parent's page opens.
                    inheritedAnchor ??= current.Anchor;
                    current = current.Parent is null ? null : map.Find(current.Parent);
                    continue;
                default:
                    if (string.IsNullOrEmpty(current.Help))
                    {
                        return null;
                    }

                    var anchor = !string.IsNullOrEmpty(elementAnchor) ? elementAnchor : inheritedAnchor ?? current.Anchor;
                    return new HelpTarget(surfaceId, current.Help, string.IsNullOrEmpty(anchor) ? null : anchor);
            }
        }

        return null;
    }
}
