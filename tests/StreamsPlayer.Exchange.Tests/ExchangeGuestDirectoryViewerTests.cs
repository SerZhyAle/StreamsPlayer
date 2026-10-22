using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0222 requirement 5 / reading 4: a guest entry is read by a short session of its own that says hello, lists,
/// subscribes, keeps alive and says bye - and nothing else - and the list it shows is only what the server
/// granted to the guest.
/// </summary>
public sealed class ExchangeGuestDirectoryViewerTests
{
    private const string Password = "grant-pair-secret";

    [Fact]
    public async Task AGuestViewerListsOnlyWhatWasGrantedAndAnnouncesNothing()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate, accountKind: "guest");
        var entry = Entry(server);
        var viewer = new ExchangeGuestDirectoryViewer(entry);
        try
        {
            viewer.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "the viewer never asked for the list");

            // The guest's own directory: its own device and the one record granted to it, with the grant's pair.
            await server.SendAsync(new
            {
                schemaVersion = 2,
                type = "directory",
                revision = 1,
                devices = new object[] { new { deviceId = "owner-phone", deviceName = "Owner phone", presence = "online" } },
                broadcasts = new object[]
                {
                    new
                    {
                        broadcastId = "granted", deviceId = "owner-phone", title = "Kitchen", mode = "AUDIO_ONLY", grantId = "g1",
                        credentials = new { user = "guest-pair", password = Password },
                        descriptor = new
                        {
                            schemaVersion = 1, url = "https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream",
                            title = "Kitchen", mode = "AUDIO_ONLY", sourceId = "src-granted", isLive = true,
                            endpoints = new object[]
                            {
                                new
                                {
                                    url = "https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream",
                                    transport = "RELAY", mode = "AUDIO_ONLY"
                                }
                            }
                        }
                    }
                }
            });
            await DirectoryFixtureServer.UntilAsync(() => viewer.Snapshot is not null, "the granted record was not shown");

            var view = Assert.Single(Assert.Single(viewer.Snapshot!.Groups).Broadcasts);
            Assert.Equal("granted", view.Record.BroadcastId);
            Assert.Equal(new ExchangeRelayCredentials("guest-pair", Password), view.Record.Credentials);
            Assert.Equal("ExchangeOnline", viewer.StatusKey);
            Assert.False(viewer.Ended);

            // The session said hello as the entry's own device, with its token, and nothing a guest may not say.
            Assert.Equal(entry.DeviceId, server.Hello?.DeviceId);
            Assert.True(server.Hello?.HadToken);
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("subscribe"), "no subscribe after the answer");
            Assert.Equal(["keepalive", "list", "subscribe"], server.Seen());

            viewer.Dispose();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("bye"), "the viewer did not say bye");
            Assert.All(server.Seen(), type => Assert.Contains(type, new[] { "keepalive", "list", "subscribe", "bye" }));
        }
        finally
        {
            viewer.Dispose();
        }
    }

    [Fact]
    public async Task ASignInTheAccountEndedIsShownAsItsLineAndEndsTheSession()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate, accountKind: "guest");
        var viewer = new ExchangeGuestDirectoryViewer(Entry(server));
        try
        {
            viewer.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "the viewer never asked for the list");

            await server.SendAsync(new { schemaVersion = 2, type = "refused", reason = "signed-out" });
            await DirectoryFixtureServer.UntilAsync(() => viewer.Ended, "the refusal did not end the session");

            Assert.Equal("ExchangeSignedOut", viewer.StatusKey);
            Assert.Null(viewer.Snapshot);
        }
        finally
        {
            viewer.Dispose();
        }
    }

    [Fact]
    public async Task AChangedCertificateIsSaidAndNotApprovedByTheViewer()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate, accountKind: "guest");
        var entry = Entry(server) with { Pin = "SHA256:" + new string('A', 43) };
        var viewer = new ExchangeGuestDirectoryViewer(entry);
        try
        {
            viewer.Start();
            await DirectoryFixtureServer.UntilAsync(() => viewer.Ended, "a wrong pin did not end the session");

            Assert.Equal("ExchangeCertificateChanged", viewer.StatusKey);
            Assert.Null(server.Hello);
            Assert.Empty(server.Seen());
        }
        finally
        {
            viewer.Dispose();
        }
    }

    private static ExchangeAccount Entry(DirectoryFixtureServer server) =>
        new(ExchangeProtocol.NewDeviceId(), "localhost", server.Port, "guest-login", new string('g', 43), server.Pin,
            Enabled: true, Guest: true);
}
