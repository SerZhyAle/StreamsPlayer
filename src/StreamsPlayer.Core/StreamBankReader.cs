using System.IO.Compression;
using System.Text;

namespace StreamsPlayer.Core;

public static class StreamBankReader
{
    // Matches the publisher-side ceiling in the upstream catalog packer (Invoke-PublishCatalog, S0925).
    // A lower value here silently drops a legitimately published atlas: the 2026-07 bank is already
    // 2.9 MB against the previous 4 MB limit, and the atlas grows with the channel count.
    public const int MaximumAtlasBytes = 30 * 1024 * 1024;

    public static StreamBank Read(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("The stream bank ZIP is empty.");
        }

        var csvWasFirst = EndsWith(archive.Entries[0].FullName, "streams.csv");
        if (!csvWasFirst)
        {
            throw new InvalidDataException("streams.csv must be the first ZIP entry.");
        }

        var csvEntry = archive.Entries.FirstOrDefault(entry => EndsWith(entry.FullName, "streams.csv"))
            ?? throw new InvalidDataException("The stream bank does not contain streams.csv.");

        string csv;
        using (var reader = new StreamReader(csvEntry.Open(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
        {
            csv = reader.ReadToEnd();
        }

        var entries = StreamCatalogCsvParser.Parse(csv);
        byte[]? atlas = null;
        var atlasEntry = archive.Entries.FirstOrDefault(entry => EndsWith(entry.FullName, "favicon-atlas.png"));
        if (atlasEntry is not null && atlasEntry.Length <= MaximumAtlasBytes)
        {
            using var source = atlasEntry.Open();
            using var target = new MemoryStream((int)atlasEntry.Length);
            source.CopyTo(target);
            atlas = target.ToArray();
        }

        var maximumFaviconIndex = entries.Select(entry => entry.FaviconIndex).DefaultIfEmpty(null).Max();
        return new StreamBank(entries, atlas, csvWasFirst, maximumFaviconIndex);
    }

    private static bool EndsWith(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
