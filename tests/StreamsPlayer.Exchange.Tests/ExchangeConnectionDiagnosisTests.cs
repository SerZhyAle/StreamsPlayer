using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0225: one fixture per step of the "won't connect" check, against the shipping service and a scripted TLS
/// server. Each one drives a real failure, reads the key the screens would show, walks the ladder with the real
/// probes (adapters and name lookup scripted so the machine running the test cannot change the answer) and holds
/// the step it stops at. The texts of the rows are held by the vocabulary test in the Core tests.
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private static readonly char[] DiagnosisSecret = ['d', 'i', 'a', 'g', '-', 's', 'e', 'c', 'r', 'e', 't'];

    private static ExchangeDiagnosisProbes QuietProbes(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(adapters: () => [], connectBudget: TimeSpan.FromSeconds(5), resolve: resolve);

    private static async Task<ExchangeDiagnosisResult> Walk(string? statusKey, string host, int port,
        ExchangeDiagnosisProbes? probes = null)
    {
        var row = ExchangeFailureRows.ByMessageKey(statusKey);
        Assert.NotNull(row);
        return await ExchangeDiagnosisEngine.RunAsync(row, host, port, probes ?? QuietProbes(), CancellationToken.None);
    }

    private static ExchangeDiagnosisState StateOf(ExchangeDiagnosisResult result, ExchangeDiagnosisStep step) =>
        result.Steps.Single(item => item.Step == step).State;

    private static void AssertReportCarriesNothingPrivate(ExchangeDiagnosisResult result, params string[] secrets)
    {
        var line = result.ToRedactedLine();
        foreach (var secret in secrets.Concat(["test-login", "localhost", "127.0.0.1", "diag-secret", new string('t', 43)]))
        {
            Assert.DoesNotContain(secret, line, StringComparison.OrdinalIgnoreCase);
        }

        // The line goes through the same log and bundle every diagnostic does, and comes out as it went in.
        using var scope = new TestDirectory();
        var log = new CurrentLog(scope.Path);
        log.Information(line);
        log.Dispose();
        var archivePath = DiagnosticArchiveBuilder.Build(scope.Path, scope.Path, "diagnosis check", DateTimeOffset.UtcNow);
        using var archive = ZipFile.OpenRead(archivePath);
        var all = string.Concat(archive.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }));
        Assert.Contains(line, all, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARefusedPort_IsUnreachable_AndTheCheckStopsAtTheAddress()
    {
        // expected: a closed port on a private host answers "refused": the machine is awake (step 1 passes) and the
        // address or port is what is off, so the walk stops at the address step with the cannot-reach row.
        using var scope = new TestDirectory();
        var port = FreePort();
        var service = new ExchangeSourceService(scope.Path);

        await service.EnrollAsync("127.0.0.1", port, "test-login", false, DiagnosisSecret.ToArray(), null);

        Assert.Equal("ExchangeUnreachable", service.EnrollmentOutcomeKey);
        Assert.Equal(("127.0.0.1", port), service.LastAttemptEndpoint);
        var result = await Walk(service.EnrollmentOutcomeKey, "127.0.0.1", port);
        Assert.Equal(ExchangeFailureRows.Unreachable, result.Row);
        Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.PcAsleep));
        Assert.Equal(ExchangeDiagnosisState.Failed, StateOf(result, ExchangeDiagnosisStep.AddressLan));
        Assert.Equal(ExchangeDiagnosisStep.AddressLan, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisState.NotReached, StateOf(result, ExchangeDiagnosisStep.Vpn));
        AssertReportCarriesNothingPrivate(result, port.ToString());
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsUnreachable_AndTheCheckStopsWhereTheRouteSaysItShould()
    {
        // The classification: a failed name lookup is a socket failure, and with no open connection it is "cannot
        // reach the server".
        Assert.Equal(ExchangeFailureRows.Unreachable,
            ExchangeFailures.Of(new SocketException((int)SocketError.HostNotFound), connectionOpen: false));

        static Task<IPAddress[]> NotFound(string name, CancellationToken token) =>
            Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

        // A dotted name is reached from outside: the step that asks to try the server from another network.
        var dotted = await Walk("ExchangeUnreachable", "exchange.example.invalid", 443, QuietProbes(NotFound));
        Assert.Equal(ExchangeDiagnosisApplies.Internet, dotted.Route);
        Assert.Equal(ExchangeDiagnosisState.CouldNotCheck, StateOf(dotted, ExchangeDiagnosisStep.PcAsleep));
        Assert.Equal(ExchangeDiagnosisState.NotApplicable, StateOf(dotted, ExchangeDiagnosisStep.AddressLan));
        Assert.Equal(ExchangeDiagnosisStep.ServerInternet, dotted.AdviceStep);

        // A one-word name is a machine on the local network: the step that asks to check the address.
        var machine = await Walk("ExchangeUnreachable", "sp0225-studio", 443, QuietProbes(NotFound));
        Assert.Equal(ExchangeDiagnosisApplies.Network, machine.Route);
        Assert.Equal(ExchangeDiagnosisStep.AddressLan, machine.AdviceStep);
        AssertReportCarriesNothingPrivate(dotted, "exchange.example", "sp0225-studio");
    }

    [Fact]
    public async Task AServerThatCannotCompleteTheProtectedHandshake_IsTlsFailed_AndTheCheckOffersTheClock()
    {
        // A listener that takes the connection and closes it before any TLS: the TCP connection exists, the protected
        // one cannot be set up. That is neither an unreachable host nor a refusal.
        using var scope = new TestDirectory();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            client.Client.Shutdown(SocketShutdown.Both);
        });
        try
        {
            var service = new ExchangeSourceService(scope.Path);

            await service.EnrollAsync("127.0.0.1", port, "test-login", false, DiagnosisSecret.ToArray(), null);
            await accepting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("ExchangeTlsFailed", service.EnrollmentOutcomeKey);
            var result = await Walk(service.EnrollmentOutcomeKey, "127.0.0.1", port);
            Assert.Equal(ExchangeFailureRows.TlsFailed, result.Row);
            Assert.Equal(ExchangeDiagnosisStep.DeviceClock, result.AdviceStep);
            Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.PcAsleep));
            Assert.Equal(ExchangeDiagnosisState.NotReached, StateOf(result, ExchangeDiagnosisStep.Certificate));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ASignInTheServerNeverAnswered_LeavesNoAccount_AndTheCheckReadsTheOutcomeOfThatAttempt()
    {
        // The case the owner's real test server showed: it read the enroll envelope and closed the stream with no
        // frame (item AN). No account exists, so the connection line is not this attempt's - the outcome is, and the
        // check must open on "the server did not answer" and offer the server's operator, not walk "cannot reach".
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("enroll", ExchangeProtocol.String(enrollment.RootElement, "type"));
        });
        var service = new ExchangeSourceService(scope.Path);

        await service.EnrollAsync("localhost", server.Port, "test-login", false, DiagnosisSecret.ToArray(),
            ExchangeProtocol.Fingerprint(certificate.RawData));

        Assert.False(service.HasAccount);
        Assert.Equal("ExchangeUnanswered", service.EnrollmentOutcomeKey);
        Assert.Equal(("localhost", server.Port), service.LastAttemptEndpoint);
        var request = ExchangeDiagnosisRequest.ForStatus(service.EnrollmentOutcomeKey, string.Empty, "localhost", server.Port);
        Assert.Equal(ExchangeFailureRows.Unanswered, request.Row);
        Assert.Equal("ExchangeUnanswered", request.HappenedKey);
        var result = await Walk(request.HappenedKey, "localhost", server.Port);
        Assert.Equal(ExchangeDiagnosisStep.Operator, result.AdviceStep);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AChangedCertificate_IsTheCertificateStep()
    {
        using var certificate = Certificate();
        using var presented = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(presented, (_, tls, token) => HoldOpen(tls, token));
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeCertificateChanged");

            var result = await Walk(service.StatusKey, "localhost", server.Port);
            Assert.Equal(ExchangeFailureRows.CertificateChanged, result.Row);
            Assert.Equal(ExchangeDiagnosisStep.Certificate, result.AdviceStep);
            Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.PcAsleep));
            Assert.Equal(ExchangeDiagnosisState.Failed, StateOf(result, ExchangeDiagnosisStep.Certificate));
            Assert.Equal(ExchangeDiagnosisState.NotReached, StateOf(result, ExchangeDiagnosisStep.Credentials));
            AssertReportCarriesNothingPrivate(result, ExchangeProtocol.Fingerprint(certificate.RawData));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task AStreamClosedWithNoAnswer_IsUnanswered_NeverUnreachable_AndTheCheckOffersTheOperator()
    {
        // expected: the handshake completed and this product's own envelope was read, then the stream closed with no
        // frame (item AN): "the server did not answer", and the only step left to offer is the server's operator.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            Assert.Equal("hello", ExchangeProtocol.String(hello.RootElement, "type"));
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeUnanswered");

            var result = await Walk(service.StatusKey, "localhost", server.Port);
            Assert.Equal(ExchangeFailureRows.Unanswered, result.Row);
            Assert.Equal(ExchangeDiagnosisStep.Operator, result.AdviceStep);
            Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.Certificate));
            Assert.Equal(ExchangeDiagnosisState.CouldNotCheck, StateOf(result, ExchangeDiagnosisStep.Operator));
            AssertReportCarriesNothingPrivate(result);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Theory]
    [InlineData(false, "ExchangeBadCredentials")]
    [InlineData(true, "ExchangeBadCredentialsCode")]
    public async Task RefusedCredentials_AreARefusal_AndTheCheckStopsAtTheCredentials(bool oneTimeCode, string key)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "bad-credentials" }, false, token);
        });
        var service = new ExchangeSourceService(scope.Path);

        await service.EnrollAsync("localhost", server.Port, "test-login", oneTimeCode, DiagnosisSecret.ToArray(),
            ExchangeProtocol.Fingerprint(certificate.RawData));

        Assert.Equal(key, service.EnrollmentOutcomeKey);
        var result = await Walk(service.EnrollmentOutcomeKey, "localhost", server.Port);
        Assert.Equal(ExchangeDiagnosisStep.Credentials, result.AdviceStep);
        // A refusal is the server's answer: every step of the link before it is passed on that evidence, and nothing
        // is probed - a refusal never reads as a network failure.
        Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.PcAsleep));
        Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.Certificate));
        Assert.Equal(ExchangeDiagnosisState.Failed, StateOf(result, ExchangeDiagnosisStep.Credentials));
        Assert.Equal(ExchangeDiagnosisState.NotReached, StateOf(result, ExchangeDiagnosisStep.SignInRate));
        AssertReportCarriesNothingPrivate(result, "diag-secret");
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task SignedOutByTheAccount_IsARefusal_AndTheCheckStopsAtTheCredentials()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "signed-out" }, false, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeSignedOut");

            var result = await Walk(service.StatusKey, "localhost", server.Port);
            Assert.Equal(ExchangeFailureRows.SignedOut, result.Row);
            Assert.Equal(ExchangeDiagnosisStep.Credentials, result.AdviceStep);
            Assert.Equal(ExchangeDiagnosisState.Passed, StateOf(result, ExchangeDiagnosisStep.Certificate));
            AssertReportCarriesNothingPrivate(result);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task ANewerVersion_IsTheDifferentVersionsRow_NeverAnUpdateDemand_AndTheCheckOffersTheOperator()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "version-unsupported" }, false, token);
        });
        var service = new ExchangeSourceService(scope.Path);

        await service.EnrollAsync("localhost", server.Port, "test-login", false, DiagnosisSecret.ToArray(),
            ExchangeProtocol.Fingerprint(certificate.RawData));

        Assert.Equal("ExchangeVersionUnsupported", service.EnrollmentOutcomeKey);
        var result = await Walk(service.EnrollmentOutcomeKey, "localhost", server.Port);
        Assert.Equal(ExchangeDiagnosisStep.Operator, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisState.Failed, StateOf(result, ExchangeDiagnosisStep.Operator));
        // A newer connection string is the local row with no step: its own text, alone.
        var newerString = ExchangeConnectionImport.ReadText("FMSEXCH2:x=localhost:443");
        Assert.Equal(ExchangeFailureRows.FormatUnsupported.MessageKey, newerString.MessageKey);
        var alone = await Walk(newerString.MessageKey, "localhost", server.Port);
        Assert.Empty(alone.Steps);
        Assert.Null(alone.AdviceStep);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task TheCheckConnectsOnlyWhenAskedFor_ASignedInServiceMakesNoExtraConnection()
    {
        // The request log of the server stays at the one session the service itself keeps: nothing probes the
        // server on a timer, at start-up or after a failure. The engine runs only from the check window.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => service.StatusKey == "ExchangeOnline");
            await Task.Delay(2500);

            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }
    }
}
