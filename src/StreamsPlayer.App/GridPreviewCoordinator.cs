using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StreamsPlayer.App;

public static class GridPreviewFeature
{
    public const bool CaptureEnabled = true;
}

public sealed class GridPreviewCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan HoverThrottle = TimeSpan.FromSeconds(15);
    // Up to 4 thumbnails capture at once; a fifth waits in the queue until a slot frees (user spec).
    private const int MaxConcurrentCaptures = 4;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IReadOnlyList<ChannelRow>> _visibleRows;
    private readonly Action<string, ImageSource, bool?> _applyPreview;
    private readonly PreviewFrameCache _memoryCache;
    private readonly PreviewFrameStore _diskStore;
    private readonly VideoFrameCaptureService _captureService;
    private readonly Action<string>? _reportCaptureFailure;
    private readonly Func<bool> _autoCaptureEnabled;
    private readonly ConcurrentQueue<PreviewRequest> _queue = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visibleUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastHoverCapture = new(StringComparer.Ordinal);
    // Per-URL cancellation for captures that are already running, so a tile scrolled out of view aborts.
    private readonly Dictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly object _pendingGate = new();
    private readonly object _hoverGate = new();
    private readonly object _inflightGate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _session;
    private Task[]? _workers;

    public GridPreviewCoordinator(
        Dispatcher dispatcher,
        Func<IReadOnlyList<ChannelRow>> visibleRows,
        Action<string, ImageSource, bool?> applyPreview,
        PreviewFrameCache memoryCache,
        PreviewFrameStore diskStore,
        VideoFrameCaptureService captureService,
        Action<string>? reportCaptureFailure = null,
        Func<bool>? autoCaptureEnabled = null)
    {
        _dispatcher = dispatcher;
        _visibleRows = visibleRows;
        _applyPreview = applyPreview;
        _memoryCache = memoryCache;
        _diskStore = diskStore;
        _captureService = captureService;
        _reportCaptureFailure = reportCaptureFailure;
        _autoCaptureEnabled = autoCaptureEnabled ?? (() => true);
    }

    public bool IsRunning => _session is not null;

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!GridPreviewFeature.CaptureEnabled || _session is not null)
            {
                return;
            }

            _session = new CancellationTokenSource();
            _workers = new Task[MaxConcurrentCaptures];
            for (var i = 0; i < MaxConcurrentCaptures; i++)
            {
                _workers[i] = RunWorkerAsync(_session.Token);
            }

            await QueueVisibleAsync(force: false, _session.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task QueueVisibleAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!GridPreviewFeature.CaptureEnabled || _session is not { } session)
        {
            return;
        }

        using var activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
        var activeToken = activeRequest.Token;
        var rows = await _dispatcher.InvokeAsync(_visibleRows);
        lock (_pendingGate)
        {
            _visibleUrls.Clear();
            foreach (var row in rows.Where(row => PreviewCapturePolicy.IsCaptureable(row.Channel)))
            {
                _visibleUrls.Add(row.Channel.Url);
            }
        }

        CancelCapturesOutsideViewport();
        foreach (var row in rows.Where(row => PreviewCapturePolicy.IsCaptureable(row.Channel)))
        {
            activeToken.ThrowIfCancellationRequested();
            var url = row.Channel.Url;
            var hasStored = false;
            if (_memoryCache.TryGet(url, out var cached) && cached is not null)
            {
                await ApplyAsync(url, cached, null);
                hasStored = true;
            }
            else
            {
                var restored = await _diskStore.LoadAsync(url, activeToken);
                if (restored is not null)
                {
                    _memoryCache.Put(url, restored);
                    await ApplyAsync(url, restored, null);
                    hasStored = true;
                }
            }

            // Stored previews always show; auto-capture of a first-time blank only when the setting is on. Explicit refresh always captures.
            if (force || (!hasStored && _autoCaptureEnabled()))
            {
                Enqueue(url, force);
            }
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is null)
            {
                return;
            }

            var session = _session;
            var workers = _workers ?? [];
            _session = null;
            _workers = null;
            session.Cancel(); // wakes every worker: WaitAsync(token) and in-flight CaptureAsync both throw
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal grid-exit path.
            }
            finally
            {
                session.Dispose();
                while (_queue.TryDequeue(out _))
                {
                }

                lock (_pendingGate)
                {
                    _pending.Clear();
                    _visibleUrls.Clear();
                }

                lock (_inflightGate)
                {
                    _inflight.Clear();
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _captureService.DisposeAsync();
        _signal.Dispose();
        _lifecycle.Dispose();
    }

    // On-demand refresh of a single tile after a hover dwell, rate-limited per channel.
    public void RequestHoverCapture(string url)
    {
        if (!GridPreviewFeature.CaptureEnabled || _session is null || !_autoCaptureEnabled())
        {
            return;
        }

        lock (_hoverGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastHoverCapture.TryGetValue(url, out var last) && now - last < HoverThrottle)
            {
                return;
            }

            _lastHoverCapture[url] = now;
        }

        Enqueue(url, force: true);
    }

    // Adopt a frame captured elsewhere (e.g. the first frame from the player) as this channel's thumbnail.
    public void IngestFrame(string url, BitmapSource frame)
    {
        _memoryCache.Put(url, frame);
        _ = ApplyAsync(url, frame, true);
        _ = SaveIngestedAsync(url, frame);
    }

    private async Task SaveIngestedAsync(string url, BitmapSource frame)
    {
        try
        {
            await _diskStore.SaveAsync(url, frame, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _reportCaptureFailure?.Invoke($"ingest_save_error={ex.GetType().Name}:{url}");
        }
    }

    private void Enqueue(string url, bool force)
    {
        lock (_pendingGate)
        {
            if (!_pending.Add(url))
            {
                return;
            }
        }

        _queue.Enqueue(new PreviewRequest(url, force));
        _signal.Release();
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken);
            if (!_queue.TryDequeue(out var request))
            {
                continue;
            }

            CancellationTokenSource? captureCts = null;
            try
            {
                lock (_pendingGate)
                {
                    if (!_visibleUrls.Contains(request.Url))
                    {
                        continue;
                    }
                }

                if (!request.Force && _memoryCache.TryGet(request.Url, out var existing) && existing is not null)
                {
                    continue;
                }

                // Register a per-URL token so a scroll that hides this tile can abort its in-flight capture.
                captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_inflightGate)
                {
                    _inflight[request.Url] = captureCts;
                }

                var frame = await _captureService.CaptureAsync(request.Url, captureCts.Token);
                if (frame is null)
                {
                    _reportCaptureFailure?.Invoke(request.Url);
                    await ApplyReachabilityAsync(request.Url, false);
                    continue;
                }

                _memoryCache.Put(request.Url, frame);
                await ApplyAsync(request.Url, frame, true);
                // Persist against the session token: a frame that just left the viewport is still worth saving.
                await _diskStore.SaveAsync(request.Url, frame, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The tile scrolled out of view mid-capture; drop it and keep serving the queue.
            }
            finally
            {
                if (captureCts is not null)
                {
                    lock (_inflightGate)
                    {
                        // Only clear our own entry: a re-enqueue may already have installed a newer CTS.
                        if (_inflight.TryGetValue(request.Url, out var current) && ReferenceEquals(current, captureCts))
                        {
                            _inflight.Remove(request.Url);
                        }
                    }

                    captureCts.Dispose();
                }

                lock (_pendingGate)
                {
                    _pending.Remove(request.Url);
                }
            }
        }
    }

    // Abort captures already running for tiles no longer in the viewport (called after _visibleUrls is rebuilt).
    private void CancelCapturesOutsideViewport()
    {
        HashSet<string> visible;
        lock (_pendingGate)
        {
            visible = new HashSet<string>(_visibleUrls, StringComparer.Ordinal);
        }

        List<CancellationTokenSource>? stale = null;
        lock (_inflightGate)
        {
            foreach (var (url, cts) in _inflight)
            {
                if (!visible.Contains(url))
                {
                    (stale ??= new List<CancellationTokenSource>()).Add(cts);
                }
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var cts in stale)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The capture completed and disposed its token between the snapshot and here; nothing to abort.
            }
        }
    }

    private async Task ApplyAsync(string url, ImageSource image, bool? reachable) =>
        await _dispatcher.InvokeAsync(() => _applyPreview(url, image, reachable));

    private async Task ApplyReachabilityAsync(string url, bool reachable) =>
        await _dispatcher.InvokeAsync(() =>
        {
            if (_memoryCache.TryGet(url, out var cached) && cached is not null)
            {
                _applyPreview(url, cached, reachable);
            }
        });

    private sealed record PreviewRequest(string Url, bool Force);
}
