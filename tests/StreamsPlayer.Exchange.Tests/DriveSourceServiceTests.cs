using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed class DriveSourceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp-0202", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SourceMakesNoStartupOrSignInListingAndStoresOnlyProtectedGrant()
    {
        using var handler = new Handler();
        using var source = new DriveSourceService(_directory, handler, "client", "secret",
            (_, _) => Task.FromResult(new DriveTokens("private-access", "private-refresh", 3600)));
        Assert.Equal(0, handler.Requests);
        await source.SignInAsync(default);
        Assert.True(source.SignedIn);
        Assert.Equal(0, handler.Requests);
        var stored = File.ReadAllBytes(Path.Combine(_directory, "drive-account.bin"));
        Assert.DoesNotContain("private-refresh", Encoding.UTF8.GetString(stored));
        var clear = ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
        try { Assert.Contains("private-refresh", Encoding.UTF8.GetString(clear)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        await source.PollAsync(default);
        Assert.Equal(1, handler.Listings);
        Assert.NotNull(source.Directory);
        await source.SignOutAsync(default);
        Assert.Equal(1, handler.Revocations);
        Assert.False(source.SignedIn);
        Assert.False(File.Exists(Path.Combine(_directory, "drive-account.bin")));
        await source.StopAsync();
    }

    [Fact]
    public async Task RestartLoadsWithoutConnectingAndRefreshesOnlyOnDemand()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("client")));
        await new DriveAccountStore(_directory).SaveAsync(new DriveAccount(ExchangeProtocol.NewDeviceId(), hash, "refresh", []), default);
        using var handler = new Handler();
        using var source = new DriveSourceService(_directory, handler, "client", "secret");
        Assert.True(source.SignedIn);
        Assert.Equal(0, handler.Requests);
        await source.PollAsync(default);
        Assert.Equal(1, handler.Refreshes);
        Assert.Equal(1, handler.Listings);
        await source.StopAsync();
    }

    [Fact]
    public async Task RunningBroadcastPublishesAndStoppingRemovesItsOwnedAnnouncements()
    {
        using var handler = new Handler();
        using var source = new DriveSourceService(_directory, handler, "client", "secret",
            (_, _) => Task.FromResult(new DriveTokens("access", "refresh", 3600)));
        await source.SignInAsync(default);
        await source.UpdateBroadcastsAsync([Announcement()]);
        Assert.Equal(2, handler.Writes.Count);
        var record = DriveRecords.Parse(handler.Writes[0], "fmsx-broadcast-broadcast.json");
        Assert.Equal(source.DeviceId, record!.DeviceId);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("client")));
        Assert.Equal(2, new DriveAccountStore(_directory).Load(hash)!.OwnedFiles.Count);
        await source.UpdateBroadcastsAsync([]);
        Assert.Equal(2, handler.Deletes);
        Assert.Empty(new DriveAccountStore(_directory).Load(hash)!.OwnedFiles);
        Assert.Equal(0, handler.Listings);
        await source.SignOutAsync(default);
        await source.StopAsync();
    }

    [Fact]
    public async Task DirectorySuccessDoesNotHidePublicationFailureAndRecoveryClearsIt()
    {
        using var handler = new Handler { RefuseWrites = true };
        using var source = new DriveSourceService(_directory, handler, "client", "secret",
            (_, _) => Task.FromResult(new DriveTokens("access", "refresh", 3600)));
        await source.SignInAsync(default);
        await source.UpdateBroadcastsAsync([Announcement()]);
        Assert.Equal("DrivePublishFailed", source.StatusKey);
        Assert.True(await source.PollAsync(default));
        Assert.Equal("DrivePublishFailed", source.StatusKey);
        handler.RefuseWrites = false;
        await source.UpdateBroadcastsAsync([Announcement()]);
        Assert.Equal("DriveConnected", source.StatusKey);
        await source.SignOutAsync(default);
        await source.StopAsync();
    }

    [Fact]
    public async Task FailedRevocationStillForgetsTheLocalGrantAndExplainsTheRemainder()
    {
        using var handler = new Handler { RefuseRevocation = true };
        using var source = new DriveSourceService(_directory, handler, "client", "secret",
            (_, _) => Task.FromResult(new DriveTokens("access", "refresh", 3600)));
        await source.SignInAsync(default);
        await source.SignOutAsync(default);
        Assert.False(source.SignedIn);
        Assert.Equal("DriveRevocationFailed", source.StatusKey);
        Assert.False(File.Exists(Path.Combine(_directory, "drive-account.bin")));
        await source.StopAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static DriveAnnouncement Announcement() => new("broadcast", new
    {
        broadcastId = "broadcast", deviceId = "not-the-drive-device", mode = "AUDIO_ONLY", title = "Diagnostic",
        descriptor = new { schemaVersion = 1, url = "http://127.0.0.1:8768/live-audio.aac", mode = "AUDIO_ONLY" }
    });

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int Listings { get; private set; }
        public int Refreshes { get; private set; }
        public int Revocations { get; private set; }
        public int Deletes { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public bool RefuseRevocation { get; init; }
        public bool RefuseWrites { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                Refreshes++;
                Assert.Contains("refresh_token=refresh", await request.Content!.ReadAsStringAsync(cancellationToken));
                return Reply(new { access_token = "access", expires_in = 3600, scope = DriveOAuth.Scope });
            }
            if (request.RequestUri.AbsolutePath == "/revoke")
            {
                Revocations++;
                return new HttpResponseMessage(RefuseRevocation ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Post)
            {
                if (RefuseWrites) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var parts = Assert.IsAssignableFrom<MultipartContent>(request.Content);
                Writes.Add(await parts.Last().ReadAsByteArrayAsync(cancellationToken));
                return Reply(new { id = "own-" + Writes.Count });
            }
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                return Reply(new { });
            }
            Listings++;
            Assert.Equal(HttpMethod.Get, request.Method);
            var result = Reply(new { files = Array.Empty<object>() });
            result.Headers.Date = DateTimeOffset.UtcNow;
            return result;
        }

        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
