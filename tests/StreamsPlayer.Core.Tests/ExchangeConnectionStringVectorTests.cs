using System.Security.Cryptography;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeConnectionStringVectorTests
{
    private static string VectorPath => Path.Combine(AppContext.BaseDirectory, "Vectors", "connection-string-v1.json");

    [Fact]
    public void PublishedArtifact_IsByteExact()
    {
        Assert.Equal("4e75c0da11515f4b4e77a46e24751b55f874d699a0362de26cd591df79a8d26d",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(VectorPath))));
    }

    public static IEnumerable<object[]> Cases() => ConnectionStringVectorReplay.Cases(VectorPath);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task PublishedCase_ReplaysFieldsReasonAndCanonicalBytes(string name, string json) =>
        ConnectionStringVectorReplay.ReplayAsync(name, json);
}

/// <summary>
/// FMS_W 224's account-lifecycle corpus, adopted by DEVICE-EXCHANGE 1.1 under <c>vectors/fmsw-224/</c>: the
/// 117 cases of the earlier artifact plus the invitation key <c>i</c> of item AR. A superset, so it is replayed
/// whole - this is where this reader's <c>bad-i</c> and repeated-key answers meet the producer's.
/// </summary>
public sealed class ExchangeConnectionStringAccountCorpusTests
{
    private static string VectorPath => Path.Combine(AppContext.BaseDirectory, "Vectors", "fmsw-224", "connection-string-v1.json");

    [Fact]
    public void AdoptedCorpus_IsByteExact()
    {
        Assert.Equal("45b36244ecb14c196e9c014bb901d9027f0d70e7769455657433db3ff550748f",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(VectorPath))));
    }

    public static IEnumerable<object[]> Cases() => ConnectionStringVectorReplay.Cases(VectorPath);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task AdoptedCase_ReplaysFieldsReasonAndCanonicalBytes(string name, string json) =>
        ConnectionStringVectorReplay.ReplayAsync(name, json);
}
