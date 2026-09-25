namespace StreamsPlayer.Core;

/// <summary>SP-0075: one programme of a schedule channel. Times keep the broadcaster's offset.</summary>
public sealed record TvProgramme(DateTimeOffset Start, DateTimeOffset Stop, string Title);

/// <summary>
/// SP-0075: one channel of the downloaded schedule - the source's own identifier, the names it publishes
/// for it, and its programmes ordered by start.
/// </summary>
public sealed record TvScheduleChannel(string Id, IReadOnlyList<string> Names, IReadOnlyList<TvProgramme> Programmes);

/// <summary>
/// SP-0075: what one explicit download left on disk. <see cref="SchemaVersion"/> gates the reader: a file
/// written by a later build reads as "no schedule", never as a half-understood one.
/// </summary>
public sealed record TvScheduleDocument(
    int SchemaVersion,
    string SourceUrl,
    DateTimeOffset FetchedAt,
    IReadOnlyList<TvScheduleChannel> Channels)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// SP-0075: the user's own decision for one catalog channel, keyed by stream URL. A null
/// <see cref="ScheduleChannelId"/> is an explicit "this channel has no schedule", which beats the
/// automatic name match; absence of a binding means "match automatically".
/// </summary>
public sealed record TvScheduleBinding(string Url, string? ScheduleChannelId);

/// <summary>The programme on air now and the one after it; either may be absent.</summary>
public sealed record TvScheduleNowNext(TvProgramme? Now, TvProgramme? Next)
{
    public bool IsEmpty => Now is null && Next is null;
}

/// <summary>
/// SP-0075: the disk and memory appetite of the schedule, fixed in advance rather than discovered
/// (strategic constraint). The caps multiply out to a stored document of at most a few tens of
/// megabytes in the worst case; a typical country guide keeps well under one.
/// </summary>
public static class TvScheduleLimits
{
    /// <summary>The downloaded body, compressed or not. A larger source is refused before it is read.</summary>
    public const long MaximumDownloadBytes = 64L * 1024 * 1024;

    /// <summary>What the parser will read after decompression - a gzip bomb stops here.</summary>
    public const long MaximumDecompressedBytes = 512L * 1024 * 1024;

    /// <summary>How far ahead of the download moment programmes are kept.</summary>
    public static readonly TimeSpan WindowAhead = TimeSpan.FromHours(36);

    /// <summary>Programmes kept across all channels; the earliest win when a source offers more.</summary>
    public const int MaximumProgrammes = 60_000;

    public const int MaximumChannels = 20_000;
    public const int MaximumNamesPerChannel = 8;
    public const int MaximumTextLength = 120;

    /// <summary>A transfer that delivers nothing for this long is abandoned.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// SP-0129: how long the response head may take. The silence bound covers only the body, and the
    /// client this download shares has no timeout of its own.
    /// </summary>
    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How close to its end the stored data may come before the product suggests an update. Only a
    /// suggestion: nothing is ever fetched because of it.
    /// </summary>
    public static readonly TimeSpan RunningOutMargin = TimeSpan.FromHours(3);
}

public static class TvScheduleQueries
{
    /// <summary>
    /// The programme covering <paramref name="now"/> and the one that follows it, from programmes ordered
    /// by start. A gap in the schedule yields no current programme but still the next one; data that has
    /// ended yields nothing, which the interface shows as nothing rather than as an error.
    /// </summary>
    public static TvScheduleNowNext NowAndNext(IReadOnlyList<TvProgramme> programmes, DateTimeOffset now)
    {
        TvProgramme? current = null;
        for (var index = 0; index < programmes.Count; index++)
        {
            var programme = programmes[index];
            if (programme.Stop <= now)
            {
                continue;
            }

            if (programme.Start <= now)
            {
                current = programme;
                continue;
            }

            return new TvScheduleNowNext(current, programme);
        }

        return new TvScheduleNowNext(current, null);
    }
}
