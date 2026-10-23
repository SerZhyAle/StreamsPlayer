using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0221 against a local TLS fixture: a password changed here (item AT), a sign-in ended by the account
/// (`signed-out`), a renewal with the same key (item AY), and the account kinds (item AO) - guest entries
/// held beside the own one, never connected, never announcing.
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private static readonly string NewToken = new('n', 43);

    private static Task WelcomeAs(SslStream tls, string? kind, CancellationToken token) => ExchangeProtocol.WriteAsync(tls, new
    {
        schemaVersion = 2, type = "welcome", account = "test-login", accountKind = kind, publicEndpoint = "localhost:44022",
        serverVersion = "test", features = new[] { "directory", "cast" }, keepaliveSeconds = 30
    }, false, token);

    private static async Task ServeSession(SslStream tls, CancellationToken token, Func<string, JsonElement, Task> onFrame)
    {
        await Handshake(tls, token);
        while (true)
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
            await onFrame(ExchangeProtocol.String(frame.RootElement, "type") ?? "", frame.RootElement);
        }
    }

    // ------------------------------------------------------------------ signed out by the account (item AT)

    [Fact]
    public async Task SignedOut_KeepsTheEntry_SaysSo_AndNeverRetries()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("hello", ExchangeProtocol.String(hello.RootElement, "type"));
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "signed-out" }, false, token);
        });
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeSignedOut");
            Assert.True(service.SignedOutNotice);
            var stored = new ExchangeAccountStore(scope.Path).Load();
            Assert.Null(stored.Token);
            Assert.True(stored.SignedOut);
            Assert.Equal(original.DeviceId, stored.DeviceId);
            Assert.Equal(original.Host, stored.Host);
            Assert.Equal(original.Port, stored.Port);
            Assert.Equal(original.Login, stored.Login);
            Assert.Equal(original.Pin, stored.Pin);
            // The first retry of a backoff would be a second away: nothing is scheduled at all.
            await Task.Delay(2500);
            Assert.Equal(1, server.Connections);
            service.Start();
            await Task.Delay(300);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        // A restart reads the same state: the message again, still no connection.
        var restarted = new ExchangeSourceService(scope.Path);
        restarted.Start();
        await Task.Delay(500);
        Assert.Equal("ExchangeSignedOut", restarted.StatusKey);
        Assert.Equal(1, server.Connections);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task SignedOutDevice_EnrollsAgainWithTheSameDeviceId_AndTheMessageGoes()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        ExchangeAccount? original = null;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.Equal(original!.DeviceId, ExchangeProtocol.String(frame.RootElement, "deviceId"));
            Assert.Equal("the-new-password", ExchangeProtocol.String(frame.RootElement, "password"));
            await Enrolled(tls, token);
            await HoldOpen(tls, token);
        });
        original = Account(server.Port, certificate) with { Token = null, SignedOut = true };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            Assert.Equal("ExchangeSignedOut", service.StatusKey);
            await service.EnrollAsync("localhost", server.Port, "test-login", false, "the-new-password".ToCharArray(),
                original.Pin);
            Assert.Equal(new string('k', 43), service.Account.Token);
            Assert.False(service.Account.SignedOut);
            Assert.Equal(original.DeviceId, service.Account.DeviceId);
            Assert.False(new ExchangeAccountStore(scope.Path).Load().SignedOut);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    // ------------------------------------------------------------------ change password (item AT, AW)

    [Theory]
    [InlineData("bad-credentials", "ExchangePasswordOldWrong")]
    [InlineData("password-policy", "ExchangePasswordPolicy")]
    [InlineData("rate-limited", "ExchangeRateLimited")]
    public async Task PasswordChange_Refusal_IsOneAnswer_NothingChanges_AndTheSessionStaysOpen(string reason, string key)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var changes = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, (_, tls, token) => ServeSession(tls, token, async (type, root) =>
        {
            if (type != "password-change")
            {
                return;
            }

            changes.Enqueue(ExchangeProtocol.String(root, "oldPassword") + "|" + ExchangeProtocol.String(root, "newPassword"));
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason }, true, token);
        }));
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            var oldPassword = "Passw0rdTest".ToCharArray();
            var newPassword = "Vector-new-pw-2".ToCharArray();
            var result = await service.ChangePasswordAsync(oldPassword, newPassword).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Changed);
            Assert.Equal(key, result.Key);
            Assert.Equal(new[] { "Passw0rdTest|Vector-new-pw-2" }, changes);
            Assert.All(oldPassword, character => Assert.Equal('\0', character));
            Assert.All(newPassword, character => Assert.Equal('\0', character));
            // A refusal is not a revocation: the token, the entry and the session are as they were.
            await Task.Delay(300);
            Assert.Equal(original.Token, service.Account.Token);
            Assert.Equal("ExchangeOnline", service.StatusKey);
            Assert.Equal(1, server.Connections);
            Assert.Equal(original.Token, new ExchangeAccountStore(scope.Path).Load().Token);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task PasswordChange_Success_StoresTheFreshToken_AndTheNextHelloUsesIt()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var tokens = new ConcurrentQueue<string?>();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
            tokens.Enqueue(ExchangeProtocol.String(opening.RootElement, "deviceToken"));
            await WelcomeAs(tls, "member", token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                if (ExchangeProtocol.String(frame.RootElement, "type") == "password-change")
                {
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "password-changed", deviceToken = NewToken }, true, token);
                }
            }
        });
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            var result = await service.ChangePasswordAsync("Passw0rdTest".ToCharArray(), "Vector-new-pw-2".ToCharArray())
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Changed);
            Assert.Equal("ExchangePasswordChanged", result.Key);
            Assert.Equal(NewToken, service.Account.Token);
            Assert.Equal(NewToken, new ExchangeAccountStore(scope.Path).Load().Token);
            // The device stays signed in on the same stream; a later session opens with the new token.
            Assert.Equal("ExchangeOnline", service.StatusKey);
            await service.StopAsync();
            service.Start();
            await Until(() => tokens.Count == 2);
            Assert.Equal(new[] { original.Token, NewToken }, tokens);
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(scope.Path, "exchange-account.bin")), null,
                DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(clear);
            Assert.DoesNotContain("Passw0rdTest", text);
            Assert.DoesNotContain("Vector-new-pw-2", text);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task PasswordChange_IsOneFlight_AndALateAnswerIsStillTheAnswer()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var requests = 0;
        await using var server = new ScriptedServer(certificate, (_, tls, token) => ServeSession(tls, token, async (type, _) =>
        {
            if (type != "password-change")
            {
                return;
            }

            Interlocked.Increment(ref requests);
            await Task.Delay(700, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "password-changed", deviceToken = NewToken }, true, token);
        }));
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            using var giveUp = new CancellationTokenSource();
            var first = service.ChangePasswordAsync("Passw0rdTest".ToCharArray(), "Vector-new-pw-2".ToCharArray(), giveUp.Token);
            await Until(() => Volatile.Read(ref requests) == 1);
            // A second request while the first is out is refused locally and never reaches the server.
            var second = await service.ChangePasswordAsync("Passw0rdTest".ToCharArray(), "Another-pw-3".ToCharArray());
            Assert.Equal("ExchangePasswordBusy", second.Key);
            // The person stops waiting; the answer arrives later and the new token must still be kept.
            giveUp.Cancel();
            Assert.Equal("ExchangeUnanswered", (await first).Key);
            await Until(() => service.Account.Token == NewToken);
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task LateRefusalOfAChange_NeverRevokesTheDevice()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var asked = false;
        await using var server = new ScriptedServer(certificate, (_, tls, token) => ServeSession(tls, token, async (type, _) =>
        {
            if (type != "password-change")
            {
                return;
            }

            asked = true;
            await Task.Delay(500, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "bad-credentials" }, true, token);
        }));
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var result = await service.ChangePasswordAsync("wrong-old-pw".ToCharArray(), "Vector-new-pw-2".ToCharArray(), giveUp.Token);
            Assert.Equal("ExchangeUnanswered", result.Key);
            Assert.True(asked);
            await Task.Delay(1200);
            // Read as this device's revocation, that refusal would have wiped the token and the entry.
            Assert.Equal(original.Token, service.Account.Token);
            Assert.Equal("ExchangeOnline", service.StatusKey);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task PasswordChange_WithoutASession_ReachesNothing_AndEmptyFieldsNeverLeave()
    {
        using var scope = new TestDirectory();
        var service = new ExchangeSourceService(scope.Path);
        Assert.Equal("ExchangePasswordNotSignedIn",
            (await service.ChangePasswordAsync("a".ToCharArray(), "b".ToCharArray())).Key);
        Assert.Equal("ExchangePasswordEmpty",
            (await service.ChangePasswordAsync([], "b".ToCharArray())).Key);
        using var certificate = Certificate();
        scope.Save(Account(FreePort(), certificate) with { Enabled = false });
        var offline = new ExchangeSourceService(scope.Path);
        Assert.Equal("ExchangeUnreachable",
            (await offline.ChangePasswordAsync("a".ToCharArray(), "b".ToCharArray())).Key);
    }

    // ------------------------------------------------------------------ a renewal with the same key (item AY)

    private static X509Certificate2 CertificateFor(RSA key, string subject)
    {
        var request = new CertificateRequest("CN=" + subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    [Fact]
    public async Task RenewedLeaf_WithTheSameKey_NeedsNoPrompt_AndTheNewLeafIsRecorded()
    {
        using var key = RSA.Create(2048);
        using var first = CertificateFor(key, "localhost");
        using var renewed = CertificateFor(key, "localhost");
        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(renewed, (_, tls, token) => ServeSession(tls, token, (_, _) => Task.CompletedTask));
        var pinned = ExchangeProtocol.Fingerprint(first.RawData);
        scope.Save(Account(server.Port, first) with
        {
            PinKey = ExchangeProtocol.PublicKeyFingerprint(first.RawData)
        });
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            Assert.Null(service.PresentedFingerprint);
            var expected = ExchangeProtocol.Fingerprint(renewed.RawData);
            await Until(() => service.Account.Pin == expected);
            Assert.NotEqual(pinned, service.Account.Pin);
            Assert.Equal(expected, new ExchangeAccountStore(scope.Path).Load().Pin);
            Assert.Equal(ExchangeProtocol.PublicKeyFingerprint(first.RawData), service.Account.PinKey);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task LeafWithAnotherKey_IsAChangedCertificate_AndTheEntryIsNotTouched()
    {
        using var firstKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var first = CertificateFor(firstKey, "localhost");
        using var foreign = CertificateFor(otherKey, "localhost");
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(foreign, (_, tls, token) => ServeSession(tls, token, (_, _) => Task.CompletedTask));
        var original = Account(server.Port, first) with { PinKey = ExchangeProtocol.PublicKeyFingerprint(first.RawData) };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeCertificateChanged");
            Assert.Equal(ExchangeProtocol.Fingerprint(foreign.RawData), service.PresentedFingerprint);
            Assert.Equal(original.Pin, service.Account.Pin);
            Assert.Equal(original.PinKey, service.Account.PinKey);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task EntryWithOnlyALeaf_GainsItsKeyOnTheFirstAcceptedConnection()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, (_, tls, token) => ServeSession(tls, token, (_, _) => Task.CompletedTask));
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            await Until(() => service.Account.PinKey is not null);
            Assert.Equal(ExchangeProtocol.PublicKeyFingerprint(certificate.RawData), service.Account.PinKey);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Connection_NegotiatesTls12OrLater()
    {
        using var certificate = Certificate();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Task.Delay(200, token);
        });
        using var connection = await ExchangeTlsConnection.OpenAsync("localhost", server.Port,
            ExchangeProtocol.Fingerprint(certificate.RawData), CancellationToken.None);
        Assert.True(connection.Stream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, connection.Stream.SslProtocol.ToString());
    }

    // ------------------------------------------------------------------ account kinds (item AO), guest entries

    private static Task EnrolledAs(SslStream tls, string kind, CancellationToken token) => ExchangeProtocol.WriteAsync(tls, new
    {
        schemaVersion = 2, type = "enrolled", deviceToken = new string('g', 43), account = "guest-login", accountKind = kind,
        publicEndpoint = "localhost:44022", serverVersion = "test", features = new[] { "directory" }, keepaliveSeconds = 30
    }, false, token);

    [Fact]
    public async Task GuestAccount_FromAnEmptySlot_IsKeptBesideAndAnnouncesNothing()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<string>();
        var roles = "";
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            roles = enrollment.RootElement.GetProperty("roles").GetRawText();
            await EnrolledAs(tls, "guest", token);
            try
            {
                while (true)
                {
                    using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                    frames.Enqueue(ExchangeProtocol.String(frame.RootElement, "type")!);
                }
            }
            catch (EndOfStreamException)
            {
                // The client closed the stream.
            }
        });
        var service = new ExchangeSourceService(scope.Path);
        var emptySlotId = service.Account.DeviceId;
        try
        {
            await service.EnrollAsync("localhost", server.Port, "guest-login", false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData));
            Assert.Null(service.EnrollmentOutcomeKey);
            Assert.Equal("ExchangeGuestSignedIn", service.GuestOutcomeKey);
            // The own slot is still empty and gave its device id to the guest entry (item AA: one id per entry).
            Assert.Null(service.Account.Token);
            Assert.NotEqual(emptySlotId, service.Account.DeviceId);
            var entry = Assert.Single(service.Guests.Entries);
            Assert.True(entry.Guest);
            Assert.Equal(emptySlotId, entry.DeviceId);
            Assert.Equal(new string('g', 43), entry.Token);
            Assert.NotNull(entry.PinKey);
            Assert.Equal("[\"receiver\",\"broadcaster\"]", roles);
            // Reloaded from its DPAPI file: still one guest, with its token.
            Assert.Equal(entry.Token, Assert.Single(new ExchangeGuestEntries(scope.Path).Entries).Token);
            await Until(() => frames.Contains("bye"));
            Assert.DoesNotContain("keepalive", frames);
            Assert.DoesNotContain("list", frames);
            Assert.DoesNotContain("broadcast-start", frames);
            Assert.DoesNotContain("publish", frames);
            service.Start();
            await Task.Delay(500);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task GuestAccount_BesideAMemberEntry_DoesNotLeaveIt()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var ownFrames = new ConcurrentQueue<string>();
        await using var ownServer = new ScriptedServer(certificate, (_, tls, token) => ServeSession(tls, token,
            (type, _) =>
            {
                ownFrames.Enqueue(type);
                return Task.CompletedTask;
            }));
        await using var guestServer = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("guest-login", ExchangeProtocol.String(enrollment.RootElement, "login"));
            await EnrolledAs(tls, "guest", token);
            await HoldOpen(tls, token);
        });
        var original = Account(ownServer.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");
            await service.EnrollAsync("localhost", guestServer.Port, "guest-login", false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData), original);
            Assert.Equal("ExchangeGuestSignedIn", service.GuestOutcomeKey);
            Assert.Single(service.Guests.Entries);
            // The own entry was never stopped, left or revoked.
            Assert.Equal(original.DeviceId, service.Account.DeviceId);
            Assert.Equal(original.Token, service.Account.Token);
            Assert.Equal("ExchangeOnline", service.StatusKey);
            Assert.Equal(1, ownServer.Connections);
            Assert.DoesNotContain("revoke", ownFrames);
            Assert.DoesNotContain("bye", ownFrames);
        }
        finally
        {
            await service.StopAsync();
        }

        ownServer.ThrowIfScriptFailed();
        guestServer.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task GuestEntries_AreBounded_AndADeviceThatCannotBeKeptIsRevoked()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            await EnrolledAs(tls, "guest", token);
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("revoke", ExchangeProtocol.String(frame.RootElement, "type"));
            revoked.SetResult(ExchangeProtocol.String(frame.RootElement, "deviceId")!);
        });
        var guests = new ExchangeGuestEntries(scope.Path);
        for (var index = 0; index < ExchangeEntryLimits.MaximumGuestEntries; index++)
        {
            guests.Upsert(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "host" + index + ".test", 22022, "guest-" + index,
                new string('g', 43), ExchangeProtocol.Fingerprint([1]), true, false, null, true));
        }

        var service = new ExchangeSourceService(scope.Path);
        var freshId = service.Account.DeviceId;
        try
        {
            await service.EnrollAsync("localhost", server.Port, "guest-login", false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData));
            Assert.Equal("ExchangeGuestLimit", service.EnrollmentOutcomeKey);
            Assert.Equal(freshId, await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ExchangeEntryLimits.MaximumGuestEntries, service.Guests.Entries.Count);
            Assert.Null(service.Guests.Find("localhost", server.Port, "guest-login"));
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task SignOutOfAGuestEntry_SaysHelloThenRevoke_AndForgetsIt()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var order = new ConcurrentQueue<string>();
        var revokedId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            order.Enqueue("hello");
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
            order.Enqueue(ExchangeProtocol.String(frame.RootElement, "type")!);
            revokedId.SetResult(ExchangeProtocol.String(frame.RootElement, "deviceId")!);
        });
        var entry = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", server.Port, "guest-login",
            new string('g', 43), ExchangeProtocol.Fingerprint(certificate.RawData), true, false, null, true);
        new ExchangeGuestEntries(scope.Path).Upsert(entry);
        var service = new ExchangeSourceService(scope.Path);
        await service.SignOutGuestAsync(entry);
        Assert.Equal(entry.DeviceId, await revokedId.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "hello", "revoke" }, order);
        Assert.Empty(service.Guests.Entries);
        Assert.Equal("ExchangeGuestSignedOut", service.GuestOutcomeKey);
        Assert.Empty(new ExchangeGuestEntries(scope.Path).Entries);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task SignOutOfAGuestEntry_WithTheServerDown_ForgetsLocallyAndNeverRetries()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var entry = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", FreePort(), "guest-login",
            new string('g', 43), ExchangeProtocol.Fingerprint(certificate.RawData), true, false, null, true);
        new ExchangeGuestEntries(scope.Path).Upsert(entry);
        var service = new ExchangeSourceService(scope.Path);
        await service.SignOutGuestAsync(entry);
        Assert.Empty(service.Guests.Entries);
        Assert.Equal("ExchangeGuestSignedOutOffline", service.GuestOutcomeKey);
    }

    [Fact]
    public async Task OwnEntry_WhoseAccountBecameAGuest_MovesBesideWithoutARevoke()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
            await WelcomeAs(tls, "guest", token);
            try
            {
                while (true)
                {
                    using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                    frames.Enqueue(ExchangeProtocol.String(frame.RootElement, "type")!);
                }
            }
            catch (EndOfStreamException)
            {
                // The receiver ended with the move.
            }
        });
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.Guests.Entries.Count == 1);
            Assert.Null(service.Account.Token);
            var moved = service.Guests.Entries[0];
            Assert.Equal(original.DeviceId, moved.DeviceId);
            Assert.Equal(original.Token, moved.Token);
            Assert.True(moved.Guest);
            Assert.NotEqual(original.DeviceId, service.Account.DeviceId);
            await Task.Delay(300);
            Assert.DoesNotContain("revoke", frames);
            Assert.DoesNotContain("list", frames);
            Assert.Equal("ExchangeGuestMoved", service.GuestOutcomeKey);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    private static Task WelcomeWith(SslStream tls, string? kind, string[]? features, CancellationToken token)
    {
        var welcome = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2, ["type"] = "welcome", ["account"] = "test-login", ["accountKind"] = kind,
            ["publicEndpoint"] = "localhost:44022", ["serverVersion"] = "test", ["keepaliveSeconds"] = 30
        };
        if (features is not null)
        {
            welcome["features"] = features;
        }

        return ExchangeProtocol.WriteAsync(tls, welcome, false, token);
    }

    [Theory]
    [InlineData("member")]
    [InlineData(null)]
    public async Task OwnEntryFlaggedAsAGuest_IsAMemberAgain_WhenTheNextWelcomeSaysMember(string? kind)
    {
        // Item AO: an operator may change the kind either way. The flag set by an earlier welcome (no room beside
        // the own slot) must not outlive the server's own word, or the producer stays off until the next sign-in.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
            await WelcomeWith(tls, kind, ["directory", "cast"], token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate) with { Guest = true });
        var service = new ExchangeSourceService(scope.Path);
        Assert.False(service.MayBroadcast);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");

            Assert.True(service.MayBroadcast);
            Assert.False(service.Account.Guest);
            Assert.False(new ExchangeAccountStore(scope.Path).Load().Guest);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Theory]
    [InlineData(new[] { "directory", "cast" }, "member", true)]
    [InlineData(new[] { "directory", "tunnel", "pairing", "clipboard" }, "member", false)]
    [InlineData(new string[0], "member", false)]
    [InlineData(new[] { "directory", "cast" }, "guest", false)]
    public async Task TheCastEntryIsOfferedOnlyToAMemberOnAServerThatDidNotLeaveCastOut(string[] features, string kind, bool offered)
    {
        // 7.2: a client hides what the server's feature list leaves out.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
            await WelcomeWith(tls, kind, features, token);
            await HoldOpen(tls, token);
        });
        // A guest welcome with no room beside the own slot keeps the entry, flagged: the session goes on without a producer.
        var guests = new ExchangeGuestEntries(scope.Path);
        if (kind == "guest")
        {
            for (var index = 0; index < ExchangeEntryLimits.MaximumGuestEntries; index++)
            {
                guests.Upsert(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "other.example", 443, "guest" + index,
                    new string('g', 43), ExchangeProtocol.Fingerprint(certificate.RawData), true, false, null, true));
            }
        }

        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");

            Assert.Equal(offered, service.CanCast);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public void BeforeAnyWelcomeNothingIsHiddenForWantOfAFeatureList()
    {
        using var scope = new TestDirectory();

        var service = new ExchangeSourceService(scope.Path);

        Assert.True(service.CanCast);
    }

    [Fact]
    public async Task InvitationString_NeverReachesTheWire()
    {
        // The reader keeps the fact of an invitation and drops the code, so a sign-in built from the string
        // cannot send it: the enroll frame has no member for it, whatever the string carried.
        const string text = "FMSEXCH1:x=localhost%3A22022&u=test-login&i=a3gPp-pvQPdQN7qwDDVbyQ";
        var data = ExchangeConnectionImport.ReadText(text).Data!;
        Assert.True(data.HasInvitation);
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        string? frameText = null;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            frameText = frame.RootElement.GetRawText();
            await Enrolled(tls, token);
            await HoldOpen(tls, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, data.Login!, false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData));
            Assert.NotNull(frameText);
            Assert.Equal("enroll", JsonDocument.Parse(frameText!).RootElement.GetProperty("type").GetString());
            Assert.DoesNotContain("a3gPp", frameText);
            Assert.DoesNotContain("invitation", frameText!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.StopAsync();
        }
    }
}
