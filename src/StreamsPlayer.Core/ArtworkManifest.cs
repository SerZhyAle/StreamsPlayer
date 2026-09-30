using System.Security.Cryptography;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// One published artwork file as <c>artwork-manifest.json</c> declares it: the stable name, the byte
/// size and the SHA-256 of the exact bytes that name resolved to when the manifest was written.
/// </summary>
public sealed record ArtworkFile(string Name, long Size, string Sha256)
{
    /// <summary>
    /// Compares <paramref name="payload"/> against the declared size and hash; null when it matches,
    /// otherwise the one-line description of the mismatch for the log.
    /// </summary>
    /// <remarks>
    /// <para>STREAM-BANK item L: the manifest's per-file hashes are diagnostic, not a gate - a consumer
    /// must not refuse an otherwise valid pack on a mismatch it cannot act on. What gates the import is
    /// the structural check (the archive opens, every slot name is a decimal, the per-tile ceiling);
    /// this report is what the log keeps. The truncated transfer and the half-replaced publish it names
    /// are real accidents, and a support report can quote the line even though the import proceeds.</para>
    /// <para>Size is compared first because it is free and it names the likelier accident - a truncated
    /// transfer - in a message that says which file and by how much.</para>
    /// </remarks>
    public string? Diagnose(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.LongLength != Size)
        {
            return $"{Name} arrived as {payload.LongLength} bytes; the manifest declares {Size}.";
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"{Name} does not match the manifest: sha256 {actual}, expected {Sha256}.";
        }

        return null;
    }
}

/// <summary>
/// One artwork set - the tile pack and the sidecar that were built together - plus the stamp that
/// identifies that build.
/// </summary>
public sealed record ArtworkSet(string Stamp, IReadOnlyList<ArtworkFile> Files)
{
    /// <summary>The declared file with this stable name.</summary>
    /// <exception cref="InvalidDataException">The set does not list it.</exception>
    public ArtworkFile File(string name) =>
        Files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"The artwork manifest lists no file named {name} (it has: {string.Join(", ", Files.Select(file => file.Name))}).");
}

