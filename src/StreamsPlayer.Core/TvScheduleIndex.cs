namespace StreamsPlayer.Core;

/// <summary>
/// SP-0075: the schedule as the interface asks for it - by stream URL. Built once per schedule, binding
/// change or catalog change; every lookup afterwards is a dictionary read and a short scan.
/// </summary>
public sealed class TvScheduleIndex
{
    public static readonly TvScheduleIndex Empty = new(null, [], [], new Dictionary<string, TvScheduleBinding>());

    private readonly Dictionary<string, TvScheduleChannel> _byUrl;
    private readonly Dictionary<string, TvScheduleChannel> _byId;

    private TvScheduleIndex(
        TvScheduleDocument? document,
        Dictionary<string, TvScheduleChannel> byUrl,
        Dictionary<string, TvScheduleChannel> byId,
        IReadOnlyDictionary<string, TvScheduleBinding> bindings)
    {
        Document = document;
        _byUrl = byUrl;
        _byId = byId;
        Bindings = bindings;
        CoverageEnd = document?.Channels
            .SelectMany(channel => channel.Programmes)
            .Select(programme => (DateTimeOffset?)programme.Stop)
            .DefaultIfEmpty(null)
            .Max();
    }

    public TvScheduleDocument? Document { get; }
    public IReadOnlyDictionary<string, TvScheduleBinding> Bindings { get; }
    public bool HasSchedule => Document is not null;

    /// <summary>The last moment any stored programme covers; null without data.</summary>
    public DateTimeOffset? CoverageEnd { get; }

    /// <summary>Catalog channels that ended up with a schedule channel.</summary>
    public int MatchedCount => _byUrl.Count;

    /// <summary>Catalog channels a schedule could apply to - every picture channel.</summary>
    public int EligibleCount { get; private init; }

    public IReadOnlyList<TvScheduleChannel> Channels => Document?.Channels ?? [];

    public static TvScheduleIndex Build(
        TvScheduleDocument? document,
        IEnumerable<TvScheduleBinding> bindings,
        IEnumerable<StreamChannel> catalog)
    {
        var bindingsByUrl = new Dictionary<string, TvScheduleBinding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            bindingsByUrl[CatalogUrlIdentity.Normalize(binding.Url)] = binding;
        }

        var byUrl = new Dictionary<string, TvScheduleChannel>(StringComparer.Ordinal);
        var byId = new Dictionary<string, TvScheduleChannel>(StringComparer.Ordinal);
        var eligible = 0;
        if (document is not null)
        {
            foreach (var channel in document.Channels)
            {
                byId.TryAdd(channel.Id, channel);
            }

            var names = TvScheduleMatcher.BuildNameIndex(document.Channels);
            foreach (var channel in catalog)
            {
                if (TvScheduleMatcher.IsEligible(channel))
                {
                    eligible++;
                }

                if (TvScheduleMatcher.Resolve(channel, bindingsByUrl, names) is { } id &&
                    byId.TryGetValue(id, out var scheduleChannel))
                {
                    byUrl[CatalogUrlIdentity.Normalize(channel.Url)] = scheduleChannel;
                }
            }
        }

        return new TvScheduleIndex(document, byUrl, byId, bindingsByUrl) { EligibleCount = eligible };
    }

    /// <summary>The schedule channel bound to this stream, if any.</summary>
    public TvScheduleChannel? ChannelFor(string url) =>
        _byUrl.TryGetValue(CatalogUrlIdentity.Normalize(url), out var channel) ? channel : null;

    /// <summary>The user's own binding for this stream, or null when it is matched automatically.</summary>
    public TvScheduleBinding? BindingFor(string url) =>
        Bindings.TryGetValue(CatalogUrlIdentity.Normalize(url), out var binding) ? binding : null;

    public TvScheduleNowNext NowAndNext(string url, DateTimeOffset now) =>
        ChannelFor(url) is { } channel
            ? TvScheduleQueries.NowAndNext(channel.Programmes, now)
            : new TvScheduleNowNext(null, null);

    /// <summary>
    /// True when stored data is close to its end or already past it - the moment to suggest an update.
    /// Never a trigger for a download.
    /// </summary>
    public bool IsRunningOut(DateTimeOffset now) =>
        HasSchedule && (CoverageEnd is null || CoverageEnd.Value - now < TvScheduleLimits.RunningOutMargin);
}
