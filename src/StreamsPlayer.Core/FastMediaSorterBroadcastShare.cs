using System.IO.Compression;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0217 requirement 3: the encode direction of the hand-off channels of `LIVE-BROADCAST` section 4 -
/// the `FMSBCAST1:` payload (the barcode's text), the import file and the link. The one reader
/// (`FastMediaSorterBroadcastDescriptor`) accepts every form this writer emits, so a round trip through
/// each channel is the same descriptor, byte for byte in meaning, never a second shape.
/// </summary>
public static class FastMediaSorterBroadcastShare
{
    /// <summary>Section 4.2: the import file is the section 3 JSON, uncompressed, at most 64 KiB.</summary>
    public static string ImportFileContent(string descriptorJson) => descriptorJson;

    /// <summary>Section 4.1: `FMSBCAST1:` plus Base64 (standard alphabet) of the GZIP of the descriptor JSON.</summary>
    public static string BarcodeText(string descriptorJson)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var body = Encoding.UTF8.GetBytes(descriptorJson);
            gzip.Write(body);
        }

        return FastMediaSorterBroadcastDescriptor.CompressedPrefix
            + Convert.ToBase64String(compressed.ToArray());
    }

    /// <summary>
    /// Section 4.3's next form, the one this product's own reader opens directly: the URL-encoded
    /// `FMSBCAST1:` descriptor behind the importer's scheme. It carries no secret - a descriptor is
    /// addresses and names only - so it is safe to place on the clipboard.
    /// </summary>
    public static string Link(string descriptorJson) =>
        "fmsbcast://import?payload=" + Uri.EscapeDataString(BarcodeText(descriptorJson));

    /// <summary>Section 4.2: the phone names it `broadcast_&lt;n&gt;.fmsbcast`; this product does the same.</summary>
    public static string SuggestedFileName(int index) =>
        "broadcast_" + index.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + ".fmsbcast";
}
