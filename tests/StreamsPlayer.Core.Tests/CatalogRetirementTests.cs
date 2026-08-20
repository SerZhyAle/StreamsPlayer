using System.IO.Compression;
using System.Net;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0089, source contract item D: absence of a URL from a bank build is authority to stop offering a
/// channel and nothing more. It is not authority to delete the pin, the collection membership or the
/// listening history the user built on top of it.
/// </summary>
/// <remarks>
/// The rule exists because of a measured incident rather than a worry. On 2026-08-19 the published bank
/// went from 19 534 rows to 17 628; of the 1 906 that left, 1 512 carried an <c>unknown</c> verdict - the
/// producer failing to measure the row, not judging it - and 1 321 belonged to one provider whose
/// stations still decoded audio the next day, 20 out of 20 sampled. None of those deletions was a
/// decision about a channel. Worse, the loss was unrecoverable from either side: both ends keyed on
/// absence and minted a fresh identity when a URL came back, so republishing the identical bytes restored
/// not one pin. The producer has fixed its probe, but a producer defect must not be able to reach through
/// the format and destroy user data, and the producer cannot be the only guard against the producer.
///
/// Every test here is about a <em>catalog</em> row. User rows are already untouchable by the merge contract.
/// </remarks>
public sealed class CatalogRetirementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Gone = "https://example.test/gone";
    private const string Offered = "https://example.test/offered";

    [Fact]
    public void Merge_DeletesAMissingRowThatCarriesNothingTheUserMade()
    {
        // The catalog is not an archive. Retirement is for rows where deletion destroys something; a row
        // nobody ever touched still goes, or the state grows by the 332 rows a single bank turn drops.
        var untouched = Channel(Gone);

        var result = CatalogMerger.Merge([untouched], [Entry(Offered)], Now, channelsWithUserData: new HashSet<Guid>());

        Assert.Equal(Offered, Assert.Single(result.Channels).Url);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Retired);
    }

    [Fact]
    public void Merge_RetiresRatherThanDeletesAPinnedRowTheBankStoppedListing()
    {
        var pinned = Channel(Gone) with { Pinned = true };

        var result = CatalogMerger.Merge([pinned], [Entry(Offered)], Now, channelsWithUserData: new HashSet<Guid>());

        var survivor = Assert.Single(result.Channels, channel => channel.Id == pinned.Id);
        Assert.True(survivor.Pinned);
        Assert.Equal(Now, survivor.RetiredAt);
        // Not counted as removed: the row did not leave, it stopped being offered. A support report has
        // to be able to tell those apart, which is the whole reason the counts are separate.
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Retired);
    }

    [Theory]
    [InlineData("collection")]
    [InlineData("history")]
    public void Merge_RetiresARowProtectedFromOutsideTheChannelList(string protection)
    {
        // Pinning is visible on the row itself; collection membership and history are not, which is
        // exactly why they were the two that would have been lost silently.
        var member = Channel(Gone);
        var state = new CatalogState
        {
            Channels = [member],
            Collections = protection == "collection"
                ? [new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning", ChannelIds = [member.Id] }]
                : [],
            ListeningHistory = protection == "history"
                ? [new ListeningHistoryEntry
                    {
                        ChannelId = member.Id,
                        Title = "Title",
                        MediaKind = MediaKind.Audio,
                        LastPlayedAt = Now
                    }]
                : []
        };

        var result = CatalogMerger.Merge(
            state.Channels,
            [Entry(Offered)],
            Now,
            channelsWithUserData: UserAuthoredChannels.Identify(state));

        Assert.Equal(Now, Assert.Single(result.Channels, channel => channel.Id == member.Id).RetiredAt);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Merge_KeepsTheOriginalRetirementMomentAcrossFurtherRefreshes()
    {
        // "When the bank stopped offering it", not "when we last looked". A moment that rewrites itself
        // on every refresh answers no question anyone would ask of it.
        var pinned = Channel(Gone) with { Pinned = true };

        var first = CatalogMerger.Merge([pinned], [Entry(Offered)], Now, channelsWithUserData: new HashSet<Guid>());
        var second = CatalogMerger.Merge(first.Channels, [Entry(Offered)], Now.AddDays(3), channelsWithUserData: new HashSet<Guid>());

        Assert.Equal(Now, Assert.Single(second.Channels, channel => channel.Id == pinned.Id).RetiredAt);
        Assert.Equal(1, second.Retired);
    }

    [Fact]
    public void Merge_ReattachesUserDataWhenTheUrlReturns()
    {
        // The full cycle the ticket asks for: present, absent, present again. Nothing is re-attached by
        // hand - the row kept its id through the absence, so every reference to it stayed correct and the
        // merge only has to say the channel is on offer again.
        var pinned = Channel(Gone) with { Pinned = true, SortIndex = -4 };
        var collection = new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning", ChannelIds = [pinned.Id] };
        var state = new CatalogState { Channels = [pinned], Collections = [collection] };

        var absent = CatalogMerger.Merge(
            state.Channels,
            [Entry(Offered)],
            Now,
            channelsWithUserData: UserAuthoredChannels.Identify(state));

        var returned = CatalogMerger.Merge(
            absent.Channels,
            [Entry(Offered), Entry(Gone, "Renamed while away")],
            Now.AddDays(1),
            channelsWithUserData: UserAuthoredChannels.Identify(state with { Channels = [.. absent.Channels] }));

        var revived = Assert.Single(returned.Channels, channel => channel.Url == Gone);
        Assert.Equal(pinned.Id, revived.Id);
        Assert.Null(revived.RetiredAt);
        Assert.True(revived.Pinned);
        Assert.Equal(-4, revived.SortIndex);
        // Metadata that moved on while the channel was away rides in with the return.
        Assert.Equal("Renamed while away", revived.Title);
        // No duplicate, and the collection still names a channel that exists.
        Assert.Equal(2, returned.Channels.Count);
        Assert.Equal(0, returned.Retired);
        Assert.Contains(collection.ChannelIds, id => returned.Channels.Any(channel => channel.Id == id));
    }

    [Fact]
    public void Merge_WithoutAProtectedSetStillHonoursThePinOnTheRow()
    {
        // A caller that forgets the argument must not silently opt out of the whole rule. It loses the
        // protections it did not pass; it does not lose the one visible on the row.
        var result = CatalogMerger.Merge([Channel(Gone) with { Pinned = true }], [Entry(Offered)], Now);

        Assert.Equal(1, result.Retired);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Identify_CollectsPinsCollectionsAndHistoryAndNothingElse()
    {
        var pinned = Channel("https://example.test/pinned") with { Pinned = true };
        var collected = Channel("https://example.test/collected");
        var listened = Channel("https://example.test/listened");
        // Played and hidden are the two that look like user data and are not: an outcome stamp is
        // something the application wrote about itself, and hiding survives the row by URL on its own.
        var played = Channel("https://example.test/played") with
        {
            LastPlayedAt = Now,
            LastPlayOutcome = PlayOutcome.Ok
        };
        var hidden = Channel("https://example.test/hidden");

        var authored = UserAuthoredChannels.Identify(new CatalogState
        {
            Channels = [pinned, collected, listened, played, hidden],
            Collections = [new ChannelCollection { Id = Guid.NewGuid(), Name = "C", ChannelIds = [collected.Id] }],
            ListeningHistory =
            [
                new ListeningHistoryEntry
                {
                    ChannelId = listened.Id,
                    Title = "Title",
                    MediaKind = MediaKind.Audio,
                    LastPlayedAt = Now
                }
            ],
            HiddenCatalogUrls = [hidden.Url]
        });

        Assert.Equal(3, authored.Count);
        Assert.Contains(pinned.Id, authored);
        Assert.Contains(collected.Id, authored);
        Assert.Contains(listened.Id, authored);
        Assert.DoesNotContain(played.Id, authored);
        Assert.DoesNotContain(hidden.Id, authored);
    }

    /// <summary>
    /// The same cycle through the real refresh path, which is what actually runs: the service is what
    /// reads the state, computes the protected set and hands it to the merge, and a rule wired only in
    /// the merger would pass every unit test above while deleting the user's pins in production.
    /// </summary>
    [Fact]
    public async Task Refresh_PinSurvivesTheChannelLeavingTheBankAndComingBack()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(
                BankZip(Gone, Offered),
                BankZip(Offered),
                BankZip(Gone, Offered));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var seeded = await service.RefreshAsync(new CatalogState());
            var pinned = Assert.Single(seeded.State.Channels, channel => channel.Url == Gone);
            var collection = new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning", ChannelIds = [pinned.Id] };
            var withUserData = seeded.State with
            {
                Channels = [.. seeded.State.Channels.Select(
                    channel => channel.Id == pinned.Id ? channel with { Pinned = true } : channel)],
                Collections = [collection]
            };

            var absent = await service.RefreshAsync(withUserData);

            var retired = Assert.Single(absent.State.Channels, channel => channel.Id == pinned.Id);
            Assert.NotNull(retired.RetiredAt);
            Assert.True(retired.Pinned);
            Assert.Equal(0, absent.Removed);
            Assert.Equal(1, absent.Retired);
            Assert.Contains(collection.ChannelIds, id => absent.State.Channels.Any(channel => channel.Id == id));

            var returned = await service.RefreshAsync(absent.State);

            var revived = Assert.Single(returned.State.Channels, channel => channel.Url == Gone);
            Assert.Equal(pinned.Id, revived.Id);
            Assert.Null(revived.RetiredAt);
            Assert.True(revived.Pinned);
            Assert.Equal(2, returned.State.Channels.Count);
            Assert.Equal(0, returned.Retired);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Refresh_StillDeletesAMissingRowNobodyTouched()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(BankZip(Gone, Offered), BankZip(Offered));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var seeded = await service.RefreshAsync(new CatalogState());
            var pruned = await service.RefreshAsync(seeded.State);

            Assert.Equal(Offered, Assert.Single(pruned.State.Channels).Url);
            Assert.Equal(1, pruned.Removed);
            Assert.Equal(0, pruned.Retired);
        }
        finally
        {
            Delete(directory);
        }
    }

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Title",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = Now
    };

    private static CatalogEntry Entry(string url, string title = "Title") =>
        new(title, url, MediaKind.Audio, null, null, null, null, null, null);

    private static byte[] BankZip(params string[] urls)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("streams.csv").Open();
            var csv = new StringBuilder("name,url,media_kind\n");
            foreach (var url in urls)
            {
                csv.Append("Title,").Append(url).Append(",AUDIO\n");
            }

            stream.Write(Encoding.UTF8.GetBytes(csv.ToString()));
        }

        return buffer.ToArray();
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class QueuedZipHandler(params byte[][] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responses[Math.Min(_index++, responses.Length - 1)])
            });
    }
}
