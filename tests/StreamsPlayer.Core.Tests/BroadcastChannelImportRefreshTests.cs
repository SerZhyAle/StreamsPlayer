using System.Globalization;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S14-3: re-importing a refreshed broadcast document updates the row it already made. What the user edited
/// stays; only what the document owns (addresses, lifetime, playlist flag, access note) follows it.
/// </summary>
public sealed class BroadcastChannelImportRefreshTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void A_reimport_keeps_what_the_user_edited_and_follows_the_document_where_it_owns()
    {
        var first = BroadcastChannelImport.Apply([], Resource("192.168.1.100", "Dune", "note one", false, "2026-11-01T00:00:00Z"), Now);
        var edited = first.Channel with
        {
            Title = "My Dune cut",
            Category = "Films",
            Topic = "Sci-fi",
            Language = "en",
            Country = "US",
            Homepage = "https://example.test/dune",
            Protocol = "http",
            Format = "ts",
            Bitrate = "4000",
            IsLive = true,
            Pinned = true,
            SortIndex = 7
        };

        var second = BroadcastChannelImport.Apply(
            [edited], Resource("10.0.0.7", "Dune - recut", "note two", true, "2026-12-01T00:00:00Z"), Now.AddDays(1));

        Assert.False(second.Added);
        var row = Assert.Single(second.Channels);
        Assert.Equal(row.Id, second.Channel.Id);

        // The user's: identity, metadata, pin, place and history. The name follows the refreshed document.
        Assert.Equal(edited.Id, row.Id);
        Assert.Equal("Dune - recut", row.Title);
        Assert.Equal("Films", row.Category);
        Assert.Equal("Sci-fi", row.Topic);
        Assert.Equal("en", row.Language);
        Assert.Equal("US", row.Country);
        Assert.Equal("https://example.test/dune", row.Homepage);
        Assert.Equal("http", row.Protocol);
        Assert.Equal("ts", row.Format);
        Assert.Equal("4000", row.Bitrate);
        Assert.True(row.IsLive);
        Assert.True(row.Pinned);
        Assert.Equal(7, row.SortIndex);
        Assert.Equal(Now, row.AddedAt);
        Assert.Equal(SourceOrigin.Imported, row.SourceOrigin);
        Assert.Equal("/b/tok", row.ResourceKey);

        // The document's: where to reach it, how long it lives, whether it is a playlist, what to say when it fails.
        Assert.Equal("http://10.0.0.7:55272/b/tok", row.Url);
        Assert.Equal("http://46.54.0.135:55272/b/tok", Assert.Single(row.AlternateUrls!));
        Assert.True(row.IsPlaylistResource);
        Assert.Equal("note two", row.ResourceAccessNote);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z", CultureInfo.InvariantCulture), row.ResourceExpiresAt);
    }

    private static BroadcastResource Resource(string host, string name, string note, bool playlist, string expiresAt)
    {
        var document = "{ \"schemaVersion\": 1, \"kind\": \"broadcast\", \"protocol\": \"http\", \"mediaKind\": \"video\", "
            + "\"playlist\": " + (playlist ? "true" : "false") + ", \"itemCount\": 1, "
            + "\"resourceName\": \"" + name + "\", \"accessNote\": \"" + note + "\", \"expiresAt\": \"" + expiresAt + "\", "
            + "\"accessPaths\": [ { \"kind\": \"lan\", \"host\": \"" + host + "\", \"port\": 55272 }, "
            + "{ \"kind\": \"portforward\", \"host\": \"46.54.0.135\", \"port\": 55272 } ], \"path\": \"/b/tok\" }";
        return BroadcastResourceReader.Read(document).Resource!;
    }
}
