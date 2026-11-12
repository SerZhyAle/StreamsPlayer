using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0262: the control stream's session loop - connect and back off (RunAsync), then hold the stream open and read
// it (HoldAsync). What each frame does is in ExchangeSourceService.Frames.cs.
internal sealed partial class ExchangeSourceService
{
    private void StartLoop(ExchangeTlsConnection? enrolled, int interval, bool announceConnecting = true)
    {
        _connectionCancellation = new CancellationTokenSource();
        _connectionTask = RunAsync(enrolled, interval, announceConnecting, _connectionCancellation.Token);
    }

    private async Task StopLoopAsync()
    {
        if (_connectionCancellation is null)
        {
            return;
        }

        await _connectionCancellation.CancelAsync().ConfigureAwait(false);
        // Teardown must never be blocked by how the loop ended: a loop that faulted would otherwise rethrow
        // here on every Forget, Disable and Enroll until the process restarts. The loop classifies the
        // failures it expects itself; an unexpected one ends it with the status stale, so say so.
        await ObserveAsync(_connectionTask).ConfigureAwait(false);
        if (_connectionTask.IsFaulted)
        {
            ClearDirectory();
            SetStatus("ExchangeUnavailable");
        }

        _connectionCancellation.Dispose();
        _connectionCancellation = null;
        _connectionTask = Task.CompletedTask;
    }

