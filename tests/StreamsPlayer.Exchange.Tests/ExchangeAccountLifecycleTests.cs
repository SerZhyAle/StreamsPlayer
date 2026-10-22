using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed partial class ExchangeLoopRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Leave_RevokesOwnDeviceWithoutBye_ThenClearsProtectedAccount(bool enabled)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<string>();
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                frames.Enqueue(type);
                if (type == "revoke")
                {
                    revoked.SetResult(ExchangeProtocol.String(frame.RootElement, "deviceId")!);
                }
            }
        });
        var original = Account(server.Port, certificate) with { Enabled = enabled };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            if (enabled)
            {
                // SP-0222: the first thing a session says is its keepalive; the directory waits for a lease.
                await Until(() => frames.Contains("keepalive"));
            }

            await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(original.DeviceId, await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain("bye", frames);
            Assert.Equal("revoke", frames.Last());
            Assert.Equal(1, server.Connections);
            Assert.Equal("ExchangeForgotten", service.StatusKey);
            AssertForgotten(scope.Path, original.DeviceId);
            service.Start();
            await Task.Delay(1200);
            Assert.Equal(1, server.Connections);
            Assert.Null(service.Directory);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task Leave_UnreachableServer_ClearsLocallyAndNeverRetries()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var original = Account(FreePort(), certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ExchangeForgottenOffline", service.StatusKey);
        AssertForgotten(scope.Path, original.DeviceId);
        service.Start();
        await Task.Delay(1200);
        Assert.Equal("ExchangeForgottenOffline", service.StatusKey);
        Assert.Null(service.Account.Token);
    }

    [Fact]
    public async Task Leave_ChangedCertificate_ForgetsWithoutSendingCredentials()
    {
        using var oldCertificate = Certificate();
        using var newCertificate = Certificate();
        using var scope = new TestDirectory();
        var credentialsSent = false;
        await using var server = new ScriptedServer(newCertificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            credentialsSent = true;
        });
        var original = Account(server.Port, oldCertificate) with { Enabled = false };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Schannel may finish the server side of TLS even though the client refused the leaf.
        // The security boundary is the opening frame, not the server's TLS connection count.
        Assert.False(credentialsSent);
        Assert.Equal("ExchangeForgottenOffline", service.StatusKey);
        AssertForgotten(scope.Path, original.DeviceId);
        Assert.Null(service.PresentedFingerprint);
    }

    [Fact]
    public async Task Leave_WithoutAccount_DoesNotChangeIdentityOrStore()
    {
        using var scope = new TestDirectory();
        var service = new ExchangeSourceService(scope.Path);
        var original = service.Account;
        var bytes = File.ReadAllBytes(Path.Combine(scope.Path, "exchange-account.bin"));
        await service.ForgetAsync();
        Assert.Equal(original, service.Account);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(scope.Path, "exchange-account.bin")));
        Assert.Equal("ExchangeOff", service.StatusKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedReplacement_SignsTheNewAccountInFirst_AndOnlyThenLeavesTheOldOne(bool refuse)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var sequence = new ConcurrentQueue<string>();
        var oldRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldServer = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type");
                Assert.NotEqual("bye", type);
                if (type == "revoke")
                {
                    sequence.Enqueue("revoke");
                    oldRevoked.SetResult();
                }
            }
        });
        var original = Account(oldServer.Port, certificate);
        scope.Save(original);
        await using var nextServer = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.NotEqual(original.DeviceId, ExchangeProtocol.String(frame.RootElement, "deviceId"));
            Assert.Equal("new-login", ExchangeProtocol.String(frame.RootElement, "login"));
            sequence.Enqueue("enroll");
            if (refuse)
            {
                await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "bad-credentials" }, false, token);
                return;
            }

            await Enrolled(tls, token);
            await HoldOpen(tls, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeOnline");
            await service.EnrollAsync("localhost", nextServer.Port, "new-login", false, ['p', 'w'],
                ExchangeProtocol.Fingerprint(certificate.RawData), original).WaitAsync(TimeSpan.FromSeconds(15));
            if (refuse)
            {
                // SP-0221: the kind of the new account is known only once it answers, so the old entry is left
                // after the new sign-in succeeded as a member's - a refusal changes nothing and revokes nothing.
                Assert.Equal(new[] { "enroll" }, sequence);
                Assert.Equal("ExchangeBadCredentials", service.EnrollmentOutcomeKey);
                Assert.Equal(original.DeviceId, service.Account.DeviceId);
                Assert.Equal(original.Token, service.Account.Token);
                Assert.Equal("test-login", service.Account.Login);
                Assert.Equal("ExchangeOnline", service.StatusKey);
                Assert.Equal(1, oldServer.Connections);
                Assert.False(oldRevoked.Task.IsCompleted);
            }
            else
            {
                await Until(() => sequence.Count == 2);
                Assert.Equal(new[] { "enroll", "revoke" }, sequence);
                Assert.Equal(1, oldServer.Connections);
                Assert.Equal(nextServer.Port, service.Account.Port);
                Assert.Equal("new-login", service.Account.Login);
                Assert.NotEqual(original.DeviceId, service.Account.DeviceId);
            }
        }
        finally
        {
            await service.StopAsync();
        }

        oldServer.ThrowIfScriptFailed();
        nextServer.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task SameAccount_ReenrollsSameIdentity_RotatesTokenAndKeepsSwitch()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        ExchangeAccount? original = null;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.Equal(original!.DeviceId, ExchangeProtocol.String(frame.RootElement, "deviceId"));
            await Enrolled(tls, token);
            await HoldOpen(tls, token);
        });
        original = Account(server.Port, certificate) with { Enabled = false, AutoAcceptCasts = true };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        var secret = new[] { 'p', 'w' };
        try
        {
            Assert.False(service.RequiresLeave("LOCALHOST", server.Port, "TEST-LOGIN"));
            await service.EnrollAsync("LOCALHOST", server.Port, "TEST-LOGIN", false, secret, original.Pin);
            Assert.Equal(original.DeviceId, service.Account.DeviceId);
            Assert.Equal(new string('k', 43), service.Account.Token);
            Assert.True(service.Account.AutoAcceptCasts);
            Assert.All(secret, character => Assert.Equal('\0', character));
            var bytes = File.ReadAllBytes(Path.Combine(scope.Path, "exchange-account.bin"));
            Assert.DoesNotContain(new string('k', 43), System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task DifferentLogin_AndStaleConfirmation_DoNotReplaceAccount()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var original = Account(FreePort(), certificate) with { Enabled = false };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        Assert.True(service.RequiresLeave(original.Host, original.Port, "another-login"));
        await service.EnrollAsync(original.Host, original.Port, "another-login", false, ['p', 'w'], original.Pin);
        Assert.Equal(original, service.Account);
        await service.EnrollAsync(original.Host, original.Port, "another-login", false, ['p', 'w'], original.Pin,
            original with { DeviceId = ExchangeProtocol.NewDeviceId() });
        Assert.Equal(original, service.Account);
        Assert.Equal("ExchangeReplacementRequired", service.EnrollmentOutcomeKey);
    }

    [Fact]
    public async Task ConfirmedDifferentLogin_OnSameServer_UsesFreshDeviceId()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        ExchangeAccount? original = null;
        var order = new ConcurrentQueue<string>();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            if (index == 0)
            {
                // SP-0221: the new login signs in first (its kind decides whether the old entry is left at all).
                using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
                Assert.Equal("enroll", ExchangeProtocol.String(enrollment.RootElement, "type"));
                Assert.Equal("another-login", ExchangeProtocol.String(enrollment.RootElement, "login"));
                Assert.NotEqual(original!.DeviceId, ExchangeProtocol.String(enrollment.RootElement, "deviceId"));
                order.Enqueue("enroll");
                await Enrolled(tls, token);
                await HoldOpen(tls, token);
                return;
            }

            await Handshake(tls, token);
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("revoke", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.Equal(original!.DeviceId, ExchangeProtocol.String(frame.RootElement, "deviceId"));
            order.Enqueue("revoke");
            revoked.SetResult();
        });
        original = Account(server.Port, certificate) with { Enabled = false };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, "another-login", false, ['p', 'w'], original.Pin, original);
            await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "enroll", "revoke" }, order);
            Assert.Equal("another-login", service.Account.Login);
            Assert.NotEqual(original.DeviceId, service.Account.DeviceId);
            Assert.Equal("ExchangeForgotten", service.LeaveOutcomeKey);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Theory]
    [InlineData("[2001:0db8:0:0::1]", "2001:db8::1")]
    [InlineData("SERVER.EXAMPLE", "server.example")]
    public void AccountIdentity_NormalizesHostAndLogin(string first, string second) =>
        Assert.True(ExchangeAccountIdentity.SameAccount(first, 443, "User", second, 443, "user"));

    private static Task Enrolled(SslStream tls, CancellationToken token) => ExchangeProtocol.WriteAsync(tls, new
    {
        schemaVersion = 2, type = "enrolled", deviceToken = new string('k', 43), account = "test-login",
        publicEndpoint = "localhost:44022", serverVersion = "test", features = new[] { "directory" }, keepaliveSeconds = 30
    }, false, token);

    private static void AssertForgotten(string directory, string previousDeviceId)
    {
        var stored = new ExchangeAccountStore(directory).Load();
        Assert.Null(stored.Token);
        Assert.Null(stored.Pin);
        Assert.Empty(stored.Host);
        Assert.Empty(stored.Login);
        Assert.False(stored.Enabled);
        Assert.NotEqual(previousDeviceId, stored.DeviceId);
    }
}
