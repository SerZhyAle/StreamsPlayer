using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// Replays the catalog's protection transcript (`vectors/protection-v1.json`, DEVICE-EXCHANGE 0.19 section 17
/// items AG to AL) against the one thing this product reads of a protected resource - the directory frame.
/// The boundary it holds: a `sftp-share` record with `protection`, with or without `access`, is imported as
/// nothing, is never an error and never disturbs the rest of the directory (section 9 rule 10); this product
/// declares no `pin-v1` capability, so no conforming server hands it `access` in the first place (17 item AG).
/// </summary>
public sealed class ExchangeProtectionVectorTests
{
    private static string VectorPath => Path.Combine(AppContext.BaseDirectory, "Vectors", "protection-v1.json");

    private const string PublishedSHA256 = "2f6b4ddfc97edfd53db7d2b5406ccaa0510ff8a229dd1866c72c51751e01cd75";

    [Fact]
    public void PublishedArtifact_IsByteExact()
    {
        Assert.Equal(PublishedSHA256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(VectorPath))));
    }

    public static IEnumerable<object[]> DirectoryFrames()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(VectorPath));
        foreach (var transcript in document.RootElement.GetProperty("transcripts").EnumerateArray())
        {
            foreach (var step in transcript.GetProperty("steps").EnumerateArray())
            {
                if (ExchangeProtocol.String(step, "direction") != "server->client")
                {
                    continue;
                }

                using var frame = JsonDocument.Parse(ExchangeProtocol.String(step, "json")!);
                if (frame.RootElement.TryGetProperty("type", out var type) && type.GetString() == "directory")
                {
                    var name = $"{ExchangeProtocol.String(step, "stream")}-revision-{frame.RootElement.GetProperty("revision").GetInt32()}";
                    yield return [name, step.GetProperty("hex").GetString()!, ExchangeProtocol.String(step, "json")!];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(DirectoryFrames))]
    public async Task DirectoryFrame_IsByteExact_AndImportsTheProtectedResourceAsNothing(
        string _, string hex, string json)
    {
        var frameBytes = Convert.FromHexString(hex);
        var bodyBytes = frameBytes[4..];
        Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(frameBytes), (uint)bodyBytes.Length);
        Assert.Equal(json, Encoding.UTF8.GetString(bodyBytes));

        using var body = JsonDocument.Parse(bodyBytes);
        var revision = body.RootElement.GetProperty("revision").GetInt32();

        await using var stream = new MemoryStream(frameBytes, writable: false);
        using var document = await ExchangeProtocol.ReadAsync(stream, authenticated: true, CancellationToken.None);
        var state = new ExchangeDirectoryState();
        state.ApplyFull(document.RootElement);
        var snapshot = state.Snapshot;
        Assert.Equal(revision, snapshot.Revision);
        Assert.Empty(snapshot.Groups);
        Assert.Empty(snapshot.LiveSourceIds);
        Assert.Empty(snapshot.LiveBroadcastIds);
    }
}
