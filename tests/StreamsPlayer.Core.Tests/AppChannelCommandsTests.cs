using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0274: channel add - the add-by-address rules (duplicate, scheme, collection), the row it writes, the answer it gives,
// and the merge that must keep the row. The App executes; these decide.
public sealed class AppChannelCommandsTests
{
    private const string Radio = "https://example.org/live.mp3";

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NewId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    private static StreamChannel Existing(string url, SourceOrigin origin = SourceOrigin.Catalog, long sortIndex = 4) => new()
    {
        Id = Guid.Parse("1f8fad5b-d9cb-469f-a165-70867728950e"),
        Url = url,
        Title = "Existing",
        MediaKind = MediaKind.Audio,
        SourceOrigin = origin,
        SortIndex = sortIndex,
        AddedAt = Now
    };

    private static ChannelCollection Collection(string name, params Guid[] members) => new()
    {
        Id = Guid.Parse("2f8fad5b-d9cb-469f-a165-70867728950e"),
        Name = name,
        ChannelIds = [.. members]
    };

    // ---- the parser ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ChannelAddTakesAnAddressAndTheOptionalTitleCollectionAndResult()
    {
        var parse = Parse("channel", "add", "--url", Radio, "--title", "Test", "--collection", "Morning", "--result", "answer.json");

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.Equal("channel add", parse.Entry!.Name);
        Assert.Equal(Radio, parse.Value("url"));
        Assert.Equal("Test", parse.Value("title"));
        Assert.Equal("Morning", parse.Value("collection"));
    }

