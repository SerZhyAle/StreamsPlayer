namespace StreamsPlayer.Core;

/// <summary>
/// SP-0172: what a status document turned out to be, so a broadcaster whose server answers with
/// something other than a status document is not reported the same as one whose document simply
/// lists other mounts than the one being played.
/// </summary>
public enum IcecastStatusParse
{
    /// <summary>The payload is not an Icecast status document: unreadable JSON, or no <c>icestats</c> object.</summary>
    NotStatusDocument,

    /// <summary>A readable status document whose sources do not include the mount being played.</summary>
    NoMatchingMount,

    /// <summary>The playing mount was found; <c>title</c> carries its current (possibly empty) title.</summary>
    MatchedMount
}
