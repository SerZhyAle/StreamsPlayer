using System.Diagnostics;
using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0222 reading 4 / requirement 5: the broadcasts one guest entry was given, read while the list is open.
/// A guest sign-in is a stored entry that nothing connects (SP-0221), so a guest's list needs a short session
/// of its own: <c>hello</c> with the entry's id and token, <c>list</c>, <c>subscribe</c> once the answer comes,
/// keepalives, and <c>bye</c> when the list closes. It is read-only by construction - it sends nothing else,
/// announces nothing, never takes the own entry's slot, and writes no account: the entry's token is read to
/// say <c>hello</c> and goes nowhere else. What the server lists to a guest is only what was granted to it
/// (<c>DEVICE-EXCHANGE</c> item BD); nothing is filtered here and nothing is added.
/// </summary>
internal sealed class ExchangeGuestDirectoryViewer(ExchangeAccount entry, Action<Exception>? reportFault = null, ExchangeDeviceTrust? trust = null) : IDisposable
{
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    private readonly ExchangeDirectoryState _state = new();
    private readonly CancellationTokenSource _cancellation = new();
    private int _started;

    /// <summary>The server asked this session to wait (rate, capacity, unavailable): its reason is already shown.</summary>
    private sealed class ServerPauseException() : IOException("The server asked this session to wait.");

    /// <summary>The entry this session reads. The token it carries is never copied out of it.</summary>
    internal ExchangeAccount Entry { get; } = entry;

    /// <summary>What the server listed to this guest; null until the first answer and again after the connection is lost.</summary>
    internal ExchangeDirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The line that says why there is no list: connecting, unreachable, or the server's refusal.</summary>
    internal string StatusKey { get; private set; } = "ExchangeConnecting";

    /// <summary>True once the server has ended this sign-in or refused it for good; the list then offers the next step.</summary>
    internal bool Ended { get; private set; }

    internal event Action? Changed;

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // RunAsync ends every failure itself, so the task has nothing left to fault with.
            _ = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    public void Dispose()
    {
        // The loop's own finally says bye and closes the stream; waiting for it here would block the window
        // that is closing on a server that may not answer.
        _cancellation.Cancel();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            ExchangeTlsConnection? connection = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(ConnectBudget);
                connection = await ExchangeTlsConnection.OpenAsync(Entry.Host, Entry.Port, Entry.Pin, deadline.Token,
                    Entry.PinKey).ConfigureAwait(false);
                await ExchangeProtocol.WriteAsync(connection.Stream, new
                {
                    schemaVersion = 2, type = "hello", deviceId = Entry.DeviceId, deviceToken = Entry.Token,
                    productVersion = ProductInfo.Version, keepaliveSeconds = 30
                }, false, deadline.Token).ConfigureAwait(false);
                using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", deadline.Token)
                    .ConfigureAwait(false);
                if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
                {
                    if (EndedBy(ExchangeProtocol.String(welcome.RootElement, "reason")))
                    {
                        return;
                    }

                    throw new ServerPauseException();
                }

                backoff = TimeSpan.FromSeconds(1);
                await HoldAsync(connection, ExchangeHandshake.ReadInterval(welcome.RootElement), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (ExchangeCertificateException exception)
            {
                // Approving a changed certificate is Settings' act on the entry; this session only says so.
                Ended = true;
                Publish(ExchangeFailures.CertificateKey(exception), null);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ServerPauseException)
            {
                // The reason was published where it was read; the loop waits and asks again.
            }
            catch (Exception exception) when (ExchangeSourceService.IsConnectionFailure(exception))
            {
                Publish(ExchangeFailures.KeyOf(exception, connectionOpen: connection is not null), null);
            }
            catch (Exception exception)
            {
                // Whatever else went wrong is this session's fault to report, and it ends here: a background
                // task nobody awaits must not take the list down with it.
                reportFault?.Invoke(exception);
                Ended = true;
                Publish("ExchangeUnavailable", null);
                return;
            }
            finally
            {
                if (connection is not null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await ExchangeSourceService.TryByeAsync(connection).ConfigureAwait(false);
                    }

                    connection.Dispose();
                }
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaximumBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private async Task HoldAsync(ExchangeTlsConnection connection, int interval, CancellationToken cancellationToken)
    {
        var lastAnswer = Stopwatch.StartNew();
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<System.Text.Json.JsonDocument>? pending = null;
        var subscribed = false;
        var nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
        try
        {
            await SendAsync(connection, "keepalive", cancellationToken).ConfigureAwait(false);
            await SendAsync(connection, "list", cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= ExchangeProtocol.ReadAsync(connection.Stream, true, readCancellation.Token);
                var ready = await Task.WhenAny(pending, nextTick).ConfigureAwait(false);
                if (ready == nextTick)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lastAnswer.Elapsed.TotalSeconds >= interval * 3)
                    {
                        throw new IOException("Exchange keepalive window expired.");
                    }

                    await SendAsync(connection, "keepalive", cancellationToken).ConfigureAwait(false);
                    nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    continue;
                }

                using var response = await pending.ConfigureAwait(false);
                pending = null;
                ExchangeHandshake.ValidateSchema(response.RootElement);
                switch (ExchangeProtocol.String(response.RootElement, "type"))
                {
                    case "keepalive":
                        lastAnswer.Restart();
                        break;
                    case "directory":
                        if (!subscribed)
                        {
                            await SendAsync(connection, "subscribe", cancellationToken).ConfigureAwait(false);
                            subscribed = true;
                        }

                        _state.ApplyFull(response.RootElement);
                        Publish("ExchangeOnline", _state.Snapshot);
                        break;
                    case "changed":
                        if (_state.ApplyChange(response.RootElement).RelistNeeded)
                        {
                            await SendAsync(connection, "list", cancellationToken).ConfigureAwait(false);
                        }

                        Publish("ExchangeOnline", _state.Snapshot);
                        break;
                    case "refused":
                        if (EndedBy(ExchangeProtocol.String(response.RootElement, "reason")))
                        {
                            return;
                        }

                        throw new ServerPauseException();
                    default:
                        // A guest is never offered a cast and has no role to answer; anything else is not for it.
                        break;
                }
            }
        }
        finally
        {
            await readCancellation.CancelAsync().ConfigureAwait(false);
            if (pending is not null)
            {
                try
                {
                    (await pending.ConfigureAwait(false)).Dispose();
                }
                catch (Exception exception) when (ExchangeSourceService.IsConnectionFailure(exception))
                {
                    // The cancelled outstanding read; the owning loop handles the connection.
                }
            }
        }
    }

