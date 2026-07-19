using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace StreamPlayer.App;

public sealed class VideoFrameCaptureService : IAsyncDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int BytesPerPixel = 4;
    private const int Pitch = Width * BytesPerPixel;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(12);
    private readonly LibVLC _libVlc;

    public VideoFrameCaptureService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet");
    }

    public async Task<BitmapSource?> CaptureAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FirstFrameTimeout);
        using var mediaPlayer = new VlcMediaPlayer(_libVlc) { Mute = true, Volume = 0 };
        using var media = new Media(_libVlc, new Uri(url));
        media.AddOption(":no-audio");
        media.AddOption(":network-caching=2000");
        media.AddOption(":live-caching=2000");

        var pixels = new byte[Pitch * Height];
        var pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var frameClaimed = 0;
        var firstFrame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VlcMediaPlayer.LibVLCVideoLockCb lockCallback = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, pinnedPixels.AddrOfPinnedObject());
            return IntPtr.Zero;
        };
        VlcMediaPlayer.LibVLCVideoDisplayCb displayCallback = (_, _) =>
        {
            if (Interlocked.Exchange(ref frameClaimed, 1) == 0)
            {
                firstFrame.TrySetResult([.. pixels]);
            }
        };
        void OnError(object? sender, EventArgs args) => failed.TrySetResult();

        mediaPlayer.SetVideoFormat("RV32", Width, Height, Pitch);
        mediaPlayer.SetVideoCallbacks(lockCallback, null, displayCallback);
        mediaPlayer.EncounteredError += OnError;
        try
        {
            if (!mediaPlayer.Play(media))
            {
                return null;
            }

            var completed = await Task.WhenAny(firstFrame.Task, failed.Task).WaitAsync(timeout.Token);
            if (completed == failed.Task)
            {
                return null;
            }

            var capturedPixels = await firstFrame.Task;
            var frame = BitmapSource.Create(
                Width,
                Height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                capturedPixels,
                Pitch);
            frame.Freeze();
            return frame;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (VLCException)
        {
            return null;
        }
        finally
        {
            mediaPlayer.EncounteredError -= OnError;
            try
            {
                await Task.Run(mediaPlayer.Stop);
            }
            catch (VLCException)
            {
                // Disposal below remains mandatory even if native stop reports failure.
            }
            finally
            {
                if (pinnedPixels.IsAllocated)
                {
                    pinnedPixels.Free();
                }
                GC.KeepAlive(lockCallback);
                GC.KeepAlive(displayCallback);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _libVlc.Dispose();
        return ValueTask.CompletedTask;
    }
}