    [Theory]
    [InlineData("channel", "add")]
    [InlineData("channel", "add", "--url")]
    [InlineData("channel", "add", "--url", "")]
    [InlineData("channel", "add", "--url", "   ")]
    [InlineData("channel", "add", "--url", Radio, "--url", "https://example.org/other.mp3")]
    [InlineData("channel", "add", "--url", Radio, "--title", "A", "--title", "B")]
    [InlineData("channel", "add", "--url", Radio, "--collection", "A", "--collection", "B")]
    [InlineData("channel", "add", "--url", "ftp://example.org/file.mp3")]
    [InlineData("channel", "add", "--url", "file:///C:/music/song.mp3")]
    [InlineData("channel", "add", "--url", "javascript:alert(1)")]
    [InlineData("channel", "add", "--url", "example.org/live.mp3")]
    [InlineData("channel", "add", "--url", Radio, "--bogus", "x")]
    public void ChannelAddRefusesAMissingRepeatedOrNonStreamAddressWithExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
        Assert.Equal(AppCommandExit.Usage, parse.ToRefusal("id").ExitCode);
    }

    [Theory]
    [InlineData("https://example.org/live.mp3")]
    [InlineData("http://example.org:8000/stream")]
    [InlineData("rtsp://camera.example.org:554/live")]
    [InlineData("https://listener:secret@example.org/live.mp3")]
    public void ChannelAddAcceptsHttpHttpsAndRtspAddresses(string url)
    {
        var parse = Parse("channel", "add", "--url", url);

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
    }

    // ---- the row ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewAddressBecomesAManualRowWithTheDialogsDefaultsAtTheEndOfTheList()
    {
        var before = new CatalogState { Channels = [Existing("https://other.example.org/a.mp3", sortIndex: 7)] };

        var plan = AppChannelCommands.Plan(before.Channels, before.Collections, "  " + Radio + " ", null, null, NewId, Now);
        var row = plan.Channel!;

        Assert.Equal(AppChannelAddOutcome.Added, plan.Outcome);
        Assert.Equal(NewId, row.Id);
        Assert.Equal(Radio, row.Url);
        Assert.Equal("example.org", row.Title);
        Assert.Equal(StreamMediaKindClassifier.Classify(Radio), row.MediaKind);
        Assert.Equal(SourceOrigin.Manual, row.SourceOrigin);
        Assert.Equal(8, row.SortIndex);
        Assert.Equal(Now, row.AddedAt);
        Assert.True(row.IsLive);
        Assert.Null(plan.CollectionId);
    }

    [Fact]
    public void TheTitleGivenIsTheTitleAndAnEmptyListStartsAtZero()
    {
        var plan = AppChannelCommands.Plan([], [], Radio, "  Test  ", null, NewId, Now);

        Assert.Equal("Test", plan.Channel!.Title);
        Assert.Equal(0, plan.Channel!.SortIndex);
    }

    [Theory]
    [InlineData("https://EXAMPLE.org/live.mp3")]
    [InlineData("https://example.org:443/live.mp3")]
    [InlineData("https://listener:secret@example.org/live.mp3")]
    [InlineData("https://example.org/live.mp3")]
    public void AnAddressAlreadyInTheListIsNotDuplicatedWhateverItsOrigin(string address)
    {
        var existing = Existing("https://example.org/live.mp3", SourceOrigin.Catalog);
        var before = new CatalogState { Channels = [existing] };

        var plan = AppChannelCommands.Plan(before.Channels, before.Collections, address, null, null, NewId, Now);
        var after = AppChannelCommands.Apply(before, plan);
        var answer = AppChannelCommands.Answer("r1", plan);

        Assert.Equal(AppChannelAddOutcome.AlreadyPresent, plan.Outcome);
        Assert.Equal(existing.Id, plan.Channel!.Id);
        Assert.Same(before, after);
        Assert.Equal(AppCommandStatus.Done, answer.Status);
        Assert.False(answer.Data!["created"]!.GetValue<bool>());
        Assert.Equal(existing.Id.ToString("D"), answer.Data!["channelId"]!.GetValue<string>());
    }

    [Fact]
    public void AnAnswerNeverCarriesAnAddressOrItsLogin()
    {
        var plan = AppChannelCommands.Plan([], [], "https://listener:secret@example.org/live.mp3", null, null, NewId, Now);

        var answer = AppChannelCommands.Answer("r2", plan);

        Assert.DoesNotContain("secret", answer.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", answer.Data!.ToJsonString(), StringComparison.Ordinal);
    }

    // ---- the collection -----------------------------------------------------------------------------------------------

    [Fact]
    public void ACollectionIsNamedWithoutRegardToCaseAndTheNewRowJoinsIt()
    {
        var morning = Collection("Morning");
        var before = new CatalogState { Collections = [morning] };

        var plan = AppChannelCommands.Plan(before.Channels, before.Collections, Radio, null, "  morning ", NewId, Now);
        var after = AppChannelCommands.Apply(before, plan);

        Assert.Equal(AppChannelAddOutcome.Added, plan.Outcome);
        Assert.Equal(morning.Id, plan.CollectionId);
        Assert.Equal(new[] { NewId }, Assert.Single(after.Collections).ChannelIds);
        Assert.Single(after.Channels);
    }

    [Fact]
    public void AnExistingRowIsAddedToTheNamedCollectionOnceAndAgainIsNoChange()
    {
        var existing = Existing(Radio, SourceOrigin.Manual);
        var morning = Collection("Morning");
        var before = new CatalogState { Channels = [existing], Collections = [morning] };

        var first = AppChannelCommands.Apply(before, AppChannelCommands.Plan(before.Channels, before.Collections, Radio, null, "Morning", NewId, Now));
        var second = AppChannelCommands.Apply(first, AppChannelCommands.Plan(first.Channels, first.Collections, Radio, null, "Morning", NewId, Now));

        Assert.Equal(new[] { existing.Id }, Assert.Single(first.Collections).ChannelIds);
        Assert.Same(first, second);
        Assert.Single(second.Channels);
    }

    [Fact]
    public void AnUnknownCollectionIsRefusedWithTargetMissingAndChangesNothing()
    {
        var before = new CatalogState { Collections = [Collection("Morning")] };

        var plan = AppChannelCommands.Plan(before.Channels, before.Collections, Radio, null, "Evening", NewId, Now);
        var answer = AppChannelCommands.Answer("r3", plan);

        Assert.Equal(AppChannelAddOutcome.CollectionMissing, plan.Outcome);
        Assert.Null(plan.Channel);
        Assert.Same(before, AppChannelCommands.Apply(before, plan));
        // SP-0279, APP-COMMANDS item M: the thing the verb names does not exist - failed, not refused (rule 12).
        Assert.Equal(AppCommandStatus.Failed, answer.Status);
        Assert.Equal(AppCommandReason.TargetMissing, answer.Reason);
        Assert.Equal(AppCommandExit.NotReady, answer.ExitCode);
    }

    // ---- the merge and the manifest -----------------------------------------------------------------------------------

    [Fact]
    public void ALaterCatalogMergeKeepsTheManualRow()
    {
        var added = AppChannelCommands.Plan([], [], Radio, "Test", null, NewId, Now).Channel!;
        var bank = new CatalogEntry("Bank title", "https://bank.example.org/x.mp3", MediaKind.Audio, "News", "World", "english", "MT", null, 2);

        var result = CatalogMerger.Merge([added], [bank], Now.AddMinutes(1));

        var kept = Assert.Single(result.Channels, channel => channel.Id == added.Id);
        Assert.Equal(SourceOrigin.Manual, kept.SourceOrigin);
        Assert.Equal(Radio, kept.Url);
        Assert.Equal("Test", kept.Title);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void TheManifestDeclaresChannelAddAsASafeStartVerbWithTheDraftOptions()
    {
        var entry = AppCommandManifest.Embedded.Find("channel add");

        Assert.NotNull(entry);
        Assert.Equal("safe", entry.Risk);
        Assert.Equal("start", entry.ColdStart);
        Assert.Equal(10_000, entry.TimeoutMs);
        Assert.True(entry.Idempotent);
        Assert.Equal(new[] { "url", "url-file", "title", "collection", "result" }, entry.Args.Select(argument => argument.Name));
        // SP-0279, item H: the address is --url or --url-file, one of them (the manifest's oneOf), so neither is required alone.
        Assert.False(entry.Args.Single(argument => argument.Name == "url").Required);
        Assert.True(entry.Args.Single(argument => argument.Name == "url").Sensitive);
    }
}
