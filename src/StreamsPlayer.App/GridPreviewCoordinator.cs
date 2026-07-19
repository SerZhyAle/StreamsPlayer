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
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly Dispatcher _dispatcher;
    private readonly Func<IReadOnlyList<ChannelRow>> _visibleRows;
    private readonly Action<string, ImageSource, bool?> _applyPreview;
    private readonly PreviewFrameCache _memoryCache;
    private readonly PreviewFrameStore _diskStore;
    private readonly VideoFrameCaptureService _captureService;
    private readonly ConcurrentQueue<PreviewRequest> _queue = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visibleUrls = new(StringComparer.Ordinal);
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _session;
    private Task? _worker;
    private Task? _periodic;

    public GridPreviewCoordinator(
        Dispatcher dispatcher,
        Func<IReadOnlyList<ChannelRow>> visibleRows,
        Action<string, ImageSource, bool?> applyPreview,
        PreviewFrameCache memoryCache,
        PreviewFrameStore diskStore,
        VideoFrameCaptureService captureService)
    {
        _dispatcher = dispatcher;
        _visibleRows = visibleRows;
        _applyPreview = applyPreview;
        _memoryCache = memoryCache;
        _diskStore = diskStore;
        _captureService = captureService;
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
            _worker = RunWorkerAsync(_session.Token);
            _periodic = RunPeriodicAsync(_session.Token);
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
        foreach (var row in rows.Where(row => PreviewCapturePolicy.IsCaptureable(row.Channel)))
        {
            activeToken.ThrowIfCancellationRequested();
            if (_memoryCache.TryGet(row.Channel.Url, out var cached) && cached is not null)
            {
                await ApplyAsync(row.Channel.Url, cached, null);
            }
            else
            {
                var restored = await _diskStore.LoadAsync(row.Channel.Url, activeToken);
                if (restored is not null)
                {
                    _memoryCache.SeedRestored(row.Channel.Url, restored);
                    await ApplyAsync(row.Channel.Url, restored, null);
                }
            }

            if (force || !_memoryCache.IsFresh(row.Channel.Url))
            {
                Enqueue(row.Channel.Url, force);
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
            var worker = _worker ?? Task.CompletedTask;
            var periodic = _periodic ?? Task.CompletedTask;
            _session = null;
            _worker = null;
            _periodic = null;
            session.Cancel();
            _signal.Release();
            try
            {
                await Task.WhenAll(worker, periodic);
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

            try
            {
                lock (_pendingGate)
                {
                    if (!_visibleUrls.Contains(request.Url))
                    {
                        continue;
                    }
                }

                if (!request.Force && _memoryCache.IsFresh(request.Url))
                {
                    continue;
                }

                var frame = await _captureService.CaptureAsync(request.Url, cancellationToken);
                if (frame is null)
                {
                    await ApplyReachabilityAsync(request.Url, false);
                    continue;
                }

                _memoryCache.PutLive(request.Url, frame);
                await ApplyAsync(request.Url, frame, true);
                await _diskStore.SaveAsync(request.Url, frame, cancellationToken);
            }
            finally
            {
                lock (_pendingGate)
                {
                    _pending.Remove(request.Url);
                }
            }
        }
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await QueueVisibleAsync(force: false, cancellationToken);
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
