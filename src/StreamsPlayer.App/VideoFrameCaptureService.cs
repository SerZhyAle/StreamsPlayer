using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using StreamsPlayer.Core;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace StreamsPlayer.App;

public sealed class VideoFrameCaptureService : IAsyncDisposable
{
    private const int Width = 480;  // ~matches the largest grid tile (400px) with DPI headroom; keeps thumbnails small
    private const int Height = 270;
    private const int BytesPerPixel = 4;
    private const int Pitch = Width * BytesPerPixel;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(12);
    // SP-0120: how long a capture waits for the native stop before abandoning the player (see CaptureAsync).
    internal static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(2);
    private readonly LibVLC _libVlc;
    // SP-0166: how many capture players a hung stop may leave behind before capture pauses for the session.
    private readonly AbandonedEngineBudget _abandoned;
    private readonly Action<string, string[]>? _diagnostics;

    public VideoFrameCaptureService(Action<string, string[]>? diagnostics = null, AbandonedEngineBudget? abandoned = null)
    {
        _diagnostics = diagnostics;
        _abandoned = abandoned ?? new AbandonedEngineBudget();
        LibVLCSharp.Shared.Core.Initialize();
        // Software decode for thumbnail grabs so background capture never competes with the player for GPU decode surfaces.
        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet", "--avcodec-hw=none");
    }

    /// <summary>
    /// True once too many capture players were abandoned to stops that never returned (SP-0166). No further
    /// capture engine is created for the rest of the session; callers treat it as "nothing to do", not as a
    /// failed source.
    /// </summary>
    public bool IsPaused => _abandoned.IsPaused;

