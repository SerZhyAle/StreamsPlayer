using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public static class GridPreviewFeature
{
    // Kill switch for the whole grid-preview subsystem. Deliberately not a const: as a const the compiler
    // folds every guard into unreachable code, which both warns and stops the guards being real checks.
    public static readonly bool CaptureEnabled = true;
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
    // Lifecycle/visibility diagnostics. Whether a tile shows a preview or falls back to its favicon
    // depends on coordinator state that is invisible from the UI, so it has to be greppable in the log.
    private readonly Action<string, string[]>? _diagnostics;
    private readonly Func<bool> _autoCaptureEnabled;
    private readonly ConcurrentQueue<PreviewRequest> _queue = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visibleUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastHoverCapture = new(StringComparer.Ordinal);
    // A source that just failed to yield a frame is not asked again on the next scroll; see
    // PreviewCaptureCooldown for the evidence and for why only the automatic path is held back.
    private readonly PreviewCaptureCooldown _failureCooldown = new();
    private readonly object _failureGate = new();
    // Per-URL cancellation for captures that are already running, so a tile scrolled out of view aborts.
    private readonly Dictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly object _pendingGate = new();
    private readonly object _hoverGate = new();
    private readonly object _inflightGate = new();
    // SP-0120: how long stopping waits for the capture workers. Each one is bounded already - a capture's
    // native stop is abandoned after VideoFrameCaptureService.NativeStopTimeout - so this is the margin that
    // keeps any other hang from holding _lifecycle, which the next start and the window's disposal both need.
    private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(3);
    // Deliberately never disposed (SP-0120). A late caller - MainWindow_Deactivated, the Closed callback of a
    // PlayerWindow - can already be queued on _lifecycle when the window disposes this coordinator; disposing
    // the semaphore left that caller waiting for ever, and a Release on it threw. Neither holds a kernel handle
    // unless AvailableWaitHandle is read, which nothing here does.
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _session;
    private Task[]? _workers;
    // SP-0065: set once, under _lifecycle, by DisposeAsync. Every entry point reads it before waiting and again
    // after it has the lock (SP-0120): a caller that passed the first read can be granted the lock after the
    // disposal released it, and starting a session then would leave capture workers running against a disposed
    // capture engine. An ObjectDisposedException inside those async void handlers takes the process down.
    private bool _disposed;
    // SP-0166: 1 once a worker's fault was logged for the current session, so four workers that fail together
    // leave one line. Reset by StartAsync.
    private int _workerFaultReported;

    public GridPreviewCoordinator(
        Dispatcher dispatcher,
        Func<IReadOnlyList<ChannelRow>> visibleRows,
        Action<string, ImageSource, bool?> applyPreview,
        PreviewFrameCache memoryCache,
        PreviewFrameStore diskStore,
        VideoFrameCaptureService captureService,
        Action<string>? reportCaptureFailure = null,
        Func<bool>? autoCaptureEnabled = null,
        Action<string, string[]>? diagnostics = null)
    {
        _diagnostics = diagnostics;
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
        if (_disposed)
        {
            return;
        }

        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || !GridPreviewFeature.CaptureEnabled || _session is not null)
            {
                return;
            }

            _session = new CancellationTokenSource();
            _workers = new Task[MaxConcurrentCaptures];
            Volatile.Write(ref _workerFaultReported, 0);
            for (var i = 0; i < MaxConcurrentCaptures; i++)
            {
                _workers[i] = RunWorkerAsync(_session);
            }

            _diagnostics?.Invoke("PREVIEW COORD", ["state=started"]);
            await QueueVisibleAsync(force: false, _session.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Restores stored previews for the visible rows and, when a capture session is running, queues the
    /// tiles that still need one.
    /// </summary>
    /// <remarks>
    /// Showing a frame that is already on disk deliberately does NOT require a running session. Capture is
    /// suspended whenever the window is inactive or something is playing, and gating the disk restore on
    /// the same switch meant that watching one channel left the grid stripped back to favicons for the rest
    /// of the session, with nothing able to repaint it.
    /// </remarks>
    public async Task QueueVisibleAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!GridPreviewFeature.CaptureEnabled || _disposed)
        {
            return;
        }

        var session = _session;
        using var activeRequest = session is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
        var activeToken = activeRequest.Token;
        var rows = await _dispatcher.InvokeAsync(_visibleRows);
        var restoredFromStore = 0;
        var captureable = 0;
        var cooledDown = 0;
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
            captureable++;
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
                    // Apply before Put: Put can evict another entry and blank that row, and the eviction
                    // callback must never race ahead of the row we are filling right now.
                    await ApplyAsync(url, restored, null);
                    _memoryCache.Put(url, restored);
                    hasStored = true;
                    restoredFromStore++;
                }
            }

            // Stored previews always show; auto-capture of a first-time blank only when the setting is on.
            // Explicit refresh always captures - but only while a capture session exists.
            if (session is not null && (force || (!hasStored && _autoCaptureEnabled())) &&
                Enqueue(url, force) == PreviewEnqueueOutcome.UnderCooldown)
            {
                cooledDown++;
            }
        }

        // cooledDown is why this pass queued fewer captures than it had blank tiles: without it a fix
        // that stops retrying dead sources is indistinguishable in the log from one that stopped working.
        _diagnostics?.Invoke("PREVIEW VISIBLE",
            [$"rows={rows.Count}", $"captureable={captureable}", $"restored={restoredFromStore}", $"force={force}",
             $"cooldown={cooledDown}"]);
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycle.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await StopSessionAsync();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Ends the capture session. The caller holds <see cref="_lifecycle"/>.</summary>
    private async Task StopSessionAsync()
    {
        if (_session is null)
        {
            _diagnostics?.Invoke("PREVIEW COORD", ["state=stop_noop"]);
            return;
        }

        _diagnostics?.Invoke("PREVIEW COORD", ["state=stopping"]);

        var session = _session;
        var workers = _workers ?? [];
        _session = null;
        _workers = null;
        session.Cancel(); // wakes every worker: WaitAsync(token) and in-flight CaptureAsync both throw
        var stopped = Task.WhenAll(workers);
        try
        {
            if (await Task.WhenAny(stopped, Task.Delay(WorkerStopTimeout)) == stopped)
            {
                await stopped;
            }
            else
            {
                // A worker is still inside a capture. Its session is cancelled, so it leaves the loop when
                // the capture returns; waiting for it here would hold the lock the whole time. Its fault, if
                // it ends in one, is observed here so it is not reported later as an unobserved task.
                _diagnostics?.Invoke("PREVIEW COORD", ["state=stop_timeout", $"after_ms={WorkerStopTimeout.TotalMilliseconds:F0}"]);
                _ = stopped.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal grid-exit path.
        }
        catch (Exception exception)
        {
            // SP-0166: workers contain their own faults, so this is unreachable by design; it is the belt that
            // keeps a stop - and the close steps behind it - from ever ending in a worker's exception.
            _diagnostics?.Invoke("PREVIEW COORD", ["state=stop_fault", .. FaultLogFields.Of(exception)]);
        }
        finally
        {
            session.Dispose();
            while (_queue.TryDequeue(out _))
            {
            }

            // The queue and the signal count must fall together: a dropped request that left its
            // release behind would wake a worker of the next session with nothing to dequeue.
            while (_signal.Wait(0))
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

            lock (_hoverGate)
            {
                _lastHoverCapture.Clear(); // otherwise it grows with every distinct tile hovered, for the session
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // SP-0120: the flag is set and the session ended under the same hold of the lock, so no caller can slip
        // a new session in between them - a caller already queued on the lock is granted it afterwards and
        // finds the flag. The semaphores stay undisposed for that caller's sake (see their declaration).
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopSessionAsync();
        }
        finally
        {
            _lifecycle.Release();
        }

        await _captureService.DisposeAsync();
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

            // SP-0069: an entry older than the throttle window can never suppress anything again, so it
            // is dead weight. Without this sweep the map kept one entry per tile the pointer had ever
            // crossed and was emptied only by StopAsync - a session-long map bounded by nothing but the
            // catalog. Sweeping on insert keeps the cost proportional to the hovering that caused it.
            if (_lastHoverCapture.Count > 0)
            {
                foreach (var expired in _lastHoverCapture
                    .Where(entry => now - entry.Value >= HoverThrottle)
                    .Select(entry => entry.Key)
                    .ToList())
                {
                    _lastHoverCapture.Remove(expired);
                }
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

    /// <summary>Queues a capture, and says why when it does not.</summary>
    private PreviewEnqueueOutcome Enqueue(string url, bool force)
    {
        // An explicit request - a hover dwell or an explicit refresh - is the user asking for this tile
        // now, and clears the cooldown instead of being stopped by it.
        if (_captureService.IsPaused)
        {
            return PreviewEnqueueOutcome.Paused;
        }

        lock (_failureGate)
        {
            if (force)
            {
                _failureCooldown.Forget(url);
            }
            else if (_failureCooldown.IsSuppressed(url, DateTimeOffset.UtcNow))
            {
                return PreviewEnqueueOutcome.UnderCooldown;
            }
        }

        lock (_pendingGate)
        {
            if (!_pending.Add(url))
            {
                return PreviewEnqueueOutcome.AlreadyPending;
            }
        }

        _queue.Enqueue(new PreviewRequest(url, force));
        _signal.Release();
        return PreviewEnqueueOutcome.Queued;
    }

    private enum PreviewEnqueueOutcome
    {
        Queued,
        AlreadyPending,
        UnderCooldown,
        Paused
    }

    /// <summary>
    /// The containment around one capture worker (SP-0166). A worker never ends in a fault: it ends when its
    /// session is cancelled, or - on any other exception - after logging it once and cancelling the session, so
    /// the pool stops as a whole. The rule is "stays stopped until the next explicit start": capture is decorative,
    /// so it is not restarted behind the user's back, and <see cref="StartAsync"/> (a window reactivation) is the
    /// one thing that brings it back. Nothing is rethrown into <see cref="StopSessionAsync"/> or the disposal.
    /// </summary>
    private async Task RunWorkerAsync(CancellationTokenSource session)
    {
        try
        {
            await ServeQueueAsync(session.Token);
        }
        catch (OperationCanceledException)
        {
            // The session ended; leaving the loop through the wait is the normal way out.
        }
        catch (Exception exception)
        {
            ReportWorkerFault(session, exception);
        }
    }

    private void ReportWorkerFault(CancellationTokenSource session, Exception exception)
    {
        try
        {
            if (Interlocked.Exchange(ref _workerFaultReported, 1) == 0)
            {
                _diagnostics?.Invoke("PREVIEW WORKER FAULT", [.. FaultLogFields.Of(exception), "previews=stopped_until_restart"]);
            }

            session.Cancel(); // wakes the other workers; the window's own stop cleans the session up later
        }
        catch (Exception)
        {
            // A disposed session (the stop got there first) or a failing log sink: nothing is left to protect.
        }
    }

    private async Task ServeQueueAsync(CancellationToken cancellationToken)
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

                // SP-0166: capture paused after abandoned engines piled up; the request is dropped, not failed -
                // a failure would mark a healthy source unreachable and put it under cooldown.
                if (_captureService.IsPaused)
                {
                    continue;
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
                    lock (_failureGate)
                    {
                        _failureCooldown.RecordFailure(request.Url, DateTimeOffset.UtcNow);
                    }

                    _reportCaptureFailure?.Invoke(request.Url);
                    await ApplyReachabilityAsync(request.Url, false);
                    continue;
                }

                lock (_failureGate)
                {
                    // A source that answers is not a source under cooldown, whatever it did before.
                    _failureCooldown.Forget(request.Url);
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
