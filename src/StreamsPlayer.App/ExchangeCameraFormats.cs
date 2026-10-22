using System.Runtime.InteropServices;

namespace StreamsPlayer.App;

/// <summary>One mode a camera offers, as the choice sees it; the WinRT format object stays in the session.</summary>
internal readonly record struct ExchangeCameraFormat(string Subtype, int Width, int Height, double FramesPerSecond);

/// <summary>
/// SP-0229: which of a camera's modes to read a QR from. A connection string is a dense code, so a low resolution
/// cannot resolve it and a very high one only costs decoding time; an uncompressed mode saves the camera's
/// decompression. The ranking is a pure function of the mode list so it is proved without a camera.
/// </summary>
internal static class ExchangeCameraFormats
{
    internal const int PreferredWidth = 1280;
    internal const int MinimumWidth = 640;
    internal const int WidthCeiling = 1920;

    private static readonly string[] Uncompressed = ["NV12", "YUY2", "ARGB32", "RGB32", "BGRA8"];

    /// <summary>The index of the mode to ask the camera for, or -1 when the list has no usable mode.</summary>
    internal static int Choose(IReadOnlyList<ExchangeCameraFormat> formats)
    {
        var best = -1;
        for (var index = 0; index < formats.Count; index++)
        {
            if (formats[index].Width < 1 || formats[index].Height < 1)
            {
                continue;
            }

            if (best < 0 || Rank(formats[index]).CompareTo(Rank(formats[best])) < 0)
            {
                best = index;
            }
        }

        return best;
    }

    // Lower is better, compared left to right: wide enough, not past the ceiling (a 4K picture only costs
    // decoding time), uncompressed, nearest the preferred width, then the faster mode. When nothing is wide
    // enough the nearest to the preferred width is still the widest available.
    private static (int TooNarrow, int PastCeiling, int Compressed, int Distance, double NegativeRate) Rank(ExchangeCameraFormat format) => (
        format.Width < MinimumWidth ? 1 : 0,
        format.Width > WidthCeiling ? 1 : 0,
        Uncompressed.Contains(format.Subtype, StringComparer.OrdinalIgnoreCase) ? 0 : 1,
        Math.Abs(format.Width - PreferredWidth),
        -format.FramesPerSecond);
}

internal enum ExchangeCameraFailure
{
    /// <summary>Windows privacy settings, or a policy, do not let this program use the camera.</summary>
    AccessDenied,

    /// <summary>The camera could not be opened; another program holding it is the usual reason.</summary>
    CouldNotOpen
}

internal static class ExchangeCameraFailures
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    internal static ExchangeCameraFailure Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => ExchangeCameraFailure.AccessDenied,
        COMException { HResult: AccessDeniedHResult } => ExchangeCameraFailure.AccessDenied,
        _ => ExchangeCameraFailure.CouldNotOpen
    };

    internal static string MessageKey(ExchangeCameraFailure failure) => failure switch
    {
        ExchangeCameraFailure.AccessDenied => "ExchangeCameraDenied",
        _ => "ExchangeCameraBusy"
    };
}
