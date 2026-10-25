using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0223 requirement 7: the barcode of a broadcast's link. The text is `FMSBCAST1:` and Base64 of the GZIP of the
/// description (`LIVE-BROADCAST` section 4.1, written by <c>FastMediaSorterBroadcastShare</c>); this class only turns text
/// into modules and modules into a picture. The password is never in it - the person tells it separately (item M).
/// </summary>
internal static class BroadcastQrCode
{
    /// <summary>The ISO/IEC 18004 quiet zone, in modules; a scanner needs it to find the code on a dark window.</summary>
    internal const int QuietModules = 4;

    /// <summary>
    /// The dark modules of <paramref name="text"/> (true is dark), or null when it fits no QR code. Correction level M is
    /// tried first for a camera pointed at a screen; L only when the description is too long for M.
    /// </summary>
    internal static bool[,]? Modules(string text)
    {
        foreach (var level in new[] { ErrorCorrectionLevel.M, ErrorCorrectionLevel.L })
        {
            try
            {
                // No character-set hint: the text is ASCII (a prefix and Base64), and a declared set makes the encoder
                // open the code with an ECI segment that several scanners cannot parse.
                var hints = new Dictionary<EncodeHintType, object>
                {
                    [EncodeHintType.ERROR_CORRECTION] = level,
                    [EncodeHintType.MARGIN] = 0
                };
                var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
                var modules = new bool[matrix.Height, matrix.Width];
                for (var y = 0; y < matrix.Height; y++)
                {
                    for (var x = 0; x < matrix.Width; x++)
                    {
                        modules[y, x] = matrix[x, y];
                    }
                }

                return modules;
            }
            catch (WriterException)
            {
                // Too long for this level; the next one holds more.
            }
        }

        return null;
    }

    /// <summary>Dark modules on a white field with the quiet zone, each module <paramref name="scale"/> pixels square.</summary>
    internal static byte[] Pixels(bool[,] modules, int scale, out int size)
    {
        var count = modules.GetLength(0) + 2 * QuietModules;
        size = count * scale;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < modules.GetLength(0); y++)
        {
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                if (!modules[y, x])
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    var row = ((y + QuietModules) * scale + dy) * size + (x + QuietModules) * scale;
                    Array.Fill(pixels, (byte)0, row, scale);
                }
            }
        }

        return pixels;
    }

    internal static BitmapSource Picture(bool[,] modules, int scale)
    {
        var pixels = Pixels(modules, scale, out var size);
        var picture = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        picture.Freeze();
        return picture;
    }
}
