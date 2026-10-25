using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;
using ZXing;
using ZXing.Common;

namespace StreamsPlayer.App;

/// <summary>A picture as the decoder wants it: BGRA, 4 bytes a pixel, row after row.</summary>
internal sealed record ExchangePixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// SP-0221 requirement 2: reads a connection QR out of a picture file, a screenshot or a clipboard image. The
/// decoder is ZXing.Net, referenced by the App only; this class keeps it behind <see cref="ExchangeQrOutcome"/>
/// so nothing else sees its types. A hostile picture costs bounded memory and time and never an exception:
/// whatever the decoder or the image codec throws is "not readable", and neither the path nor a payload is in
/// the message.
/// </summary>
internal static class ExchangeQrReader
{
    /// <summary>A screenshot of a 5K display is 14.7 MP; anything past this is not a screenshot of a QR.</summary>
    internal const int MaximumPixels = 24_000_000;
    internal const long MaximumFileBytes = 32L * 1024 * 1024;

    internal static ExchangeQrOutcome Decode(ExchangePixels pixels)
    {
        if (pixels.Width < 1 || pixels.Height < 1 || (long)pixels.Width * pixels.Height > MaximumPixels
            || pixels.Bgra.Length < pixels.Width * pixels.Height * 4)
        {
            return new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable);
        }

        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = [BarcodeFormat.QR_CODE],
                    TryHarder = true
                }
            };
            var source = new RGBLuminanceSource(pixels.Bgra, pixels.Width, pixels.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            // A light-on-dark code (a screenshot of a dark theme) only reads from the inverted picture, and
            // the multi-code path does not invert on its own, so the second pass is made here.
            var results = reader.DecodeMultiple(source);
            if (results is not { Length: > 0 })
            {
                results = reader.DecodeMultiple(source.invert());
            }

            // The same code seen twice (a rotated retry) is one code.
            var payloads = (results ?? []).Select(result => result.Text).Where(text => text is not null)
                .Distinct(StringComparer.Ordinal).ToList();
            return ExchangeQrImport.Interpret(payloads);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The decoder's own failure on a damaged picture; its message is private input.
            return new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable);
        }
    }

    /// <summary>Takes a bitmap that is alive on the calling thread; the clipboard's image is read here, then decoded elsewhere.</summary>
    internal static ExchangePixels? ToPixels(BitmapSource image)
    {
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
        {
            return null;
        }

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var bytes = new byte[width * height * 4];
        converted.CopyPixels(bytes, width * 4, 0);
        // A transparent background reads as black once the alpha is dropped, which would turn a QR on a
        // transparent PNG into one solid block: lay every pixel over white first.
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            var alpha = bytes[offset + 3];
            if (alpha == 255)
            {
                continue;
            }

            for (var channel = 0; channel < 3; channel++)
            {
                // CopyPixels of Bgra32 is straight (non-premultiplied) alpha.
                bytes[offset + channel] = (byte)((bytes[offset + channel] * alpha + 255 * (255 - alpha)) / 255);
            }

            bytes[offset + 3] = 255;
        }

        return new ExchangePixels(bytes, width, height);
    }

    internal static Task<ExchangeQrOutcome> ReadAsync(ExchangePixels pixels, CancellationToken cancellationToken = default) =>
        Task.Run(() => Decode(pixels), cancellationToken);

    internal static Task<ExchangeQrOutcome> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaximumFileBytes)
                {
                    return new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable);
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // S7-4: BitmapCacheOption.OnLoad decoded the whole frame before ToPixels could apply MaximumPixels, so a small
                // high-ratio PNG could expand to gigabytes first. None reads only the header here; ToPixels checks
                // PixelWidth x PixelHeight against the cap and the pixels are decoded (from this still-open stream) only within it.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0)
                {
                    return new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable);
                }

                var pixels = ToPixels(decoder.Frames[0]);
                return pixels is null ? new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable) : Decode(pixels);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException
                or FileFormatException)
            {
                return new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable);
            }
        }, cancellationToken);
}
