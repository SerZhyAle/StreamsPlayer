using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0221, the redaction acceptance walked flow by flow: after every way a sign-in begins and ends -
/// a sign-in, a password change, sign out on this device, signed out by the account, a guest entry and
/// an invitation string - the session log and a produced diagnostic archive grep clean of every secret.
/// The lines are the shapes the surfaces actually write in these paths (the failure window's code line,
/// the action fault boundary, the log report's exception field), and each flow adds the exception-message
/// shape it could leak - its own envelope inside a <c>msg=</c> field - because that accidental dump is
/// what the sink's markers exist for (<see cref="ExchangeDiagnosticRedactor"/>).
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private const string SignInSecret = "Hunter2-sign-in";
    private const string OldSecret = "Hunter2-old";
    private const string NewSecret = "Hunter2-new";
    private const string SharedInvitationSecret = "shared-invitation-password";
    private const string InvitationValue = "a3gPp-pvQPdQN7qwDDVbyQ";

    private static readonly string[] EverySecret =
    [
        SignInSecret, OldSecret, NewSecret, SharedInvitationSecret, InvitationValue,
        new string('t', 43), // the token the fixture account carries
        new string('k', 43), // the token an enrolled answer issues
    ];

    [Fact]
    public async Task AfterEverySignInFlow_TheLogAndTheArchive_GrepCleanOfEverySecret()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests",
            Guid.NewGuid().ToString("N"));
        // Every revoke's device id lands here; the walk waits for its own by value, because a revoke may
        // ride the live control stream or open the offline one.
        var revokedIds = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
            switch (ExchangeProtocol.String(opening.RootElement, "type"))
            {
                case "enroll":
                    Assert.Equal(SignInSecret, ExchangeProtocol.String(opening.RootElement, "password"));
                    if (ExchangeProtocol.String(opening.RootElement, "login") == "guest-login")
                    {
                        // The guest entry signs in beside the empty own slot, and holds nothing after it.
                        await ExchangeProtocol.WriteAsync(tls, new
                        {
                            schemaVersion = 2, type = "enrolled", deviceToken = new string('k', 43),
                            account = "guest-login", accountKind = "guest", publicEndpoint = "localhost:44022",
                            serverVersion = "test", features = new[] { "directory" }, keepaliveSeconds = 30
                        }, false, token);
                        await HoldOpen(tls, token);
                        break;
                    }

                    // The sign-in, the password change on the same live control stream, and the sign-out's
                    // revoke, which rides that same stream while the session is still the live one.
                    await Enrolled(tls, token);
                    while (true)
                    {
                        using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                        var type = ExchangeProtocol.String(frame.RootElement, "type");
                        if (type == "revoke")
                        {
                            revokedIds.Enqueue(ExchangeProtocol.String(frame.RootElement, "deviceId")!);
                            return;
                        }

                        if (type != "password-change")
                        {
                            continue;
                        }

                        Assert.Equal(OldSecret, ExchangeProtocol.String(frame.RootElement, "oldPassword"));
                        Assert.Equal(NewSecret, ExchangeProtocol.String(frame.RootElement, "newPassword"));
                        await ExchangeProtocol.WriteAsync(tls, new
                        {
                            schemaVersion = 2, type = "password-changed", deviceToken = new string('k', 43)
                        }, true, token);
                    }

                case "hello":
                    if (ExchangeProtocol.String(opening.RootElement, "deviceToken") == new string('t', 43))
                    {
                        // Signed out by the account: the stored hello is refused.
                        await ExchangeProtocol.WriteAsync(tls, new
                        {
                            schemaVersion = 2, type = "refused", reason = "bad-credentials"
                        }, false, token);
                        break;
                    }

                    // The offline revoke of a sign-out (the own one when the live stream could not carry it,
                    // the guest entry's always): one bounded hello and the device's own revoke, no bye.
                    await ExchangeProtocol.WriteAsync(tls, new
                    {
                        schemaVersion = 2, type = "welcome", account = "test-login",
                        publicEndpoint = "localhost:44022", serverVersion = "test",
                        features = new[] { "directory", "cast" }, keepaliveSeconds = 30
                    }, false, token);
                    while (true)
                    {
                        using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                        if (ExchangeProtocol.String(frame.RootElement, "type") == "revoke")
                        {
                            revokedIds.Enqueue(ExchangeProtocol.String(frame.RootElement, "deviceId")!);
                            return;
                        }
                    }

                    break;
            }
        });

        using (var log = new CurrentLog(scope.Path))
        {
            var pin = ExchangeProtocol.Fingerprint(certificate.RawData);
            var service = new ExchangeSourceService(scope.Path);
            try
            {
                // 1. Sign in from a pasted string, then change the password with the old and the new one.
                await service.EnrollAsync("localhost", server.Port, "test-login", false,
                    SignInSecret.ToCharArray(), pin);
                Assert.Equal(new string('k', 43), service.Account.Token);
                await Until(() => service.StatusKey == "ExchangeOnline");
                var change = await service.ChangePasswordAsync(OldSecret.ToCharArray(), NewSecret.ToCharArray())
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(change.Changed, change.Key);
                log.Information($"LOG REPORT | ok=false | err=IOException | " +
                    $"msg=enroll refused: {{\"type\":\"enroll\",\"login\":\"test-login\",\"password\":\"{SignInSecret}\",\"invitationPassword\":\"{SharedInvitationSecret}\"}}");

                // 2. Sign out on this device.
                var ownDeviceId = service.Account.DeviceId;
                await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await Until(() => revokedIds.Contains(ownDeviceId));
                log.Information($"LOG REPORT | ok=false | err=IOException | " +
                    $"msg=revoke sent under deviceToken={new string('k', 43)}");

                // 3. A guest invitation signs this PC in as a guest of that server.
                await service.EnrollAsync("localhost", server.Port, "guest-login", false,
                    SignInSecret.ToCharArray(), pin);
                await Until(() => service.Guests.Entries.Count == 1);
                log.Information($"EXCHANGE QR FAULT | type=IOException | " +
                    $"msg=FMSEXCH1:x=localhost%3A{server.Port}&u=guest-login&i={InvitationValue}");

                // 4. The guest entry signs out on this device.
                var guestDeviceId = service.Guests.Entries.Single().DeviceId;
                await service.SignOutGuestAsync(service.Guests.Entries.Single()).WaitAsync(TimeSpan.FromSeconds(30));
                await Until(() => revokedIds.Contains(guestDeviceId));
                Assert.Empty(service.Guests.Entries);
            }
            finally
            {
                await service.StopAsync();
            }

            // 4. Signed out by the account: the stored sign-in is refused and says which.
            scope.Save(Account(server.Port, certificate));
            var ended = new ExchangeSourceService(scope.Path);
            try
            {
                ended.Start();
                await Until(() => ended.StatusKey == "ExchangeBadCredentials");
                Assert.Equal(ExchangeFailureRows.BadCredentials, ended.SignInEndedNotice);
                log.Information("EXCHANGE FAILURE code=bad-credentials");
                log.Information($"LOG REPORT | ok=false | err=IOException | " +
                    $"msg=hello refused, deviceToken={new string('t', 43)}");
            }
            finally
            {
                await ended.StopAsync();
            }
        }

        // The invitation string itself is read and its code is dropped (never sent, kept or logged).
        var invitation = ExchangeConnectionStringCodec.Parse(
            $"FMSEXCH1:x=localhost%3A{server.Port}&u=carol&i={InvitationValue}");
        Assert.True(invitation.Success, invitation.Reason);
        Assert.True(invitation.Data!.HasInvitation);
        Assert.DoesNotContain(InvitationValue, ExchangeConnectionStringCodec.Encode(invitation.Data));

        // 5. The diagnostic archive: the same bundle the About page packs, grepped entry by entry.
        var summary = DiagnosticEnvironmentSummary.Render(DiagnosticEnvironmentSummary.From(new CatalogState(),
            ProductInfo.Version, "test-os", "X64", DateTimeOffset.UtcNow,
            [new KeyValuePair<string, string>("flows", "sign-in;password-change;sign-out;signed-out;guest;invitation")]));
        var archivePath = DiagnosticArchiveBuilder.Build(scope.Path, outputDirectory, summary, DateTimeOffset.UtcNow);
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == DiagnosticArchiveBuilder.SummaryEntryName);
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                foreach (var secret in EverySecret)
                {
                    Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                }
            }

            using var logEntry = archive.Entries.Single(e => e.FullName == "Current.log").Open();
            using var logReader = new StreamReader(logEntry, Encoding.UTF8);
            var logText = logReader.ReadToEnd();
            // The walk really logged: the ending's window line and the redacted dump shapes are all there.
            Assert.Contains("EXCHANGE FAILURE code=bad-credentials", logText, StringComparison.Ordinal);
            Assert.Contains("LOG REPORT", logText, StringComparison.Ordinal);
        }

        server.ThrowIfScriptFailed();
        Directory.Delete(outputDirectory, recursive: true);
    }
}
