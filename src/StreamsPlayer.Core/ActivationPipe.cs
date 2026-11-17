using System.IO.Pipes;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0118: the running copy's end of the <c>APP-ACTIVATION</c> rule 3 pipe. Accepts one connection at
/// a time, reads one <see cref="ActivationMessage"/> line from it, and hands the arguments on.
/// </summary>
/// <remarks>
/// <para>
/// Every connection runs inside its own failure boundary: an empty, malformed, oversized, stalled or
/// abruptly closed connection is reported through <c>onRejected</c> and the loop goes on to the next
/// one. Only disposal ends it - the pending asynchronous wait is cancelled, so no nudge connection is
/// needed to unblock it.
/// </para>
/// <para>
/// The pipe is opened with <see cref="PipeOptions.CurrentUserOnly"/>, and its name carries the session
/// id (<see cref="SingleInstanceIdentity"/>), so only the current user's own session can reach it.
/// The callbacks run on a pool thread; a UI consumer marshals them itself.
/// </para>
/// <para>
/// SP-0170: every line read is answered with one acknowledgement line - <see cref="AcceptedReply"/> once the
/// request has been handed on, <see cref="RefusedReply"/> for anything the listener will not act on,
/// including every request after <see cref="StopAccepting"/>. The sender therefore knows whether its launch
/// was taken, instead of counting a flushed write as delivery.
/// </para>
/// </remarks>
public sealed class ActivationPipeListener : IAsyncDisposable
{
    /// <summary>The acknowledgement line for a request that was handed on.</summary>
    public const string AcceptedReply = "ok";

    /// <summary>The acknowledgement line for a request the listener will not act on.</summary>
    public const string RefusedReply = "refused";

    /// <summary>How long a connected client has to deliver its line before it is dropped.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onRequest;
    private readonly Action<string, Exception?> _onRejected;
    private readonly Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>>? _onCommand;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _accepting = true;

