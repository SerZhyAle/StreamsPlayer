using System.Runtime.InteropServices;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace StreamsPlayer.App;

internal sealed record ExchangeCameraInfo(string Id, string Name);

/// <summary>
/// SP-0229 requirement 1: "is there a camera" asked of the same API that opens one, so the control and the capture
/// cannot disagree. An infrared-only source (a face-sign-in sensor) is not a camera for reading a QR.
/// Enumerating lights no camera and asks Windows for no permission.
/// </summary>
internal static class ExchangeCameraCatalog
{
    internal static async Task<IReadOnlyList<ExchangeCameraInfo>> FindAsync()
    {
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            return groups.Where(group => group.SourceInfos.Any(IsColorVideo))
                .Select(group => new ExchangeCameraInfo(group.Id, group.DisplayName))
                .ToList();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException
            or UnauthorizedAccessException or DllNotFoundException)
        {
            // No capture stack (a Windows edition without the media pack) is "no camera": the control stays absent.
            return [];
        }
    }

    internal static bool IsColorVideo(MediaFrameSourceInfo info) =>
        info.SourceKind == MediaFrameSourceKind.Color
        && info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;
}