    private static Task SendAsync(ExchangeTlsConnection connection, string type, CancellationToken cancellationToken) =>
        ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type }, true, cancellationToken);

    /// <summary>
    /// A refusal that ends this sign-in for good is shown and ends the session; one that only asks for a pause
    /// (rate, capacity, unavailable) returns false so the loop waits and tries again. The entry itself is never
    /// edited from here.
    /// </summary>
    private bool EndedBy(string? reason)
    {
        Publish(ExchangeProtocol.RefusalKey(reason), null);
        if (reason is "rate-limited" or "capacity" or "unavailable")
        {
            return false;
        }

        Ended = true;
        return true;
    }

    private void Publish(string statusKey, ExchangeDirectorySnapshot? snapshot)
    {
        StatusKey = statusKey;
        Snapshot = snapshot;
        OpenGrantedBoxes(snapshot);
        Changed?.Invoke();
    }

    /// <summary>
    /// SP-0226 / item DJ: a granted broadcast's keys arrive in a box addressed to this entry's device, under the context
    /// of its grant. The guest knows none of the owner's devices, so the first box of a grant fixes the sender's key
    /// (kept in the entry's trust), and a later box of the same grant from another key is `key-changed`.
    /// </summary>
    private void OpenGrantedBoxes(ExchangeDirectorySnapshot? snapshot)
    {
        if (snapshot is null || trust is null)
        {
            return;
        }

        using var self = Entry.LoadE2eKey();
        foreach (var record in snapshot.Groups.SelectMany(group => group.Broadcasts).Select(view => view.Record))
        {
            if (record.Sealed is null && !record.SealedUnreadable)
            {
                continue;
            }

            if (self is null)
            {
                ExchangeBroadcastKeys.Fail(record.BroadcastId, ExchangeSealedState.E2eMissing);
                continue;
            }

            _ = ExchangeBroadcastKeys.OpenRecord(record, Entry.DeviceId, self, trust);
        }

        // A registering box (empty keys) opens nothing and shows nothing (item DJ): that is a record not yet listed.
    }
}
