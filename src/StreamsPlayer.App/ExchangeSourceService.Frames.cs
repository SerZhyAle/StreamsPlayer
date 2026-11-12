using System.IO;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0262: what the control stream's frames do. The loop that reads them (ExchangeSourceService.Loop.cs) hands each
// validated frame to HandleFrameAsync and acts only on its answer; a new frame type is a new case here and a handler.
internal sealed partial class ExchangeSourceService
{
    /// <summary>
    /// What one session's frame handlers read and write. The loop creates it once per session and owns its
    /// lifetime; the handlers only use it, so the loop's bookkeeping stays apart from the per-frame decisions.
    /// </summary>
    private sealed class ControlSession(Func<object, CancellationToken, Task> send,
        System.Diagnostics.Stopwatch lastAnswer, List<OpenCastAnswer> castTasks, TaskCompletionSource castFailed,
        CancellationToken castToken)
    {
        /// <summary>Writes one envelope; the read loop and the cast answers take turns on the stream through it.</summary>
        internal Func<object, CancellationToken, Task> Send { get; } = send;

        /// <summary>Restarted by every keepalive from the server; the loop ends the session when it runs too long.</summary>
        internal System.Diagnostics.Stopwatch LastAnswer { get; } = lastAnswer;

        internal List<OpenCastAnswer> CastTasks { get; } = castTasks;
        internal TaskCompletionSource CastFailed { get; } = castFailed;

        /// <summary>Cancelled when the session ends; cast answers and tunnel requests live no longer than this.</summary>
        internal CancellationToken CastToken { get; } = castToken;

        /// <summary>Subscribe is written once per session, after the first answer a follower asked for.</summary>
        internal bool Subscribed { get; set; }
    }

    /// <summary>
    /// Acts on one validated control frame. True ends the session here (a refusal that is final); false reads the
    /// next frame. What makes a frame unacceptable is thrown, to the loop that owns reconnecting.
    /// </summary>
    private async Task<bool> HandleFrameAsync(string? type, JsonElement root, ControlSession session,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case "keepalive":
                session.LastAnswer.Restart();
                return false;
            case "directory":
                await HandleDirectoryFrameAsync(root, session, cancellationToken).ConfigureAwait(false);
                return false;
            case "changed":
                await HandleChangedFrameAsync(root, session, cancellationToken).ConfigureAwait(false);
                return false;
            case "cast-offer":
                HandleCastOfferFrame(root, session);
                return false;
            case "cast-stop":
                HandleCastStop(root);
                return false;
            case "open":
                HandleOpenFrame(root, session);
                return false;
            case "broadcast-started":
                AnswerBroadcastStart(root, null);
                return false;
            case "cast-result":
                HandleCastResultFrame(root);
                return false;
            case "password-changed":
                HandlePasswordChanged(root);
                return false;
            case ExchangeSettingsFrames.SavedType:
                HandleSettingsAnswer(root, saved: true);
                return false;
            case ExchangeSettingsFrames.PayloadType:
                HandleSettingsAnswer(root, saved: false);
                return false;
            case "refused":
                return HandleRefusedFrame(root);
            default:
                if (ExchangeProtocol.IsKnownType(type)
                    && type is not "pairing-code")
                {
                    throw new InvalidDataException("Invalid exchange receiver control state.");
                }

                return false;
        }
    }

    private async Task HandleDirectoryFrameAsync(JsonElement root, ControlSession session,
        CancellationToken cancellationToken)
    {
        // An answer nobody holds a lease for (it crossed the release) is read and dropped.
        if (!HoldsDirectory)
        {
            return;
        }

        if (!session.Subscribed && HoldsFollow)
        {
            await session.Send(new { schemaVersion = 2, type = "subscribe" }, cancellationToken).ConfigureAwait(false);
            session.Subscribed = true;
        }

        _directory.ApplyFull(root);
        _directoryFresh = true;
        PublishDirectory();
    }

    private async Task HandleChangedFrameAsync(JsonElement root, ControlSession session,
        CancellationToken cancellationToken)
    {
        // The subscription outlives the open list (the wire has no unsubscribe): a push nobody
        // follows, or one that arrives before this lease's list answered, is read and dropped.
        if (!HoldsFollow || !_directoryFresh)
        {
            return;
        }

        if (_directory.ApplyChange(root).RelistNeeded)
        {
            await session.Send(new { schemaVersion = 2, type = "list" }, cancellationToken).ConfigureAwait(false);
        }

        PublishDirectory();
    }

    private void HandleCastOfferFrame(JsonElement root, ControlSession session)
    {
        // Parsed here, while the frame is alive; answered on its own task, never in this loop.
        if (ExchangeCastOffer.TryParse(root, out var offer) && offer is not null)
        {
            StartCastAnswer(session.CastTasks, offer, session.Send, session.CastFailed, session.CastToken);
        }
    }

    private void HandleOpenFrame(JsonElement root, ControlSession session)
    {
        // A tunnel for this device's own broadcast: the producer bridges it on its own
        // task, off this loop, and owns the per-broadcast bound (item L).
        if (ExchangeTunnelOpen.TryParse(root) is { } tunnel)
        {
            TunnelRequested?.Invoke(tunnel, session.CastToken);
        }
    }

    private void HandleCastResultFrame(JsonElement root)
    {
        if (ExchangeCastResult.TryParse(root) is { } castResult)
        {
            CastResult?.Invoke(castResult);
        }
    }

    /// <summary>
    /// A refusal on an open session. True when it ends the session; false when it was the answer to a request
    /// that is out (a broadcast start, a password change, a settings request) and the session goes on.
    /// </summary>
    private bool HandleRefusedFrame(JsonElement root)
    {
        if (ExchangeProtocol.String(root, "broadcastId") is { })
        {
            AnswerBroadcastStart(root, ExchangeProtocol.String(root, "reason") ?? "refused");
            return false;
        }

        // A refusal while a password change is out is that change's answer, and never this device's revocation.
        if (TryAnswerPasswordChange(ExchangeProtocol.String(root, "reason")))
        {
            return false;
        }

        // A refusal of a broadcast start or update names no broadcast (7.4); it is that request's answer,
        // not the end of a session that has nothing wrong with it.
        if (TryAnswerBroadcastRefusal(ExchangeProtocol.String(root, "reason")))
        {
            return false;
        }

        // SP-0260 / item EP: a refusal while a settings request is out is that request's answer.
        if (TryAnswerSettings(ExchangeProtocol.String(root, "reason")))
        {
            return false;
        }

        if (EndForRefusal(root))
        {
            return true;
        }

        throw new ExchangeRetryRefusalException(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(root, "reason")));
    }
}
