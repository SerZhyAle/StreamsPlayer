using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>SP-0274: what one <c>channel add</c> decides, before any window or disk is touched.</summary>
public enum AppChannelAddOutcome
{
    /// <summary>A new Manual row is appended to the list.</summary>
    Added,

    /// <summary>The address is already in the list, whatever its origin: nothing is duplicated.</summary>
    AlreadyPresent,

    /// <summary><c>--collection</c> names no collection: nothing is changed.</summary>
    CollectionMissing
}

/// <summary>
/// SP-0274: the decision of one <c>channel add</c>. <see cref="Channel"/> is the new row, or the row already in the list;
/// it is null only for <see cref="AppChannelAddOutcome.CollectionMissing"/>. <see cref="CollectionId"/> is the collection
/// the channel must be in, when <c>--collection</c> was given.
/// </summary>
public sealed record AppChannelAddPlan(AppChannelAddOutcome Outcome, StreamChannel? Channel, Guid? CollectionId);

/// <summary>
/// SP-0274: the sentences of <c>channel add</c>. English, the same in both processes (rule 6). Never an address, a title
/// or a login in one - an address with a credential must not reach a log or an answer.
/// </summary>
public static class AppChannelMessages
{
    public const string UrlOnce = "The command \"channel add\" takes the address once: --url or --url-file, not both.";

    public const string TitleOnce = "The command \"channel add\" takes --title at most once.";

    public const string CollectionOnce = "The command \"channel add\" takes --collection at most once.";

    public const string AddressNotLaunchable = "Only http, https and rtsp addresses can be added to the list.";

    public const string CollectionMissing =
        "No collection has this name; create it in StreamsPlayer first - this command never creates one.";

    public const string Added = "The stream is in My streams.";

    public const string AlreadyPresent = "The stream is already in the list.";

    public const string NotSaved = "The catalog could not be saved; the log has the details.";
}

/// <summary>
/// SP-0274: the <c>channel add</c> command's decisions that need no window - which options it takes, which row an address
/// names, what a new row holds, where it goes in the list and every answer. The App executes the one save; these rules are
/// tested without a player or a disk.
/// </summary>
/// <remarks>
/// The new row is the Add stream dialog's own save without the dialog (<c>APP-COMMANDS</c> rule 4): the same duplicate test
/// (<see cref="CatalogUrlIdentity.SameIdentity"/> over every row, whatever its origin), the same Manual origin and sort
/// position, and the dialog's defaults for every field the command cannot decide. A Manual row is outside every catalog
/// merge (STREAM-BANK rule 5), so no merge rule changes for it.
/// </remarks>
public static class AppChannelCommands
{
    public const string AddVerb = "channel add";

    /// <summary>
    /// The rules of <c>channel add</c> that the manifest alone cannot express (rule 3). <see langword="null"/> when the options
    /// are acceptable. Run by the one parser, so both processes judge a command line alike.
    /// </summary>
    public static AppPlaybackCommands.Violation? Check(AppCommandEntry entry, IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        string.Equals(entry.Name, AddVerb, StringComparison.Ordinal) ? CheckAdd(options) : null;

    /// <summary>
    /// Decides one add against the list as it is now. <paramref name="collectionName"/> is matched the way collections are
    /// named (trimmed, whitespace collapsed, case-insensitive); a name that matches no collection is never created.
    /// </summary>
    public static AppChannelAddPlan Plan(
        IReadOnlyList<StreamChannel> channels,
        IReadOnlyList<ChannelCollection> collections,
        string url,
        string? title,
        string? collectionName,
        Guid newId,
        DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var address = url.Trim();
        Guid? collectionId = null;
        if (collectionName is not null)
        {
            var wanted = ChannelCollections.NormalizeName(collectionName);
            var collection = wanted is null
                ? null
                : collections.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, wanted, StringComparison.CurrentCultureIgnoreCase));
            if (collection is null)
            {
                return new AppChannelAddPlan(AppChannelAddOutcome.CollectionMissing, null, null);
            }

            collectionId = collection.Id;
        }

