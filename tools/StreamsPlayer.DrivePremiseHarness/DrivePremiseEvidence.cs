using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.DrivePremiseHarness;

/// <summary>What the <c>write</c> command left behind, so a later <c>list</c> run knows which probe to look for.</summary>
public sealed record ProbeMarker(string BroadcastId, string FileName, string DriveFileId, string Label, string WrittenAtUtc);

/// <summary>
/// The experiment's evidence: every command appends one JSON line to <c>evidence.jsonl</c> under the ticket's
/// <c>temp/SP-0202/</c> directory, and every verdict is stated in the house check shape
/// (<c>expected: ... | actual: ...</c>) so the result can be pasted straight into the report the ticket asks for.
/// </summary>
public static class DrivePremiseEvidence
{
    public const string DefaultDirectory = "temp/SP-0202";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Verdict(string expected, string actual) => $"expected: {expected} | actual: {actual}";

    public static string HashIdentifier(string identifier) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)));

    public static void SaveProbeMarker(string directory, ProbeMarker marker)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"probe-{marker.BroadcastId}.json"), JsonSerializer.Serialize(marker, Json));
    }

    /// <summary>Every probe this working tree's earlier <c>write</c> runs recorded, oldest first.</summary>
    public static IReadOnlyList<ProbeMarker> LoadProbeMarkers(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        var markers = new List<ProbeMarker>();
        foreach (var path in Directory.GetFiles(directory, "probe-*.json").Order())
        {
            try
            {
                if (JsonSerializer.Deserialize<ProbeMarker>(File.ReadAllText(path), Json) is { } marker)
                {
                    markers.Add(marker);
                }
            }
            catch (JsonException)
            {
            }
        }
        return markers;
    }

    public static void Append(string directory, object entry)
    {
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "evidence.jsonl"), JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }
}
