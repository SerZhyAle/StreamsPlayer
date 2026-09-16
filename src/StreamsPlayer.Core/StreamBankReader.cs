using System.IO.Compression;
using System.Text;

namespace StreamsPlayer.Core;

public static class StreamBankReader
{
    // Matches the publisher-side ceiling in the upstream catalog packer (Invoke-PublishCatalog, S0925).
    // A lower value here silently drops a legitimately published atlas: the 2026-07 bank is already
    // 2.9 MB against the previous 4 MB limit, and the atlas grows with the channel count.
    public const int MaximumAtlasBytes = 30 * 1024 * 1024;

    // SP-0098: Uncompressed streams.csv ceiling to protect against archive compression bombs on both
    // network and local import paths.
    public const int MaximumCsvBytes = 32 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static StreamBank Read(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("The stream bank ZIP is empty.");
        }

        // SP-0098: Match entry names strictly by exact equality rather than suffix.
        var csvWasFirst = archive.Entries[0].FullName.Equals("streams.csv", StringComparison.OrdinalIgnoreCase);
        if (!csvWasFirst)
        {
            throw new InvalidDataException("streams.csv must be the first ZIP entry.");
        }

        var csvEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("streams.csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The stream bank does not contain streams.csv.");

        byte[] csvBytes;
        using (var source = csvEntry.Open())
        using (var target = new MemoryStream())
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (target.Length + read > MaximumCsvBytes)
                {
                    throw new InvalidDataException($"streams.csv exceeds the maximum uncompressed limit of {MaximumCsvBytes} bytes.");
                }

                target.Write(buffer, 0, read);
            }

            csvBytes = target.ToArray();
        }

        // SP-0098: Strict UTF-8 decoding; throw on invalid bytes and strip optional UTF-8 BOM if present.
        string csv;
        try
        {
            csv = StrictUtf8.GetString(csvBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("streams.csv contains invalid UTF-8 byte sequences.", exception);
        }

        if (csv.StartsWith('\uFEFF'))
        {
            csv = csv[1..];
        }

        IReadOnlyList<CatalogEntry> entries;
        try
        {
            entries = StreamCatalogCsvParser.Parse(csv);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("streams.csv contains invalid CSV formatting.", exception);
        }

        byte[]? atlas = null;
        var atlasEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("favicon-atlas.png", StringComparison.OrdinalIgnoreCase));
        if (atlasEntry is not null && atlasEntry.Length <= MaximumAtlasBytes)
        {
            using var source = atlasEntry.Open();
            using var target = new MemoryStream((int)Math.Min(atlasEntry.Length, MaximumAtlasBytes));
            var buffer = new byte[81920];
            int read;
            var exceeded = false;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (target.Length + read > MaximumAtlasBytes)
                {
                    exceeded = true;
                    break;
                }

                target.Write(buffer, 0, read);
            }

            if (!exceeded)
            {
                var candidate = target.ToArray();
                // SP-0098: Inspect PNG header and dimensions. An unreadable / corrupt PNG is treated
                // as absent (FaviconAtlas = null) so invalid bytes do not displace a healthy sheet.
                if (PngHeader.TryReadDimensions(candidate, out _, out _))
                {
                    atlas = candidate;
                }
            }
        }

        var maximumFaviconIndex = entries.Select(entry => entry.FaviconIndex).DefaultIfEmpty(null).Max();
        return new StreamBank(entries, atlas, csvWasFirst, maximumFaviconIndex);
    }
}
