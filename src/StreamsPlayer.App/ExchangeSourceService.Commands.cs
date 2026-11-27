using System.IO;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// STRWIN-0303: one <c>command-request</c> as the target receives it. <see cref="Payload"/> is null when the box did not
/// open (rule 4): then <see cref="ControllerId"/> is the frame's clear routing id, unverified, and nothing is answered.
/// </summary>
internal sealed record RemoteCommandInbound(
    string ControllerId,
    string RequestId,
    byte[]? Payload,
    Func<byte[], Task<bool>> ReplyAsync)
{
    public bool Opened => Payload is not null;
}

// STRWIN-0303 / DEVICE-EXCHANGE 1.18 items GC and GD: the transport half of a command - open the box with this
// entry's key and the key it trusts for the box's `from`, and seal the answer back to that `from` alone. What the
// command means, and whether it runs, is RemoteCommandTarget's.
internal sealed partial class ExchangeSourceService
{
    /// <summary>Raised on the read loop's thread for every forwarded request; the handler must not block.</summary>
    internal event Action<RemoteCommandInbound>? CommandRequested;

    private void HandleCommandRequest(JsonElement root, ControlSession session)
    {
        if (ExchangeCommandFrames.ReadRequest(root) is not { } frame || CommandRequested is not { } handler)
        {
            return;
        }

        var controllerId = frame.Box.From;
        var trusted = Trust.TrustedDevice(controllerId);
        byte[]? payload = null;
        using (var self = OwnE2eKey())
        {
            if (self is not null && trusted is not null)
            {
                var opening = ExchangeSealedBoxes.Open(frame.Box, ExchangeSealedContext.Command(frame.RequestId),
                    Account.DeviceId, self, trusted);
                payload = opening.Payload;
            }
        }

        if (payload is null)
        {
            handler(new RemoteCommandInbound(frame.FromDeviceId, frame.RequestId, null, _ => Task.FromResult(false)));
            return;
        }

        var send = session.Send;
        var token = session.CastToken;
        handler(new RemoteCommandInbound(controllerId, frame.RequestId, payload,
            answer => ReplyToCommandAsync(controllerId, frame.RequestId, answer, send, token)));
    }

    /// <summary>Seals <paramref name="answer"/> to the controller with the result context and writes it on the session that carried the request.</summary>
    private async Task<bool> ReplyToCommandAsync(string controllerId, string requestId, byte[] answer,
        Func<object, CancellationToken, Task> send, CancellationToken token)
    {
        if (token.IsCancellationRequested || Trust.TrustedDevice(controllerId) is not { } point)
        {
            return false;
        }

        ExchangeSealedBox box;
        using (var self = OwnE2eKey())
        {
            if (self is null)
            {
                return false;
            }

            box = ExchangeSealedBoxes.Seal(answer, ExchangeSealedContext.CommandResult(requestId), Account.DeviceId, self,
                [new ExchangeSealedRecipient(controllerId, point)]);
        }

        try
        {
            await send(ExchangeCommandFrames.Result(controllerId, requestId, box), token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The session ended under the answer: the controller's own wait ends it (rule 13, timeout).
            return false;
        }
    }
}
