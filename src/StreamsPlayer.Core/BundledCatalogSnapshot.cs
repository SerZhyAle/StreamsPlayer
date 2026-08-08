using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0052: the copy of the stream bank that travels inside the application binary, for the three cases
/// where the published bank is out of reach - a first launch with no network, a failed update, and the
/// settings action.
/// <para>
/// It is an embedded resource rather than a file beside the executable because the owner's daily build
/// is a self-contained single-file publish, which a loose file does not survive. Reading it is always
/// user-initiated; nothing here touches the network, and the type has no way to.
/// </para>
/// </summary>
public static class BundledCatalogSnapshot
{
    /// <summary>
    /// The ticket's declared size ceiling for the embedded payload. Expressed here so the generator
    /// script and the release pre-flight both cite one value instead of restating a number from prose.
    /// This is a build-time contract: a payload that got past the generator is still read, because
    /// refusing it at runtime would punish the user for a mistake made at release time.
    /// </summary>
    public const int MaximumSnapshotBytes = 8 * 1024 * 1024;

    internal const string ResourceName = "catalog-snapshot.zip";
    private const string MetadataEntryName = "snapshot.json";

    private static readonly Assembly OwningAssembly = typeof(BundledCatalogSnapshot).Assembly;

    /// <summary>
    /// Whether this build carries a snapshot at all. False in a source tree whose artifact has not been
    /// generated, and in any build where it was deliberately left out - the interface offers nothing
    /// rather than failing when the user asks.
    /// </summary>
    public static bool Exists => OwningAssembly.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>
    /// Reads the embedded snapshot. Every failure - absent, truncated, mispackaged, or missing its
    /// provenance - surfaces as <see cref="InvalidDataException"/> so a single catch covers them and
    /// nothing has been written by the time it is thrown.
    /// </summary>
    public static CatalogSnapshot Read()
    {
        using var resource = OwningAssembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("This build does not carry a bundled catalog snapshot.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    internal static CatalogSnapshot Read(byte[] payload)
    {
        StreamBank bank;
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            bank = StreamBankReader.Read(stream);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"The bundled catalog snapshot is unreadable: {exception.Message}", exception);
        }

        if (bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The bundled catalog snapshot contains no valid channels.");
        }

        // A second pass over the archive: StreamBankReader knows only the two entries the published bank
        // contract defines, and the snapshot's provenance is deliberately not part of that contract.
        using var metadataSource = new MemoryStream(payload, writable: false);
        using var archive = new ZipArchive(metadataSource, ZipArchiveMode.Read, leaveOpen: true);
        var metadataEntry = archive.GetEntry(MetadataEntryName)
            ?? throw new InvalidDataException($"The bundled catalog snapshot does not contain {MetadataEntryName}.");

        SnapshotMetadata? metadata;
        try
        {
            using var metadataStream = metadataEntry.Open();
            metadata = JsonSerializer.Deserialize<SnapshotMetadata>(metadataStream, MetadataOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The bundled catalog snapshot's {MetadataEntryName} is unreadable.", exception);
        }

        if (metadata is null || metadata.SourceDate is not DateTimeOffset sourceDate)
        {
            // Without a date the interface would have to present the list without saying how old it is,
            // which is the one thing the feature must never do.
            throw new InvalidDataException($"The bundled catalog snapshot's {MetadataEntryName} carries no source date.");
        }

        return new CatalogSnapshot(bank, sourceDate, metadata.SourceUrl ?? StreamCatalogService.CatalogUrl);
    }

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web);

    private sealed record SnapshotMetadata(DateTimeOffset? SourceDate, string? SourceUrl);
}
