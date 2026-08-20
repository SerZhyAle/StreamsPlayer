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
    /// Throws unless <paramref name="payload"/> is byte-for-byte the file this entry describes.
    /// </summary>
    /// <remarks>
    /// <para>SP-0091. This is not a paranoid checksum, it is the only defence against a torn pair. The
    /// publisher replaces an asset by deleting and re-uploading it (source contract item H), so a
    /// rebuild that lands between our coords fetch and our tile-pack fetch gives us two files that each
    /// answer 200 and each are internally valid - and whose index space disagrees. The result is not
    /// missing pictures, it is a still from another station on a channel that looks perfectly healthy:
    /// the failure shape of contract item A, which the user cannot detect and a support report cannot
    /// describe. Refusing the whole import costs one retry; accepting it poisons a disk cache that
    /// nothing later re-checks.</para>
    /// <para>Size is compared first because it is free and it names the likelier accident - a truncated
    /// transfer - in a message that says which file and by how much.</para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The payload is not the declared file.</exception>
    public void Verify(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.LongLength != Size)
        {
            throw new InvalidDataException(
                $"{Name} arrived as {payload.LongLength} bytes; the manifest declares {Size}.");
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{Name} does not match the manifest: sha256 {actual}, expected {Sha256}.");
        }
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
/// SP-0091, source contract item F: the invalidation handle for the published artwork, and the reason
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
/// files of a build together, carries their hashes so a half-replaced pair is refused rather than
/// seeded, and carries a per-set <c>stamp</c> so the client can record which build it installed.</para>
/// </remarks>
public sealed record ArtworkManifest(
    int SchemaVersion,
    DateTimeOffset? GeneratedAt,
    IReadOnlyDictionary<string, ArtworkSet> Sets)
{
    public const string ChannelPreviewSet = "channelPreview";
    public const string StreamLogoSet = "streamLogo";

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
    /// set today - must not be able to break the set we do, and an unrecognised <c>schemaVersion</c> is
    /// not by itself a reason to refuse: rejecting an unknown version would rebuild the pin this class
    /// exists to remove, on a field the publisher will bump for additions. What is never tolerated is a
    /// file entry without a hash, because that is the one thing the manifest is for; such an entry is
    /// dropped here and then named by <see cref="ArtworkSet.File"/> when it is asked for.
    /// </remarks>
    public static ArtworkManifest Parse(string json)
    {
        var empty = new ArtworkManifest(0, null, new Dictionary<string, ArtworkSet>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        using var document = JsonDocument.Parse(json);
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