    public async Task<BitmapSource?> CaptureAsync(string url, CancellationToken cancellationToken)
    {
        if (_abandoned.IsPaused)
        {
            return null;
        }

        // SP-0124: before the player exists. Parsing after it threw out of the worker with the native
        // player already created - leaked, and the grid's preview worker stopped for the session.
        if (!LaunchableAddress.TryParse(url, out var address))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FirstFrameTimeout);
        var pixels = new byte[Pitch * Height];
        var frameClaimed = 0;
        var firstFrame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // SP-0166: everything native - the buffer pin, the player, the media - is acquired inside the try whose
        // finally releases it, so a throw between two acquisitions leaks neither. Until the player exists
        // nothing native holds the buffer, which is what lets the finally free it directly.
        VlcMediaPlayer? mediaPlayer = null;
        Media? media = null;
        var pinnedPixels = default(GCHandle);
        VlcMediaPlayer.LibVLCVideoLockCb? lockCallback = null;
        VlcMediaPlayer.LibVLCVideoDisplayCb? displayCallback = null;
        void OnError(object? sender, EventArgs args) => failed.TrySetResult();
        try
        {
            pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var pinnedAddress = pinnedPixels.AddrOfPinnedObject();
            lockCallback = (_, planes) =>
            {
                Marshal.WriteIntPtr(planes, pinnedAddress);
                return IntPtr.Zero;
            };
            displayCallback = (_, _) =>
            {
                if (Interlocked.Exchange(ref frameClaimed, 1) == 0)
                {
                    firstFrame.TrySetResult([.. pixels]);
                }
            };
            mediaPlayer = new VlcMediaPlayer(_libVlc) { Mute = true, Volume = 0 };
            media = new Media(_libVlc, address);
            media.AddOption(":no-audio");
            media.AddOption(":network-caching=2000");
            media.AddOption(":live-caching=2000");
            mediaPlayer.SetVideoFormat("RV32", Width, Height, Pitch);
            mediaPlayer.SetVideoCallbacks(lockCallback, null, displayCallback);
            mediaPlayer.EncounteredError += OnError;

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
            // SP-0069: deliberately not a `using` on the player. The teardown order below is load-bearing and
            // `using` would run this disposal *after* the finally block - that is, after the pixel buffer has
            // been unpinned and after the two video callbacks have lost their last GC.KeepAlive. LibVLC keeps
            // the callback function pointers registered until the player is released, so that window is a write
            // into memory the GC may have moved and a call through a delegate that may have been collected.
            if (mediaPlayer is null)
            {
                FreeBuffer(pinnedPixels);
                media?.Dispose();
            }
            else
            {
                mediaPlayer.EncounteredError -= OnError;
                await TearDownAsync(mediaPlayer, media, pinnedPixels, lockCallback, displayCallback);
            }
        }
    }

    /// <summary>
    /// Stops a capture player within a bound and releases it, or abandons it to the stop that has not returned.
    /// Never throws: it runs in a finally, and a fault here would replace the capture's own result and end the
    /// preview worker.
    /// </summary>
    private async Task TearDownAsync(
        VlcMediaPlayer mediaPlayer,
        Media? media,
        GCHandle pinnedPixels,
        Delegate? lockCallback,
        Delegate? displayCallback)
    {
        var stop = Task.Run(mediaPlayer.Stop);
        var abandoned = false;
        try
        {
            await stop.WaitAsync(NativeStopTimeout);
        }
        catch (TimeoutException)
        {
            // SP-0120: a stop that hangs on a silent network used to be awaited without a bound, inside the
            // preview coordinator's lock - previews froze for the session and the window's disposal stalled
            // behind it. The player is abandoned instead, and released by whoever the stop finally returns
            // to; until then it keeps the buffer pinned and the callbacks alive, exactly as it must.
            abandoned = true;
            _ = stop.ContinueWith(
                completed =>
                {
                    _ = completed.Exception; // a failed stop still ends in the release below
                    ReleasePlayer(mediaPlayer, pinnedPixels, lockCallback, displayCallback);
                    _abandoned.RecordReleased();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            NoteAbandoned();
        }
        catch (VLCException)
        {
            // Disposal below remains mandatory even if native stop reports failure - and a stop
            // that failed is exactly the case where the decoder may still be running, so the
            // player must be released before the buffer is unpinned rather than after.
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("PREVIEW ENGINE STOP FAULT", FaultLogFields.Of(exception));
        }
        finally
        {
            if (!abandoned)
            {
                ReleasePlayer(mediaPlayer, pinnedPixels, lockCallback, displayCallback);
            }

            media?.Dispose();
        }
    }

    /// <summary>Counts an abandoned player and, on the call that reaches the cap, pauses capture and says why.</summary>
    private void NoteAbandoned()
    {
        var pausedNow = _abandoned.RecordAbandoned();
        _diagnostics?.Invoke("PREVIEW ENGINE ABANDONED", [$"outstanding={_abandoned.Outstanding}", $"cap={AbandonedEngineBudget.DefaultCap}"]);
        if (pausedNow)
        {
            _diagnostics?.Invoke("PREVIEW CAPTURE PAUSED", ["reason=native_stop_hung", $"abandoned={_abandoned.Outstanding}", "until=session_end"]);
        }
    }

    private static void FreeBuffer(GCHandle pinnedPixels)
    {
        if (pinnedPixels.IsAllocated)
        {
            pinnedPixels.Free();
        }
    }

    /// <summary>
    /// Releases a capture player, then the buffer and the callbacks it held. The order is the point: only once
    /// the player that holds pointers to both is gone can the buffer move and the callbacks be collected.
    /// A fault in the native disposal is logged and contained; the buffer is unpinned either way.
    /// </summary>
    private void ReleasePlayer(VlcMediaPlayer player, GCHandle pinnedPixels, Delegate? lockCallback, Delegate? displayCallback)
    {
        try
        {
            player.Dispose();
        }
        catch (Exception exception)
        {
            _diagnostics?.Invoke("PREVIEW ENGINE RELEASE FAULT", FaultLogFields.Of(exception));
        }
        finally
        {
            FreeBuffer(pinnedPixels);
            GC.KeepAlive(lockCallback);
            GC.KeepAlive(displayCallback);
        }
    }

    public ValueTask DisposeAsync()
    {
        _libVlc.Dispose();
        return ValueTask.CompletedTask;
    }
}
