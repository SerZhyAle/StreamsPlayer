using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0226 / DEVICE-EXCHANGE item DF against a local TLS fixture: the device key is made before `enroll` and
/// declared with the capability `e2e-v1`; a known device id repeats the key it has (a token rotation) and one
/// enrolled before keys existed sends neither, because a known id cannot be given a key; the private key lives
/// only in the protected account record; the server's `sealed` feature is read (item DP).
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private static Task EnrolledAs(System.Net.Security.SslStream tls, string[] features, CancellationToken token) =>
        ExchangeProtocol.WriteAsync(tls, new
        {
            schemaVersion = 2, type = "enrolled", deviceToken = new string('k', 43), account = "test-login",
            accountKind = "member", publicEndpoint = "localhost:44022", serverVersion = "test", features, keepaliveSeconds = 30
        }, false, token);

    private static async Task<JsonDocument> EnrollOnce(ExchangeAccount? existing, string[] features)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        JsonDocument? seen = null;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            seen = await ExchangeProtocol.ReadAsync(tls, false, token);
            await EnrolledAs(tls, features, token);
            await HoldOpen(tls, token);
        });
        var original = existing is null ? null : existing with { Port = server.Port, Pin = ExchangeProtocol.Fingerprint(certificate.RawData) };
        if (original is not null)
        {
            scope.Save(original);
        }

        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, "test-login", false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData));
            LastService = service.Account;
            LastFeatures = (service.ServerSeals, service.OwnKeyMissing);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
        return seen!;
    }

    // The account and facts the last EnrollOnce ended with, for the assertions that follow it.
    private static ExchangeAccount? LastService;
    private static (bool ServerSeals, bool OwnKeyMissing) LastFeatures;

    [Fact]
    public async Task AFreshSignIn_DeclaresTheCapabilityAndTheKeyBeforeEnroll_AndKeepsThePrivateHalfInTheRecord()
    {
        using var frame = await EnrollOnce(null, ["directory", "sealed"]);
        var root = frame.RootElement;

        Assert.Equal(["e2e-v1"], root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray());
        var key = root.GetProperty("e2eKey");
        Assert.Equal(1, key.GetProperty("v").GetInt32());
        Assert.True(ExchangeBase64Url.TryDecode(key.GetProperty("kem").GetString(), out var point));
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
        // The record holds the pair, the pair is a consistent one, and the server's feature is read.
        using var stored = LastService!.LoadE2eKey();
        Assert.NotNull(stored);
        Assert.Equal(key.GetProperty("kem").GetString(), stored.Kem);
        Assert.True(LastFeatures.ServerSeals);
        Assert.False(LastFeatures.OwnKeyMissing);
        // The private scalar is nowhere in what the server was sent.
        Assert.DoesNotContain(LastService.E2ePrivateKey!, frame.RootElement.GetRawText());
    }

    [Fact]
    public async Task AServerWithoutTheSealedFeature_IsReadAsSuch()
    {
        using var frame = await EnrollOnce(null, ["directory"]);

        Assert.False(LastFeatures.ServerSeals);
    }

    [Fact]
    public async Task ASignedOutDevice_EnrollsAgainWithTheKeyItHas()
    {
        using var pair = ExchangeE2eKeyPair.Generate();
        var existing = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", 1, "test-login", null, null, true,
            SignedOut: true).WithE2eKey(pair);

        using var frame = await EnrollOnce(existing, ["directory", "sealed"]);

        Assert.Equal(existing.DeviceId, frame.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(pair.Kem, frame.RootElement.GetProperty("e2eKey").GetProperty("kem").GetString());
        Assert.Equal(pair.Kem, LastService!.E2eKem);
    }

    [Fact]
    public async Task AnEntryFromBeforeKeysExisted_SendsNeitherTheCapabilityNorAKey_AndStaysKeyless()
    {
        var legacy = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", 1, "test-login", null, null, true, SignedOut: true);
        Assert.True(legacy.IsKeylessLegacy);

        using var frame = await EnrollOnce(legacy, ["directory", "sealed"]);

        Assert.False(frame.RootElement.TryGetProperty("capabilities", out _));
        Assert.False(frame.RootElement.TryGetProperty("e2eKey", out _));
        Assert.Equal(legacy.DeviceId, frame.RootElement.GetProperty("deviceId").GetString());
        Assert.Null(LastService!.E2ePrivateKey);
        // Signed in with a token and no key: the person is told, and nothing is sealed to or opened by this entry.
        Assert.True(LastFeatures.OwnKeyMissing);
    }

    [Fact]
    public void TheAccountRecordNeverPrintsItsTokenOrItsKey()
    {
        using var pair = ExchangeE2eKeyPair.Generate();
        var account = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", 1, "test-login", new string('t', 43))
            .WithE2eKey(pair);

        var text = account.ToString();

        Assert.DoesNotContain(account.Token!, text);
        Assert.DoesNotContain(account.E2ePrivateKey!, text);
        Assert.DoesNotContain(account.E2eKem!, text);
    }

    [Fact]
    public void ARecordWhoseHalvesDisagreeLoadsAsNoKey_NeverAsABrokenOne()
    {
        using var one = ExchangeE2eKeyPair.Generate();
        using var other = ExchangeE2eKeyPair.Generate();
        var account = new ExchangeAccount(ExchangeProtocol.NewDeviceId()).WithE2eKey(one) with { E2eKem = other.Kem };

        Assert.Null(account.LoadE2eKey());
        Assert.Null(new ExchangeAccount(ExchangeProtocol.NewDeviceId()).LoadE2eKey());
    }

    [Fact]
    public void TheTrustBookKeepsTheFirstKeyAcrossARestartAndForgetsAnEntryThatLeft()
    {
        using var scope = new TestDirectory();
        var first = new ExchangeTrustBook(scope.Path);
        var point = ExchangeE2eKeyPair.Generate().PublicPoint;
        first.For("entry-1").ObserveDevice("device-a", point);
        first.For("entry-2").ObserveDevice("device-b", ExchangeE2eKeyPair.Generate().PublicPoint);
        first.SaveIfDirty();

        var restarted = new ExchangeTrustBook(scope.Path);
        Assert.Equal(point, restarted.For("entry-1").TrustedDevice("device-a"));
        Assert.Equal(ExchangeKeyObservation.KeyChanged,
            restarted.For("entry-1").ObserveDevice("device-a", ExchangeE2eKeyPair.Generate().PublicPoint));

        restarted.Forget("entry-1");
        var afterLeave = new ExchangeTrustBook(scope.Path);
        Assert.Null(afterLeave.For("entry-1").TrustedDevice("device-a"));
        Assert.NotNull(afterLeave.For("entry-2").TrustedDevice("device-b"));
    }

    [Fact]
    public void ACorruptTrustFileIsAnEmptyBook_NotAFailure()
    {
        using var scope = new TestDirectory();
        File.WriteAllBytes(Path.Combine(scope.Path, "exchange-trust.bin"), [1, 2, 3, 4]);

        var book = new ExchangeTrustBook(scope.Path);

        Assert.Empty(book.For("entry").DeviceIds);
    }
}
