using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed class ExchangeProtectionTests
{
    [Fact]
    public void ProtectedAccount_RoundTripsWithoutClearIdentityOrToken()
    {
        WithDirectory(directory =>
        {
            var store = new ExchangeAccountStore(directory);
            var account = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "private-host", 1234,
                "private-login", new string('t', 43), "SHA256:" + new string('f', 43), true);
            store.Save(account);
            Assert.Equal(account, store.Load());
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "exchange-account.bin")));
            foreach (var secret in new[] { account.DeviceId, account.Host, account.Login, account.Token!, account.Pin! })
            {
                Assert.DoesNotContain(secret, text);
            }
        });
    }

    [Fact]
    public void LostProtection_ChangesIdentityBeforeAnyReEnrollment()
    {
        WithDirectory(directory =>
        {
            var store = new ExchangeAccountStore(directory);
            var original = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), Token: new string('t', 43));
            store.Save(original);
            File.WriteAllBytes(Path.Combine(directory, "exchange-account.bin"), [1, 2, 3]);
            var recovered = store.Load();
            Assert.True(store.ProtectionFailed);
            Assert.NotEqual(original.DeviceId, recovered.DeviceId);
            Assert.Null(recovered.Token);
            Assert.False(recovered.Enabled);
        });
    }

    [Fact]
    public async Task UnacceptedCertificate_RefusesTlsBeforeSendingSecrets()
    {
        using var certificate = Certificate();
        await WithTlsServer(certificate, async port =>
        {
            var exception = await Assert.ThrowsAsync<ExchangeCertificateException>(() =>
                ExchangeTlsConnection.OpenAsync("localhost", port, null, default));
            Assert.Null(exception.Previous);
            Assert.Equal(ExchangeProtocol.Fingerprint(certificate.RawData), exception.Presented);
        });
    }

    [Fact]
    public async Task ExactApprovedLeaf_AllowsEncryptedTransport()
    {
        using var certificate = Certificate();
        await WithTlsServer(certificate, async port =>
        {
            using var connection = await ExchangeTlsConnection.OpenAsync("localhost", port,
                ExchangeProtocol.Fingerprint(certificate.RawData), default);
            Assert.True(connection.Stream.IsAuthenticated);
            Assert.True(connection.Stream.IsEncrypted);
        });
    }

    [Fact]
    public async Task ChangedLeaf_RefusesWithBothFingerprints()
    {
        using var oldCertificate = Certificate();
        using var newCertificate = Certificate();
        var oldPin = ExchangeProtocol.Fingerprint(oldCertificate.RawData);
        await WithTlsServer(newCertificate, async port =>
        {
            var exception = await Assert.ThrowsAsync<ExchangeCertificateException>(() =>
                ExchangeTlsConnection.OpenAsync("localhost", port, oldPin, default));
            Assert.Equal(oldPin, exception.Previous);
            Assert.Equal(ExchangeProtocol.Fingerprint(newCertificate.RawData), exception.Presented);
        });
    }

    [Theory]
    [InlineData("rate-limited", "ExchangeRateLimited")]
    [InlineData("capacity", "ExchangeCapacity")]
    [InlineData("device-revoked", "ExchangeDeviceRevoked")]
    public async Task HelloRefusal_PreservesItsLocalizedState(string reason, string expectedKey)
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43), ExchangeProtocol.Fingerprint(certificate.RawData), true));
                service = new ExchangeSourceService(directory);
                service.Start();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (service.StatusKey != expectedKey)
                {
                    await Task.Delay(20, deadline.Token);
                }

                await Task.Delay(100);
                Assert.Equal(expectedKey, service.StatusKey);
                if (reason == "device-revoked")
                {
                    Assert.Null(service.Account.Token);
                    Assert.False(service.Account.Enabled);
                    await Task.Delay(1200);
                    Assert.Equal(expectedKey, service.StatusKey);
                }
            }, async (tls, token) =>
            {
                using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
                Assert.Equal("hello", ExchangeProtocol.String(opening.RootElement, "type"));
                await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason }, false, token);
            });
        }
        finally
        {
            if (service is not null)
            {
                await service.StopAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot use the ephemeral key returned by CreateSelfSigned directly.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private static async Task WithTlsServer(X509Certificate2 certificate, Func<int, Task> test,
        Func<SslStream, CancellationToken, Task>? handle = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = ServeAsync();
        try
        {
            await test(((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            await serve;
        }

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                if (handle is null)
                {
                    await Task.Delay(Timeout.Infinite, deadline.Token);
                }
                else
                {
                    await handle(tls, deadline.Token);
                }
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException)
            {
                // Refused leaf or test teardown: neither path carries an application envelope.
            }
        }
    }

    private static void WithDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