/// <summary>
/// SP-0091, STREAM-BANK item F: the invalidation handle for the published artwork, and the reason
/// the stable names can be read at all.
/// </summary>
/// <remarks>
/// <para>Revisioned artwork names (<c>channel-preview-atlas-vN.webp</c> and friends) are frozen
/// artifacts: never deleted, and never rebuilt again. Which revision is current is a fact about today,
/// not a contract - so a revision compiled into this client does not hold a compatible payload, it
/// holds a payload that stopped being maintained, and it looks healthy forever because the asset it
/// names keeps answering 200 with the last bytes it ever had. The pin cannot lift itself, and no
/// measurement from inside the client can tell a frozen asset from a current one.</para>
/// <para>The stable names have the opposite property - they always resolve to the current build - and
/// the cost of that is that they change under you. This manifest is what makes that safe: it names the
/// files of a build together and carries a per-set <c>stamp</c> so the client can record which build it
/// installed. Item L rules the per-file hashes diagnostic: a mismatch is reported for the log, never
/// refused, and the stamp is the only invalidation key.</para>
/// </remarks>
public sealed record ArtworkManifest(
    int SchemaVersion,
    DateTimeOffset? GeneratedAt,
    IReadOnlyDictionary<string, ArtworkSet> Sets)
{
    public const string ChannelPreviewSet = "channelPreview";
    public const string StreamLogoSet = "streamLogo";

    /// <summary>The highest manifest <c>schemaVersion</c> this client understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// SP-0106, STREAM-BANK item L: refuses a manifest written by a newer schema before anything it
    /// describes is fetched.
    /// </summary>
    /// <remarks>
    /// Additions to the manifest arrive as new <c>sets</c>, which a consumer ignores, so a raised
    /// <c>schemaVersion</c> means what a raised MAJOR always means: a shape this reader cannot absorb.
    /// Reading it as the current shape would be the partial import the compatibility law forbids - a
    /// version-2 <c>channelPreview</c> set that still parses would seed pictures by indices whose meaning
    /// may have changed. A missing or lower value is read as the current shape.
    /// </remarks>
    /// <exception cref="UnsupportedArtworkManifestException">The manifest is newer than this client.</exception>
    public void EnsureSupported()
    {
        if (SchemaVersion > SupportedSchemaVersion)
        {
            throw new UnsupportedArtworkManifestException(SchemaVersion);
        }
    }

    /// <summary>The named set.</summary>
    /// <exception cref="InvalidDataException">The manifest does not carry it.</exception>
    public ArtworkSet Set(string name) =>
        Sets.TryGetValue(name, out var set)
            ? set
            : throw new InvalidDataException(
                $"The artwork manifest (schemaVersion {SchemaVersion}) carries no set named {name} " +
                $"(it has: {string.Join(", ", Sets.Keys)}).");

    /// <summary>
    /// Parses the manifest. Malformed JSON throws <see cref="JsonException"/> for the caller to treat as
    /// "not published yet".
    /// </summary>
    /// <remarks>
    /// Tolerant per set and per file, strict at the point of use. An entry we do not consume - the logo
    /// set today - must not be able to break the set we do. Parsing never judges <c>schemaVersion</c>;
    /// <see cref="EnsureSupported"/> does, so a newer manifest is refused as its own outcome rather than
    /// as a malformed one. What is never tolerated is a file entry without a hash, because that is the
    /// one thing the manifest is for; such an entry is dropped here and then named by
    /// <see cref="ArtworkSet.File"/> when it is asked for.
    /// </remarks>
    public static ArtworkManifest Parse(string json)
    {
        var empty = new ArtworkManifest(0, null, new Dictionary<string, ArtworkSet>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        // SP-0126: a publisher that writes a UTF-8 byte-order mark must not make the manifest unreadable.
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return empty;
        }

        var schemaVersion = root.TryGetProperty("schemaVersion", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : 0;

        DateTimeOffset? generatedAt = root.TryGetProperty("generatedAt", out var stamp) &&
            stamp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                stamp.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedStamp)
            ? parsedStamp
            : null;

        var sets = new Dictionary<string, ArtworkSet>(StringComparer.Ordinal);
        if (root.TryGetProperty("sets", out var setsElement) && setsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in setsElement.EnumerateObject())
            {
                if (TryReadSet(property.Value, out var set))
                {
                    sets[property.Name] = set;
                }
            }
        }

        return new ArtworkManifest(schemaVersion, generatedAt, sets);
    }

    private static bool TryReadSet(JsonElement element, out ArtworkSet set)
    {
        set = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("stamp", out var stamp) || stamp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = stamp.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var files = new List<ArtworkFile>();
        if (element.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (TryReadFile(file, out var parsed))
                {
                    files.Add(parsed);
                }
            }
        }

        set = new ArtworkSet(value, files);
        return true;
    }

    private static bool TryReadFile(JsonElement element, out ArtworkFile file)
    {
        file = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!element.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var size = element.TryGetProperty("size", out var declared) &&
            declared.ValueKind == JsonValueKind.Number && declared.TryGetInt64(out var parsedSize)
            ? parsedSize
            : -1;

        var nameValue = name.GetString();
        var hashValue = hash.GetString();
        if (string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(hashValue) || size < 0)
        {
            return false;
        }

        file = new ArtworkFile(nameValue, size, hashValue);
        return true;
    }
}

/// <summary>
/// SP-0106: the published artwork manifest was written by a newer schema than this client reads.
/// </summary>
/// <remarks>
/// The contract's nothing-case, not a failure: the installed artwork stays and no stamp is recorded.
/// Deliberately not an <see cref="InvalidDataException"/>, so no caller mistakes it for a broken publish
/// and nothing retries it - the only recovery is a newer application.
/// </remarks>
public sealed class UnsupportedArtworkManifestException(int schemaVersion)
    : Exception(
        $"The artwork manifest has schemaVersion {schemaVersion}; this client reads up to " +
        $"{ArtworkManifest.SupportedSchemaVersion}.")
{
    public int SchemaVersion { get; } = schemaVersion;
}

/// <summary>
/// SP-0160, STREAM-BANK item L: the manifest was absent (404) or unparseable, which the contract makes
/// the nothing-case - "nothing new", never an error, never a prompt, never a reason to discard the
/// installed artwork.
/// </summary>
/// <remarks>
/// <para>The original failure travels as the inner exception, and it must: <see cref="PublishWindowRetry"/>
/// classifies a publish-window 404 by walking the chain, so a manifest caught mid-publish is still
/// retried (rule 11) and this exception only escapes once the schedule is spent. Deliberately not an
/// <see cref="InvalidDataException"/>, so no caller mistakes it for a broken publish - the caller's
/// answer is a benign status, and the only recovery is the next explicit refresh.</para>
/// </remarks>
public sealed class ArtworkManifestNothingNewException(string reason, Exception innerException)
    : Exception(
        $"The artwork manifest was {reason}; STREAM-BANK item L makes that the nothing-case, not a failure.",
        innerException)
{
    /// <summary>Short cause for a log line: "not found" or "unparseable".</summary>
    public string Reason { get; } = reason;
}
