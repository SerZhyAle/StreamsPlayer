namespace StreamsPlayer.Core;

/// <summary>
/// The status of reading a broadcast resource document (`BROADCAST-RESOURCE` contract, schemaVersion 1).
/// The shape deliberately mirrors <see cref="ChannelShareStatus"/>: an import surface answers a refusal
/// with one sentence, never with an exception.
/// </summary>
public enum BroadcastResourceStatus
{
    /// <summary>A valid v1 document; <see cref="BroadcastResourceRead.Resource"/> carries it.</summary>
    Ok,

    /// <summary>The text is not a broadcast resource document at all (a random JSON file, an empty read).</summary>
    NotResourceDocument,

    /// <summary>Unmistakably a broadcast resource, written by a version this build cannot honour (schemaVersion above 1).</summary>
    UnsupportedVersion,

    /// <summary>A broadcast resource this build cannot read: a v1 document that is malformed, or a protocol this build does not speak.</summary>
    InvalidDocument
}

/// <summary>
/// The outcome of reading a broadcast resource document. <see cref="Resource"/> is <see langword="null"/>
/// for every status other than <see cref="BroadcastResourceStatus.Ok"/>, so a refusal never needs a null
/// check on the way to its sentence.
/// </summary>
public sealed record BroadcastResourceRead(BroadcastResourceStatus Status, BroadcastResource? Resource)
{
    public static readonly BroadcastResourceRead NotResourceDocument = new(BroadcastResourceStatus.NotResourceDocument, null);
    public static readonly BroadcastResourceRead UnsupportedVersion = new(BroadcastResourceStatus.UnsupportedVersion, null);
    public static readonly BroadcastResourceRead InvalidDocument = new(BroadcastResourceStatus.InvalidDocument, null);

    public bool IsAccepted => Status == BroadcastResourceStatus.Ok && Resource is not null;
}

/// <summary>
/// One address a broadcast resource can be reached on. The URL is COMPOSED from the three facts
/// (<c>http://{host}:{port}{path}</c>, IPv6 bracketed) and never transmitted: one path, many addresses,
/// is what lets the resource survive a LAN address change.
/// </summary>
public sealed record BroadcastAccessPath(string Kind, string Host, int Port);

/// <summary>
/// A `.spres` broadcast resource document (the `BROADCAST-RESOURCE` contract, schemaVersion 1): the
/// hand-off Fast Media Sorter writes for one published resource of its HTTP broadcast gateway. A sibling
/// of `.fmscfg`, not a version of it - that document describes an SFTP resource for the Android app.
/// </summary>
/// <remarks>
/// Ordering of <see cref="AccessPaths"/> is advice; racing them is the contract - an importer that plays
/// <c>accessPaths[0]</c> fails the day the PC moves to a different subnet. Unknown fields are ignored and
/// unknown <see cref="AccessPaths"/> kinds are skipped at read time: that is the additive channel for v2.
/// </remarks>
public sealed record BroadcastResource(
    int SchemaVersion,
    string ResourceName,
    string MediaKind,
    bool Playlist,
    int ItemCount,
    IReadOnlyList<BroadcastAccessPath> AccessPaths,
    string Path,
    long DurationSeconds,
    DateTimeOffset? ExpiresAt,
    string AccessNote,
    DateTimeOffset? CreatedAt)
{
    /// <summary>The contract version this build speaks.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>True when the document carries a lifetime (false = until revoked).</summary>
    public bool HasLifetime => ExpiresAt is not null;

    /// <summary>True when this resource is past its stated lifetime at <paramref name="now"/>.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expires && now >= expires;
}