    /// <summary>Waits for a task to end by any outcome without rethrowing, and marks a fault as observed.</summary>
    private static async Task ObserveAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        _ = task.Exception;
    }

    private async Task RunAsync(ExchangeTlsConnection? connection, int interval, bool announceConnecting,
        CancellationToken cancellationToken)
    {
        var backoffSeconds = 1;
        var authenticated = connection is not null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (connection is null)
                {
                    if (announceConnecting)
                    {
                        SetStatus("ExchangeConnecting");
                    }

                    announceConnecting = true;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    connection = await ExchangeTlsConnection.OpenAsync(Account.Host, Account.Port, Account.Pin, deadline.Token,
                        Account.PinKey).ConfigureAwait(false);
                    await ExchangeProtocol.WriteAsync(connection.Stream, new
                    {
                        schemaVersion = 2, type = "hello", deviceId = Account.DeviceId,
                        deviceToken = Account.Token, productVersion = ProductInfo.Version, keepaliveSeconds = 30
                    }, false, deadline.Token).ConfigureAwait(false);
                    _lastEnvelopeType = "hello";
                    using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", deadline.Token).ConfigureAwait(false);
                    if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
                    {
                        if (EndForRefusal(welcome.RootElement))
                        {
                            return;
                        }

                        throw new ExchangeRetryRefusalException(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(welcome.RootElement, "reason")));
                    }

                    interval = ExchangeHandshake.ReadInterval(welcome.RootElement);
                    // The kind is read before anything announces this session: a guest's welcome must never reach
                    // the producer's SessionOnline, which re-publishes broadcasts (item AO, item H).
                    var kind = ExchangeAccountKinds.Parse(ExchangeProtocol.String(welcome.RootElement, "accountKind"));
                    if (kind == ExchangeAccountKind.Guest && await AccountBecameGuestAsync().ConfigureAwait(false))
                    {
                        return;
                    }

                    if (kind == ExchangeAccountKind.Member)
                    {
                        AccountBecameMember();
                    }

                    RememberLeaf(connection);
                    RememberServerFacts(welcome.RootElement);
                    authenticated = true;
                }

                SetStatus("ExchangeOnline");
                await HoldAsync(connection, interval, cancellationToken).ConfigureAwait(false);
                ClearDirectory();
                return;
            }
            catch (ExchangeCertificateException exception)
            {
                ShowCertificate(exception, Account.Host, Account.Port);
                ClearDirectory();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ClearDirectory();
                return;
            }
            catch (ExchangeRetryRefusalException exception)
            {
                SetStatus(exception.Key);
                ClearDirectory();
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                // A connection that was open when it failed closed with no frame after this product's own
                // envelope (item AN); one that never opened could not be reached, or could not be protected.
                SetStatus(ExchangeFailures.KeyOf(exception, connectionOpen: connection is not null));
                if (connection is not null)
                {
                    _diagnostic?.Invoke($"EXCHANGE CLOSED code=unanswered envelope={_lastEnvelopeType ?? "unknown"}");
                }
                ClearDirectory();
            }
            finally
            {
                if (connection is not null)
                {
                    if (_accountBeingRemoved is { } removing)
                    {
                        if (authenticated)
                        {
                            _removalWritten = await TryRevokeAsync(connection, removing.DeviceId).ConfigureAwait(false);
                        }
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        await TryByeAsync(connection).ConfigureAwait(false);
                    }

                    connection.Dispose();
                    connection = null;
                    authenticated = false;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoffSeconds = Math.Min(30, backoffSeconds * 2);
        }
    }

    private async Task HoldAsync(ExchangeTlsConnection connection, int interval, CancellationToken cancellationToken)
    {
        var lastAnswer = System.Diagnostics.Stopwatch.StartNew();
        // The read loop and the cast answers share one stream, and a frame is several writes: they take turns.
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(object envelope, CancellationToken token)
        {
            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ExchangeProtocol.WriteAsync(connection.Stream, envelope, true, token).ConfigureAwait(false);
                _lastEnvelopeType = ExchangeProtocol.String(JsonSerializer.SerializeToElement(envelope, ExchangeProtocol.JsonOptions), "type");
            }
            finally
            {
                writeGate.Release();
            }
        }

        await SendAsync(new { schemaVersion = 2, type = "keepalive" }, cancellationToken).ConfigureAwait(false);
        Task<JsonDocument>? pending = null;
        var castTasks = new List<OpenCastAnswer>();
        var castFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var castCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // SP-0222 requirement 1: nothing is listed because a session came up. A list is asked for by a lease
        // (ExchangeSourceService.Directory.cs): the list fills the view first, then subscribe keeps it current.
        // A gap in revision below answers itself with a new list; subscribe is written once per session, after
        // the first answer a follower asked for.
        var session = new ControlSession(SendAsync, lastAnswer, castTasks, castFailed, castCancellation.Token);
        var nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
        // SP-0217: the producer's envelopes ride the same stream the loop owns. The queue is bounded and
        // refuses at once when full, so a broadcast start can never be queued past the session it needs.
        var outgoing = Channel.CreateBounded<(object Envelope, TaskCompletionSource<bool> Written)>(32);
        _producerOutgoing = outgoing;
        Task<bool> outgoingWait = outgoing.Reader.WaitToReadAsync(readCancellation.Token).AsTask();
        try
        {
            // A lease taken while no session was up (or in the gap before the queue above existed) is owed its
            // list now. Checked after the queue is published: a lease taken from here on posts its own.
            if (HoldsDirectory)
            {
                await SendAsync(new { schemaVersion = 2, type = "list" }, cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= ExchangeProtocol.ReadAsync(connection.Stream, true, readCancellation.Token);
                var ready = await Task.WhenAny(pending, nextTick, castFailed.Task, outgoingWait).ConfigureAwait(false);
                if (ready == castFailed.Task)
                {
                    throw new IOException("Exchange cast answer could not be delivered.");
                }

                if (ready == nextTick)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lastAnswer.Elapsed.TotalSeconds >= interval * 3)
                    {
                        throw new IOException("Exchange keepalive window expired.");
                    }

                    await SendAsync(new { schemaVersion = 2, type = "keepalive" }, cancellationToken).ConfigureAwait(false);
                    nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    continue;
                }

                if (ready == outgoingWait)
                {
                    // Drain everything the producer posted; one write per envelope, in order, and the
                    // writer's own completion says whether it landed.
                    while (outgoing.Reader.TryRead(out var posted))
                    {
                        try
                        {
                            await SendAsync(posted.Envelope, cancellationToken).ConfigureAwait(false);
                            posted.Written.TrySetResult(true);
                        }
                        catch (Exception exception) when (IsConnectionFailure(exception))
                        {
                            posted.Written.TrySetResult(false);
                            throw;
                        }
                    }

                    outgoingWait = outgoing.Reader.WaitToReadAsync(readCancellation.Token).AsTask();
                    continue;
                }

                using var response = await pending.ConfigureAwait(false);
                pending = null;
                ExchangeHandshake.ValidateSchema(response.RootElement);
                var type = ExchangeProtocol.String(response.RootElement, "type");
                if (await HandleFrameAsync(type, response.RootElement, session, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            // The session is over: dismiss every open question and wait for the answer tasks, so none of them
            // writes to the stream after the connection is disposed. AnswerCastOfferAsync ends every failure
            // itself, and ObserveAsync waits for each by any outcome.
            _castCoordinator.CancelAll();
            await castCancellation.CancelAsync().ConfigureAwait(false);
            foreach (var open in castTasks)
            {
                await ObserveAsync(open.Task).ConfigureAwait(false);
            }

            // A password change still out when the session ends is over for whoever waits: its answer, if the
            // server ever wrote one, went with the stream. The next hello says the truth (item AT).
            AbandonPasswordChange();
            // SP-0260 / item ER: a settings request still out is unanswered - a read applies nothing.
            AbandonSettings();
            // The producer's queue ends with the session: an envelope that never reached the stream is
            // answered false, never left waiting for a stream that no longer exists.
            _producerOutgoing = null;
            Volatile.Write(ref _updateRefusalUntil, 0);
            outgoing.Writer.TryComplete();
            while (outgoing.Reader.TryRead(out var undelivered))
            {
                undelivered.Written.TrySetResult(false);
            }

            // Observe the single pending read before disposing the connection, including timeout and quit paths.
            await readCancellation.CancelAsync().ConfigureAwait(false);
            if (pending is not null)
            {
                try
                {
                    (await pending.ConfigureAwait(false)).Dispose();
                }
                catch (Exception exception) when (IsConnectionFailure(exception))
                {
                    // The owning loop handles the failure; this is only the cancelled outstanding read.
                }
            }
        }
    }
}