        var existing = channels.FirstOrDefault(channel => CatalogUrlIdentity.SameIdentity(channel.Url, address));
        if (existing is not null)
        {
            return new AppChannelAddPlan(AppChannelAddOutcome.AlreadyPresent, existing, collectionId);
        }

        var row = new StreamChannel
        {
            Id = newId,
            Url = address,
            Title = string.IsNullOrWhiteSpace(title) ? LaunchableAddress.HostOf(address) : title.Trim(),
            MediaKind = StreamMediaKindClassifier.Classify(address),
            SourceOrigin = SourceOrigin.Manual,
            SortIndex = channels.Count == 0 ? 0 : channels.Max(channel => channel.SortIndex) + 1,
            AddedAt = addedAt,
            IsLive = true
        };
        return new AppChannelAddPlan(AppChannelAddOutcome.Added, row, collectionId);
    }

    /// <summary>
    /// The catalog after <paramref name="plan"/>. The same instance when the plan changes nothing, so a refusal or a plain
    /// duplicate writes nothing: the committer saves only a state it has not seen.
    /// </summary>
    public static CatalogState Apply(CatalogState state, AppChannelAddPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Channel is not { } row)
        {
            return state;
        }

        var addsRow = plan.Outcome == AppChannelAddOutcome.Added;
        var joinsCollection = plan.CollectionId is { } target &&
                              state.Collections.Any(collection => collection.Id == target && !collection.ChannelIds.Contains(row.Id));
        if (!addsRow && !joinsCollection)
        {
            return state;
        }

        var channels = state.Channels;
        if (addsRow)
        {
            channels = [.. channels, row];
        }

        var collections = state.Collections;
        if (joinsCollection)
        {
            collections = [.. ChannelCollections.AddChannel(state.Collections, plan.CollectionId!.Value, row.Id)];
        }

        return state with { Channels = channels, Collections = collections };
    }

    /// <summary>
    /// The answer of the command: <c>done</c> with <c>channelId</c> and <c>created</c> (never the address), or - for an unknown
    /// collection - <c>failed</c> with <c>target-missing</c>, exit 11 (the row of <c>APP-COMMANDS</c> rule 12: the thing the
    /// verb names does not exist; the command was understood and ran, so it is not a refusal).
    /// </summary>
    public static AppCommandResponse Answer(string requestId, AppChannelAddPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Channel is not { } row)
        {
            return AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady,
                AppChannelMessages.CollectionMissing);
        }

        var created = plan.Outcome == AppChannelAddOutcome.Added;
        var data = new JsonObject
        {
            ["channelId"] = row.Id.ToString("D"),
            ["created"] = created
        };
        return AppCommandResponse.Done(requestId, created ? AppChannelMessages.Added : AppChannelMessages.AlreadyPresent, data);
    }

    /// <summary>The outcome as the log names it (rule 16: the outcome, never a value).</summary>
    public static string OutcomeName(AppChannelAddOutcome outcome) => outcome switch
    {
        AppChannelAddOutcome.Added => "added",
        AppChannelAddOutcome.AlreadyPresent => "present",
        _ => "collection-missing"
    };

    private static AppPlaybackCommands.Violation? CheckAdd(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        // SP-0279, item H: the address is given by --url or by --url-file, exactly one of them and once (the manifest's oneOf).
        if (Count(options, "url") + Count(options, AppUrlFile.OptionName) != 1)
        {
            return Bad(AppChannelMessages.UrlOnce);
        }

        if (Count(options, "title") > 1)
        {
            return Bad(AppChannelMessages.TitleOnce);
        }

        if (Count(options, "collection") > 1)
        {
            return Bad(AppChannelMessages.CollectionOnce);
        }

        // An address read from --url-file is tested when the file is read (AppUrlFile), not here, where it is not known.
        return Count(options, "url") == 0 || StreamMediaKindClassifier.IsLaunchable(AppPlaybackCommands.Last(options, "url")!.Trim())
            ? null
            : Bad(AppChannelMessages.AddressNotLaunchable);
    }

    private static int Count(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values.Count : 0;

    private static AppPlaybackCommands.Violation Bad(string message) => new(AppCommandReason.BadArgument, message);
}