    /// <summary>How long a response line has to be written once the command has its answer.</summary>
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2);

    public ActivationPipeListener(
        string pipeName,
        Action<IReadOnlyList<string>> onRequest,
        Action<string, Exception?> onRejected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
        _onRejected = onRejected ?? throw new ArgumentNullException(nameof(onRejected));
    }

    /// <summary>
    /// SP-0271: a listener that also answers the <c>APP-COMMANDS</c> rule 7 profile. A line that carries a
    /// <c>requestId</c> goes to <paramref name="onCommand"/> and is answered with one JSON response line on the same
    /// connection; every other line takes the frozen launch path above, answered <see cref="AcceptedReply"/> or
    /// <see cref="RefusedReply"/> exactly as before.
    /// </summary>
    public ActivationPipeListener(
        string pipeName,
        Action<IReadOnlyList<string>> onRequest,
        Action<string, Exception?> onRejected,
        Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> onCommand)
        : this(pipeName, onRequest, onRejected)
    {
        _onCommand = onCommand ?? throw new ArgumentNullException(nameof(onCommand));
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>
    /// SP-0170: from now on every well-formed request is answered <see cref="RefusedReply"/> and not handed
    /// on. The pipe stays open so a later copy learns at once that this one is closing, instead of timing out.
    /// </summary>
    public void StopAccepting() => _accepting = false;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception exception)
            {
                Reject("pipe could not be opened", exception);
                if (!await DelayAsync(ReopenDelay, stopping).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // SP-0184 (S13-3): a wait that keeps failing must not spin; back off like the open path.
                    Reject("connection failed", exception);
                    if (!await DelayAsync(ReopenDelay, stopping).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                await ServeAsync(server, stopping).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(ReadTimeout);
        try
        {
            var read = await ReadLineAsync(server, deadline.Token).ConfigureAwait(false);
            if (read.Oversized)
            {
                Reject("payload exceeded the size limit", null);
                if (_onCommand is not null)
                {
                    // SP-0279, APP-COMMANDS item I.3: a request line over the bound is answered, not dropped. It is not a
                    // request this side could read, so the answer carries no request id (the second process reads that as
                    // unreadable and exits 2); a listener without the profile keeps the silence it always had.
                    await WriteResponseAsync(server,
                        AppCommandResponse.Refused(string.Empty, AppCommandReason.TooLarge, AppCommandExit.Usage, AppCommandMessages.TooLarge),
                        stopping, closing: false).ConfigureAwait(false);
                }

                return;
            }

            var line = read.Bytes;
            var accepted = false;
            if (line.Length == 0)
            {
                Reject("empty connection", null);
            }
            else if (_onCommand is not null && AppCommandRequest.Read(line) is { Kind: not AppCommandLineKind.Legacy } command)
            {
                await AnswerCommandAsync(server, command, stopping).ConfigureAwait(false);
                return;
            }
            else if (!ActivationMessage.TryParse(line, out var arguments))
            {
                Reject("malformed payload", null);
            }
            else if (!_accepting)
            {
                Reject("listener is closing", null);
            }
            else
            {
                _onRequest(arguments);
                accepted = true;
            }

            if (line is { Length: > 0 })
            {
                await AcknowledgeAsync(server, accepted, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            Reject("client sent nothing within the read timeout", null);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the connection is abandoned with the listener.
        }
        catch (Exception exception)
        {
            Reject("connection aborted", exception);
        }
    }

    /// <summary>
    /// SP-0271: one profile request, one response line. A closing listener answers <c>busy</c> without asking the
    /// handler (rule 10); a handler that throws is answered <c>failed</c>, and the loop goes on either way.
    /// </summary>
    private async Task AnswerCommandAsync(NamedPipeServerStream server, AppCommandLineRead command, CancellationToken stopping)
    {
        AppCommandResponse response;
        var closing = false;
        switch (command.Kind)
        {
            case AppCommandLineKind.NewerSchema:
                Reject("command of a newer schema", null);
                response = AppCommandResponse.Refused(command.RequestId, AppCommandReason.Schema, AppCommandExit.NotProven,
                    AppCommandMessages.Schema);
                break;
            case AppCommandLineKind.Request when !_accepting:
                Reject("listener is closing", null);
                response = AppCommandResponse.Busy(command.RequestId, AppCommandMessages.Busy);
                break;
            case AppCommandLineKind.Request:
                try
                {
                    response = await _onCommand!(command.Request!, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // SP-0279, item K: a hang-up with no line now reads as an older copy, so a listener that is closing
                    // under a running command says busy instead of going quiet.
                    Reject("listener stopped during a command", null);
                    response = AppCommandResponse.Busy(command.RequestId, AppCommandMessages.Busy);
                    closing = true;
                }
                catch (Exception exception)
                {
                    Reject("command handler failed", exception);
                    response = AppCommandResponse.Failed(command.RequestId, AppCommandReason.Error, AppCommandExit.Failed,
                        AppCommandMessages.Failed);
                }

                break;
            default:
                Reject("malformed command", null);
                response = AppCommandResponse.Refused(command.RequestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                    AppCommandMessages.BadArgument);
                break;
        }

        await WriteResponseAsync(server, response, stopping, closing).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one response line and waits for the caller to hang up. A line over the bound of <c>APP-COMMANDS</c> item L is
    /// replaced by <c>response-too-large</c>. <paramref name="closing"/> gives the write a deadline of its own, because the
    /// listener's own token is already cancelled then and the busy answer would never be written.
    /// </summary>
    private async Task WriteResponseAsync(
        NamedPipeServerStream server, AppCommandResponse response, CancellationToken stopping, bool closing)
    {
        var bytes = response.Serialize();
        if (bytes.Length - 1 > AppCommandLimits.MaximumResponseBytes)
        {
            Reject("response exceeded the size limit", null);
            bytes = AppCommandResponse.Failed(response.RequestId, AppCommandReason.ResponseTooLarge, AppCommandExit.Failed,
                AppCommandMessages.ResponseTooLarge).Serialize();
        }

        using var deadline = closing ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(ResponseWriteTimeout);
        try
        {
            await server.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await server.FlushAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[16];
            while (await server.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // The caller did not hang up in time, or the listener is stopping: the answer was written or is moot.
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// SP-0170: writes the verdict, then waits for the sender to hang up - closing first could discard a reply
    /// the sender has not read yet. A sender that is already gone (an older copy never reads) is not a fault.
    /// </summary>
    private static async Task AcknowledgeAsync(NamedPipeServerStream server, bool accepted, CancellationToken cancellationToken)
    {
        try
        {
            var reply = Encoding.ASCII.GetBytes((accepted ? AcceptedReply : RefusedReply) + "\n");
            await server.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
            await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16];
            while (await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// A line as <see cref="ReadLineAsync"/> read it: the bytes, or <see cref="Oversized"/> when it was more than
    /// <see cref="ActivationMessage.MaximumPayloadBytes"/> (the bytes are then empty).
    /// </summary>
    private readonly record struct LineRead(byte[] Bytes, bool Oversized);

    /// <summary>
    /// SP-0279: how much of an oversized line is read and thrown away after the bound, looking for its newline, so that a
    /// sender that is still writing has finished by the time it is answered. Past this the answer is written anyway.
    /// </summary>
    private const int MaximumDrainBytes = 1024 * 1024;

    /// <summary>
    /// The bytes up to the first newline or the end of the stream; <see cref="LineRead.Oversized"/> when that is more than
    /// <see cref="ActivationMessage.MaximumPayloadBytes"/>. An oversized line is read on to its newline (bounded by
    /// <see cref="MaximumDrainBytes"/> and the caller's deadline) and not kept.
    /// </summary>
    private static async Task<LineRead> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var buffer = new byte[4096];
        var oversized = false;
        long discarded = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return oversized ? new LineRead([], true) : new LineRead(line.ToArray(), false);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var length = newline < 0 ? read : newline;
            if (oversized)
            {
                discarded += length;
            }
            else
            {
                line.Write(buffer, 0, length);
                oversized = line.Length > ActivationMessage.MaximumPayloadBytes;
            }

            if (newline >= 0 || (oversized && discarded > MaximumDrainBytes))
            {
                return oversized ? new LineRead([], true) : new LineRead(line.ToArray(), false);
            }
        }
    }

    private void Reject(string reason, Exception? exception)
    {
        try
        {
            _onRejected(reason, exception);
        }
        catch
        {
            // A failing diagnostic sink must not be the thing that stops the listener.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(delay, stopping).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>SP-0170: how a forwarded launch ended, from the sending copy's side.</summary>
public enum ActivationSendResult
{
    /// <summary>The running copy took the launch and said so.</summary>
    Delivered,

    /// <summary>The launch is beyond what the receiver accepts; nothing was sent.</summary>
    TooLarge,

    /// <summary>The running copy answered that it will not act on the launch - it is closing, or found it malformed.</summary>
    Refused,

    /// <summary>Nothing accepted the connection in time, the write failed, or no acknowledgement came back.</summary>
    Unanswered
}

/// <summary>SP-0118: a later copy's end of the pipe.</summary>
public static class ActivationPipeClient
{
    /// <summary>The contract's bounded connect timeout (rule 3 allows 500-1000 ms).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>SP-0170: how long the sender waits for the acknowledgement once its line is written.</summary>
    public static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delivers <paramref name="arguments"/> to the copy listening on <paramref name="pipeName"/> and waits for
    /// its acknowledgement. Never throws for a pipe fault; a launch the receiver's limits would refuse is not
    /// sent (<see cref="ActivationSendResult.TooLarge"/>).
    /// </summary>
    public static ActivationSendResult TrySend(string pipeName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (!ActivationMessage.TrySerialize(arguments, out var payload))
        {
            return ActivationSendResult.TooLarge;
        }

        // Off the caller's context: a UI caller blocks here, and nothing below may need it back.
        return Task.Run(() => SendAsync(pipeName, payload, timeout)).GetAwaiter().GetResult();
    }

    private static async Task<ActivationSendResult> SendAsync(string pipeName, byte[] payload, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);

            // One deadline for the write as well as the wait: the pipe is unbuffered, so a write to a receiver
            // that has stopped reading would otherwise never return.
            using var deadline = new CancellationTokenSource(AcknowledgementTimeout);
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = await ReadReplyAsync(client, deadline.Token).ConfigureAwait(false);
            return reply switch
            {
                ActivationPipeListener.AcceptedReply => ActivationSendResult.Delivered,
                ActivationPipeListener.RefusedReply => ActivationSendResult.Refused,
                _ => ActivationSendResult.Unanswered
            };
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return ActivationSendResult.Unanswered;
        }
    }

    /// <summary>The acknowledgement line, or <see langword="null"/> when the receiver hung up without one.</summary>
    private static async Task<string?> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var buffer = new byte[64];
        while (line.Length < 64)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, newline < 0 ? read : newline);
            if (newline >= 0)
            {
                return Encoding.ASCII.GetString(line.ToArray()).Trim();
            }
        }

        return null;
    }
}
