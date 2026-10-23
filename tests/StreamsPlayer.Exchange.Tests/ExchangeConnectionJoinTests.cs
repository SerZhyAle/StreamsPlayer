using System.IO;
using System.IO.Compression;
using System.Text;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed partial class ExchangeLoopRecoveryTests
{
    [Fact]
    public async Task ImportedPinMismatch_RefusesBeforePassword_AndPinsNothing()
    {
        using var certificate = Certificate();
        using var otherCertificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, AssertNoEnvelope);
        var service = new ExchangeSourceService(scope.Path);
        var expected = ExchangeProtocol.Fingerprint(otherCertificate.RawData);
        var presented = ExchangeProtocol.Fingerprint(certificate.RawData);
        var refusal = await Assert.ThrowsAsync<ExchangeCertificateException>(() =>
            service.InspectCertificateAsync("localhost", server.Port, expected));
        Assert.Equal(expected, refusal.Previous);
        Assert.Equal(presented, refusal.Presented);
        Assert.Equal("ExchangeStringCertificateMismatch", service.EnrollmentOutcomeKey);
        Assert.Null(service.Account.Pin);
        Assert.Null(service.PresentedFingerprint);
        Assert.Null(new ExchangeAccountStore(scope.Path).Load().Pin);
        var secret = "private-password".ToCharArray();
        await Assert.ThrowsAsync<ExchangeCertificateException>(() => service.EnrollAsync("localhost", server.Port,
            "test-login", false, secret, expected, requireImportedPin: true));
        Assert.All(secret, c => Assert.Equal('\0', c));
        server.ThrowIfScriptFailed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingImportedPin_PasswordThenProtectedTokenAndList(bool badCredentials)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secret = "private-password".ToCharArray();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            // Inspection has verified TLS but must not write an enrollment or any other envelope.
            if (index == 0)
            {
                var buffer = new byte[1];
                Assert.Equal(0, await tls.ReadAsync(buffer, token));
                return;
            }
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.Equal("private-password", ExchangeProtocol.String(frame.RootElement, "password"));
            Assert.Equal(new[] { "receiver", "broadcaster" }, frame.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            await Until(() => secret.All(c => c == '\0'));
            if (badCredentials)
            {
                await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "bad-credentials" }, false, token);
                return;
            }
            await Enrolled(tls, token);
            using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("keepalive", ExchangeProtocol.String(keepalive.RootElement, "type"));
            using var list = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("list", ExchangeProtocol.String(list.RootElement, "type"));
            Assert.NotNull(new ExchangeAccountStore(scope.Path).Load().Token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "directory", revision = 1,
                devices = Array.Empty<object>(), resources = Array.Empty<object>(), broadcasts = Array.Empty<object>() }, true, token);
            listed.SetResult();
            await HoldOpen(tls, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            var pin = ExchangeProtocol.Fingerprint(certificate.RawData);
            Assert.Equal(pin, await service.InspectCertificateAsync("localhost", server.Port, pin));
            Assert.Null(service.Account.Token);
            await service.EnrollAsync("localhost", server.Port, "test-login", false, secret, pin, requireImportedPin: true);
            Assert.All(secret, c => Assert.Equal('\0', c));
            if (badCredentials)
            {
                Assert.Equal("ExchangeBadCredentials", service.EnrollmentOutcomeKey);
                Assert.Null(service.Account.Token);
            }
            else
            {
                // SP-0222: the session lists only when a lease asks, so the test is the list that is open.
                using var lease = service.AcquireDirectory(follow: true);
                await listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => service.Directory is not null);
                Assert.Equal(pin, service.Account.Pin);
                Assert.DoesNotContain(new string('k', 43), Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(scope.Path, "exchange-account.bin"))));
            }
        }
        finally { await service.StopAsync(); }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task UnpinnedInspection_ReturnsFingerprintWithoutEnrollmentOrStorage()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, AssertNoEnvelope);
        var service = new ExchangeSourceService(scope.Path);
        Assert.Equal(ExchangeProtocol.Fingerprint(certificate.RawData),
            await service.InspectCertificateAsync("localhost", server.Port));
        Assert.Null(service.Account.Pin);
        Assert.Null(service.Account.Token);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task CancelledEnrollmentAndFailedRead_KeepPrivateInputOutOfLogAndBundle()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        using var log = new CurrentLog(scope.Path);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            pending.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        var pin = ExchangeProtocol.Fingerprint(certificate.RawData);
        var text = ExchangeConnectionStringCodec.Encode(new("localhost", server.Port, pin, "private-name", "private-login"));
        Assert.NotNull(ExchangeConnectionImport.ReadText(text).Data);
        Assert.Null(service.Account.Token);
        var privatePath = Path.Combine(scope.Path, "private-file.fmsexch");
        Assert.Equal("ExchangeStringInvalid", (await ExchangeConnectionImport.ReadFileAsync(privatePath)).MessageKey);
        Assert.Equal("ExchangeStringInvalid", ExchangeConnectionImport.ReadText(text + "&fp=bad").MessageKey);
        Assert.Equal("ExchangeFormatUnsupported", ExchangeConnectionImport.ReadText("FMSEXCH2:x=localhost:443").MessageKey);
        using var cancellation = new CancellationTokenSource();
        var secret = "private-password".ToCharArray();
        var joining = service.EnrollAsync("localhost", server.Port, "private-login", false, secret, pin,
            cancellationToken: cancellation.Token, requireImportedPin: true);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await joining.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(secret, c => Assert.Equal('\0', c));
        Assert.Null(service.Account.Token);
        Assert.Equal("ExchangeJoinCancelled", service.EnrollmentOutcomeKey);
        using var otherCertificate = Certificate();
        await Assert.ThrowsAsync<ExchangeCertificateException>(() => service.InspectCertificateAsync("localhost", server.Port,
            ExchangeProtocol.Fingerprint(otherCertificate.RawData)));
        log.Event("CHECK", "connection import finished");
        log.Dispose();
        AssertPrivateAbsent(File.ReadAllText(Path.Combine(scope.Path, DiagnosticLogFiles.CurrentLogName)));
        var archivePath = DiagnosticArchiveBuilder.Build(scope.Path, scope.Path, "import check", DateTimeOffset.UtcNow);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            AssertPrivateAbsent(await reader.ReadToEndAsync());
        }
        server.ThrowIfScriptFailed();

        void AssertPrivateAbsent(string output)
        {
            foreach (var value in new[] { text, "localhost", pin, privatePath, "private-login", "private-password", "private-name" })
                Assert.DoesNotContain(value, output);
        }
    }

    [Fact]
    public async Task OversizeFileAndContentRecognition_RefuseWithoutPartialData()
    {
        using var scope = new TestDirectory();
        var path = Path.Combine(scope.Path, "not-an-extension.txt");
        await File.WriteAllTextAsync(path, "\uFEFF FMSEXCH1:x=server.example%3A443&u=Viewer\n");
        var result = await ExchangeConnectionImport.ReadFileAsync(path);
        Assert.Equal("server.example", result.Data?.Host);
        Assert.Equal("viewer", result.Data?.Login);
        await File.WriteAllBytesAsync(path, new byte[ExchangeConnectionStringCodec.MaximumFileBytes + 1]);
        Assert.Null((await ExchangeConnectionImport.ReadFileAsync(path)).Data);
        Assert.True(ExchangeConnectionImport.Recognizes("\uFEFF  FMSEXCH2:x=bad"));
        Assert.False(ExchangeConnectionImport.Recognizes("https://server.example"));
    }

    [Fact]
    public async Task FreshIdCollision_RegeneratesOnce_AndJoins()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstId = null;
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            // The writer wipes the array it is given: the retry must carry the typed password, not NUL characters.
            Assert.Equal("private-password", ExchangeProtocol.String(frame.RootElement, "password"));
            var deviceId = ExchangeProtocol.String(frame.RootElement, "deviceId");
            if (index == 0)
            {
                firstId = deviceId;
                await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "id-collision" }, false, token);
                return;
            }

            Assert.NotNull(firstId);
            Assert.NotEqual(firstId, deviceId);
            await Enrolled(tls, token);
            using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("keepalive", ExchangeProtocol.String(keepalive.RootElement, "type"));
            using var list = await ExchangeProtocol.ReadAsync(tls, true, token);
            Assert.Equal("list", ExchangeProtocol.String(list.RootElement, "type"));
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "directory", revision = 1,
                devices = Array.Empty<object>(), resources = Array.Empty<object>(), broadcasts = Array.Empty<object>() }, true, token);
            listed.SetResult();
            await HoldOpen(tls, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            var pin = ExchangeProtocol.Fingerprint(certificate.RawData);
            var secret = "private-password".ToCharArray();
            await service.EnrollAsync("localhost", server.Port, "test-login", false, secret, pin);
            Assert.All(secret, c => Assert.Equal('\0', c));
            Assert.Null(service.EnrollmentOutcomeKey);
            Assert.NotNull(service.Account.Token);
            Assert.NotEqual(firstId, service.Account.DeviceId);
            Assert.Equal(service.Account.DeviceId, new ExchangeAccountStore(scope.Path).Load().DeviceId);
            // SP-0222: the session lists only when a lease asks, so the test is the list that is open.
            using var lease = service.AcquireDirectory(follow: true);
            await listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await service.StopAsync(); }
        Assert.Equal(2, server.Connections);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task KeptIdCollision_RefusesWithoutRegeneration()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var pin = ExchangeProtocol.Fingerprint(certificate.RawData);
        var keptId = ExchangeProtocol.NewDeviceId();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
            Assert.Equal(keptId, ExchangeProtocol.String(frame.RootElement, "deviceId"));
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "id-collision" }, false, token);
        });
        // The same endpoint the join names, so the kept entry is re-enrolled and never left first.
        new ExchangeAccountStore(scope.Path).Save(new ExchangeAccount(keptId, "localhost", server.Port,
            "test-login", new string('t', 43), pin, true));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, "test-login", false, "private-password".ToCharArray(), pin);
            Assert.Equal("ExchangeIdCollision", service.EnrollmentOutcomeKey);
            Assert.Equal(keptId, service.Account.DeviceId);
            Assert.NotNull(service.Account.Token);
        }
        finally { await service.StopAsync(); }
        Assert.Equal(1, server.Connections);
        server.ThrowIfScriptFailed();
    }

    // ------------------------------------------------------------------ a string's fp for an entry that holds a pin (item BN)

    private static async Task EnrollThenHold(System.Net.Security.SslStream tls, CancellationToken token)
    {
        using var frame = await ExchangeProtocol.ReadAsync(tls, false, token);
        Assert.Equal("enroll", ExchangeProtocol.String(frame.RootElement, "type"));
        await Enrolled(tls, token);
        await HoldOpen(tls, token);
    }

    [Fact]
    public async Task ImportedFp_WithAnotherKeyThanTheEntryPin_IsAChangedCertificate_ReplacedOnlyOnConfirmation()
    {
        using var heldKey = System.Security.Cryptography.RSA.Create(2048);
        using var foreignKey = System.Security.Cryptography.RSA.Create(2048);
        using var held = CertificateFor(heldKey, "localhost");
        using var foreign = CertificateFor(foreignKey, "localhost");
        using var scope = new TestDirectory();
        // Connections 0 (the probe) and 1 (the unconfirmed enrollment) must never be sent a frame.
        await using var server = new ScriptedServer(foreign, (index, tls, token) =>
            index < 2 ? AssertNoEnvelope(index, tls, token) : EnrollThenHold(tls, token));
        var original = Account(server.Port, held) with { PinKey = ExchangeProtocol.PublicKeyFingerprint(held.RawData) };
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        var stringFp = ExchangeProtocol.Fingerprint(foreign.RawData);
        try
        {
            var probe = await service.ProbeCertificateAsync("localhost", server.Port, stringFp);
            Assert.Equal(stringFp, probe?.Fingerprint);
            Assert.True(probe?.ReplacesHeldPin);

            var unconfirmed = "private-password".ToCharArray();
            var refusal = await Assert.ThrowsAsync<ExchangeCertificateException>(() => service.EnrollAsync("localhost", server.Port,
                "test-login", false, unconfirmed, stringFp, requireImportedPin: true));
            Assert.Equal(original.Pin, refusal.Previous);
            Assert.Equal(stringFp, refusal.Presented);
            Assert.All(unconfirmed, c => Assert.Equal('\0', c));
            Assert.Equal(original.Pin, service.Account.Pin);
            Assert.Equal(original.PinKey, service.Account.PinKey);
            Assert.Equal(original.Pin, new ExchangeAccountStore(scope.Path).Load().Pin);

            await service.EnrollAsync("localhost", server.Port, "test-login", false, "private-password".ToCharArray(), stringFp,
                requireImportedPin: true, pinReplacementConfirmed: true);
            Assert.Null(service.EnrollmentOutcomeKey);
            Assert.Equal(stringFp, service.Account.Pin);
            Assert.Equal(ExchangeProtocol.PublicKeyFingerprint(foreign.RawData), service.Account.PinKey);
            Assert.Equal(stringFp, new ExchangeAccountStore(scope.Path).Load().Pin);
        }
        finally { await service.StopAsync(); }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ImportedFp_WithTheEntryKey_IsARenewal_NeedsNoConfirmation()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var held = CertificateFor(key, "localhost");
        using var renewed = CertificateFor(key, "localhost");
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(renewed, (index, tls, token) =>
            index == 0 ? AssertNoEnvelope(index, tls, token) : EnrollThenHold(tls, token));
        scope.Save(Account(server.Port, held) with { PinKey = ExchangeProtocol.PublicKeyFingerprint(held.RawData) });
        var service = new ExchangeSourceService(scope.Path);
        var stringFp = ExchangeProtocol.Fingerprint(renewed.RawData);
        try
        {
            var probe = await service.ProbeCertificateAsync("localhost", server.Port, stringFp);
            Assert.False(probe?.ReplacesHeldPin);

            await service.EnrollAsync("localhost", server.Port, "test-login", false, "private-password".ToCharArray(), stringFp,
                requireImportedPin: true);
            Assert.Null(service.EnrollmentOutcomeKey);
            Assert.Equal(stringFp, service.Account.Pin);
            Assert.Equal(ExchangeProtocol.PublicKeyFingerprint(held.RawData), service.Account.PinKey);
        }
        finally { await service.StopAsync(); }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ImportedFp_ForAPinWhoseKeyWasNeverRecorded_CannotProveARenewal()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var held = CertificateFor(key, "localhost");
        using var renewed = CertificateFor(key, "localhost");
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(renewed, AssertNoEnvelope);
        scope.Save(Account(server.Port, held));
        var service = new ExchangeSourceService(scope.Path);

        var probe = await service.ProbeCertificateAsync("localhost", server.Port, ExchangeProtocol.Fingerprint(renewed.RawData));

        Assert.True(probe?.ReplacesHeldPin);
        server.ThrowIfScriptFailed();
    }

    private static async Task AssertNoEnvelope(int index, System.Net.Security.SslStream tls, CancellationToken token)
    {
        var buffer = new byte[1];
        Assert.Equal(0, await tls.ReadAsync(buffer, token));
    }
}
