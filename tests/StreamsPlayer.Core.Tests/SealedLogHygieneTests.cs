using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0226 requirement 3 / acceptance "Keys stay secret": a key never reaches a log line. The product's log is
/// `CurrentLog` (App only; Core has none), so the gate is the App's own sources, read as test DATA the way
/// `LocalizedCallSiteTests` reads them: no log statement names a member that holds a key, a box or a pair. The text
/// side of the same guarantee - a line that does carry one is masked whole - is <see cref="ExchangeDiagnosticRedactor"/>.
/// </summary>
public sealed partial class SealedLogHygieneTests
{
    /// <summary>The members and types that hold key material, a sealed box or a credential pair.</summary>
    private static readonly string[] SecretMembers =
    [
        "RelayKey", "GroupKey", "TunnelPin", "E2ePrivateKey", "E2eKem", "PrivateScalar", "ExportPrivateScalar", "LinkKey",
        "ExchangeBroadcastSecrets", "ExchangeSealedBox", ".Sealed", "SealedBox", "Secrets", ".Kem", "EpochKey", "ContentKey",
        "Seal.Key", "Seal.Pin", "DescriptorText", "OutwardDescriptor"
    ];

    [GeneratedRegex(@"(?s)\b(?:_log\??|log\??|CurrentLog|Log)\s*\.\s*(?:Event|Invoke|Write\w*)\s*\(.*?\)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex LogStatement();

    private static string AppSource => Path.Combine(AppContext.BaseDirectory, "app-source");

    [Fact]
    public void NoLogStatementOfTheAppNamesAKeyABoxOrAPair()
    {
        var offenders = new List<string>();
        var statements = 0;
        foreach (var file in Directory.EnumerateFiles(AppSource, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match statement in LogStatement().Matches(text))
            {
                statements++;
                foreach (var member in SecretMembers.Where(member => statement.Value.Contains(member, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {member} in `{statement.Value.ReplaceLineEndings(" ")[..Math.Min(120, statement.Value.Length)]}`");
                }
            }
        }

        Assert.True(statements > 100, $"the scan must have read the product's log statements ({statements})");
        Assert.True(offenders.Count == 0, "A log statement names key material: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// SP-0226 / item DP, "no clear fallback through the server": every playback leg that starts a tunnel forwarder or a
    /// relay proxy first asks <c>ExchangeBroadcastKeys.Decide</c> for the endpoint, so none can hand a relay or a tunnel to a
    /// player unsealed. A new leg that starts either without asking fails here, before it plays anything.
    /// </summary>
    [Fact]
    public void EveryLegThatStartsAForwarderOrAProxyAsksTheSealedDecisionFirst()
    {
        var starters = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppSource, "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("ExchangeTunnelForwarder.Start(", StringComparison.Ordinal)
                || text.Contains("ExchangeRelayProxy.Start(", StringComparison.Ordinal))
            {
                starters.Add(Path.GetFileName(file));
                Assert.True(text.Contains("ExchangeBroadcastKeys.Decide(", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} starts a forwarder or a proxy without asking the sealed decision");
            }
        }

        Assert.Contains("PlayerWindow.Legs.cs", starters);
        Assert.Contains("BroadcastAudioSession.cs", starters);
        Assert.Contains("MainWindow.FastMediaSorterPlayback.cs", starters);
    }
    [Fact]
    public void CoreHasNoLogging()
    {
        // Core is platform-neutral and logs nothing; every key rule in it is therefore also a rule about what it returns, not prints.
        var coreSources = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "app-source", "..", "core-source"), "*.cs").ToArray();
        Assert.Empty(coreSources.Where(file => File.ReadAllText(file).Contains("Console.WriteLine", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(typeof(ExchangeE2eKeyPair))]
    [InlineData(typeof(ExchangeBroadcastSecrets))]
    [InlineData(typeof(ExchangeEndpointSeal))]
    [InlineData(typeof(ExchangeEndpointDecision))]
    [InlineData(typeof(ExchangeSealedRelayOptions))]
    public void ATypeThatHoldsAKeyDoesNotPrintIt(Type type)
    {
        var secret = new byte[32];
        Array.Fill(secret, (byte)0xAB);
        var encoded = ExchangeBase64Url.Encode(secret);
        object instance = type == typeof(ExchangeE2eKeyPair)
            ? ExchangeE2eKeyPair.Generate()
            : type == typeof(ExchangeBroadcastSecrets) ? new ExchangeBroadcastSecrets(secret, "SHA256:" + encoded, new ExchangeRelayCredentials("u", encoded))
            : type == typeof(ExchangeEndpointSeal) ? new ExchangeEndpointSeal(1, encoded, "SHA256:" + encoded)
            : type == typeof(ExchangeEndpointDecision) ? new ExchangeEndpointDecision(true, ExchangeSealedState.None, true, secret, "SHA256:" + encoded, new ExchangeRelayCredentials("u", encoded))
            : new ExchangeSealedRelayOptions("b1", secret);

        var text = instance.ToString()!;

        Assert.DoesNotContain(encoded, text);
        Assert.DoesNotContain("AB AB", text);
        Assert.DoesNotContain("171", text.Replace("ExchangeE2eKeyPair", ""));
        (instance as IDisposable)?.Dispose();
    }
}
