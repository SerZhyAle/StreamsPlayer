using System.IO;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0222 requirement 1 and its acceptance criterion, read from the request log of a scripted server: the
/// session sends no <c>list</c> and no <c>subscribe</c> until a lease on the directory asks for it; the open
/// list sends one list and one subscribe, follows what is pushed, and when it closes nothing more is sent;
/// a second open lists again and never subscribes a second time; a one-shot ask lists and follows nothing.
/// </summary>
public sealed class ExchangeDirectoryLeaseTests
{
    [Fact]
    public async Task ASessionThatHoldsNoLeaseNeverListsOrSubscribes()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            // A server that pushes a directory nobody asked for changes nothing.
            await server.SendAsync(Directory(1, "phone", "b1"));
            await server.SendAsync(Changed(2, "b2"));
            await Task.Delay(800);

            Assert.Equal(["keepalive"], server.Seen());
            Assert.Null(service.Directory);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task OpeningTheListSendsOneListAndOneSubscribeAndClosingItSendsNothingMore()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            var lease = service.AcquireDirectory(follow: true);
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "no list after the open");
            Assert.DoesNotContain("subscribe", server.Seen());

            await server.SendAsync(Directory(1, "phone", "b1"));
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("subscribe"), "no subscribe after the answer");
            await DirectoryFixtureServer.UntilAsync(() => service.Directory is not null, "the list was not shown");
            Assert.Contains("b1", service.Directory!.LiveBroadcastIds);

            // A broadcast that starts while the list is open appears with no action from the person.
            await server.SendAsync(Changed(2, "b2"));
            await DirectoryFixtureServer.UntilAsync(
                () => service.Directory?.LiveBroadcastIds.Contains("b2") == true, "the new broadcast did not appear");
            Assert.Equal(["keepalive", "list", "subscribe"], server.Seen());

            lease.Dispose();
            Assert.Null(service.Directory);
            var sent = server.Seen().Length;

            // The wire has no unsubscribe: pushes still arrive, are read and are not followed - not even a gap
            // is answered with a list.
            await server.SendAsync(Changed(3, "b3"));
            await server.SendAsync(Changed(9, "b9"));
            await Task.Delay(800);

            Assert.Equal(sent, server.Seen().Length);
            Assert.Null(service.Directory);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task OpeningTheListAgainListsAgainAndNeverSubscribesTwice()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            var first = service.AcquireDirectory(follow: true);
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "no list after the first open");
            await server.SendAsync(Directory(1, "phone", "b1"));
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("subscribe"), "no subscribe");
            first.Dispose();

            // While closed the broadcast changed twice; the second open must show the truth, not the old list.
            await server.SendAsync(Changed(2, "b2"));
            var second = service.AcquireDirectory(follow: true);
            try
            {
                await DirectoryFixtureServer.UntilAsync(() => server.Seen().Count(frame => frame == "list") == 2,
                    "no second list after the second open");
                await server.SendAsync(Directory(5, "phone", "b5"));
                await DirectoryFixtureServer.UntilAsync(
                    () => service.Directory?.LiveBroadcastIds.Contains("b5") == true, "the second list was not shown");
                Assert.DoesNotContain("b1", service.Directory!.LiveBroadcastIds);

                Assert.Equal(["keepalive", "list", "subscribe", "list"], server.Seen());
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task AOneShotAskListsOnceAndFollowsNothing()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            using (service.AcquireDirectory(follow: false))
            {
                await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "no list after the ask");
                await server.SendAsync(Directory(1, "phone", "b1"));
                Assert.True(await service.WaitForDirectoryAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("b1", service.Directory!.LiveBroadcastIds);

                // Asked, not followed: a push changes nothing.
                await server.SendAsync(Changed(2, "b2"));
                await Task.Delay(500);
                Assert.DoesNotContain("b2", service.Directory!.LiveBroadcastIds);
            }

            Assert.Null(service.Directory);
            await Task.Delay(300);
            Assert.Equal(["keepalive", "list"], server.Seen());
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task AWaitForAServerThatDoesNotAnswerEndsAtTheBound()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            using var lease = service.AcquireDirectory(follow: false);
            Assert.False(await service.WaitForDirectoryAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task RefreshDirectoryExplicitlyReListsAndGuardsAgainstOverlaps()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new Scope();
        var service = scope.Service(server);
        try
        {
            service.Start();
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("keepalive"), "the session never came up");

            using var lease = service.AcquireDirectory(follow: true);
            await DirectoryFixtureServer.UntilAsync(() => server.Seen().Contains("list"), "no list after the open");
            await server.SendAsync(Directory(1, "phone", "b1"));
            await DirectoryFixtureServer.UntilAsync(() => service.Directory is not null && service.IsDirectoryFresh, "directory not fresh");

            // Explicit refresh asks for list again
            var refreshTask = service.RefreshDirectoryAsync(TimeSpan.FromSeconds(3));
            Assert.False(service.IsDirectoryFresh);

            // Concurrent call returns immediately without error
            var concurrentTask = service.RefreshDirectoryAsync(TimeSpan.FromSeconds(3));
            Assert.True(await concurrentTask);

            // Server responds with revision 2
            await server.SendAsync(Directory(2, "phone", "b2"));
            var refreshed = await refreshTask;
            Assert.True(refreshed);
            Assert.True(service.IsDirectoryFresh);
            Assert.Contains("b2", service.Directory!.LiveBroadcastIds);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    private static object Directory(long revision, string deviceId, params string[] broadcastIds) => new
    {
        schemaVersion = 2,
        type = "directory",
        revision,
        devices = new object[] { new { deviceId, deviceName = "Pixel 8", presence = "online" } },
        broadcasts = broadcastIds.Select(id => Record(id, deviceId)).ToArray()
    };

    private static object Changed(long revision, string upsertedBroadcastId) => new
    {
        schemaVersion = 2,
        type = "changed",
        revision,
        upserts = new { devices = Array.Empty<object>(), broadcasts = new[] { Record(upsertedBroadcastId, "phone") } },
        removals = new { deviceIds = Array.Empty<string>(), broadcastIds = Array.Empty<string>() }
    };

    private static object Record(string broadcastId, string deviceId) => new
    {
        broadcastId, deviceId, title = "Kitchen " + broadcastId, mode = "AUDIO_ONLY",
        descriptor = new
        {
            schemaVersion = 1,
            url = "http://192.168.1.97:8768/live-audio.aac",
            title = "Kitchen",
            mode = "AUDIO_ONLY",
            sourceId = "src-" + broadcastId,
            isLive = true,
            endpoints = new object[]
            {
                new { url = "http://192.168.1.97:8768/live-audio.aac", transport = "HTTP", mode = "AUDIO_ONLY", isLive = true }
            }
        }
    };

    private sealed class Scope : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));

        internal Scope() => System.IO.Directory.CreateDirectory(_path);

        internal ExchangeSourceService Service(DirectoryFixtureServer server)
        {
            new ExchangeAccountStore(_path).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", server.Port,
                "test-login", new string('t', 43), server.Pin, true));
            return new ExchangeSourceService(_path);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(_path, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder a test left behind is the operating system's to sweep.
            }
        }
    }
}
