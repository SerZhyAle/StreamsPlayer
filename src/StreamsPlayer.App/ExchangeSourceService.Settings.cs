using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// What a "Save as STANDARD" or "Read STANDARD" ended with: the words to show, the server's <c>updatedAt</c> when it
/// answered, and for a read the standard understood as a whole (nothing applied yet).
/// </summary>
internal sealed record ExchangeSettingsResult(bool Succeeded, string MessageKey, DateTimeOffset? UpdatedAt = null,
    StandardSettingsDocument? Document = null);

/// <summary>
/// SP-0260 / <c>DEVICE-EXCHANGE</c> 1.12 items EN to ER: the settings standard on the live control stream. Both
/// actions are the person's own act: nothing here runs on a change, on connecting or at start (item ER), and a server
/// whose <c>welcome.features</c> leaves <c>settings</c> out is never sent a frame (item EO).
/// </summary>
internal sealed partial class ExchangeSourceService
{
    private sealed record SettingsAnswer(string? Refusal, ExchangeSettingsSaved? Saved, ExchangeSettingsPayload? Payload);

    private sealed record PendingSettings(bool Save, TaskCompletionSource<SettingsAnswer?> Answer);

    // One request at a time (item EF: a request awaits its one answer). It stays pending until the server answers or the
    // session ends - even after the caller stopped waiting - so a late answer is never taken for the next request's.
    private PendingSettings? _pendingSettings;

    /// <summary>How long the screen waits for an answer before it says the server did not answer.</summary>
    private static readonly TimeSpan SettingsAnswerWait = TimeSpan.FromSeconds(30);

    /// <summary>The reasons of 7.10 that are about the session itself and keep ending it, whatever is pending.</summary>
    private static bool IsSessionRefusal(string? reason) =>
        reason is "bad-credentials" or "device-revoked" or "signed-out" or "version-unsupported" or "tls-required";

    /// <summary>
    /// True while the control stream of the own entry is authenticated and its server lists <c>settings</c> (items EO,
    /// ER). A guest's account is not excluded here: the server answers it <c>not-permitted</c> and the screen says so.
    /// Read from the status the session raises <see cref="Changed"/> with, so the screen sees it as soon as it is true;
    /// a request in the instant before the stream's queue exists is not written and reads as unreachable.
    /// </summary>
    internal bool CanExchangeSettings =>
        Account.Token is not null && StatusKey == "ExchangeOnline" && FeaturesKnown
        && Features.Contains(StandardSettings.Feature, StringComparer.Ordinal);

    internal Task<ExchangeSettingsResult> SaveStandardSettingsAsync(string settingsPayload, CancellationToken cancellationToken) =>
        RequestSettingsAsync(ExchangeSettingsFrames.Save(settingsPayload, DateTimeOffset.UtcNow), save: true, cancellationToken);

    internal Task<ExchangeSettingsResult> ReadStandardSettingsAsync(CancellationToken cancellationToken) =>
        RequestSettingsAsync(ExchangeSettingsFrames.Get(), save: false, cancellationToken);

    private async Task<ExchangeSettingsResult> RequestSettingsAsync(object frame, bool save, CancellationToken cancellationToken)
    {
        if (!CanExchangeSettings)
        {
            return new ExchangeSettingsResult(false, "ExchangeSettingsUnavailable");
        }

        var pending = new PendingSettings(save,
            new TaskCompletionSource<SettingsAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.CompareExchange(ref _pendingSettings, pending, null) is not null)
        {
            return new ExchangeSettingsResult(false, "ExchangeSettingsBusy");
        }

        bool written;
        try
        {
            written = await PostControlEnvelopeAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref _pendingSettings, null, pending);
            throw;
        }

        if (!written)
        {
            // Nothing reached the server: no answer is owed and nothing was stored.
            Interlocked.CompareExchange(ref _pendingSettings, null, pending);
            return new ExchangeSettingsResult(false, ExchangeFailureRows.Unreachable.MessageKey);
        }

        SettingsAnswer? answer;
        try
        {
            answer = await pending.Answer.Task.WaitAsync(SettingsAnswerWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            answer = null;
        }

        return Outcome(save, answer);
    }

    /// <summary>
    /// Item ER: a save that is not answered leaves the client not knowing whether it was stored; a read that is not
    /// answered changed nothing. A payload is understood as a whole here, before the screen applies a single value.
    /// </summary>
    private static ExchangeSettingsResult Outcome(bool save, SettingsAnswer? answer)
    {
        if (answer is null)
        {
            return new ExchangeSettingsResult(false, save ? "ExchangeSettingsSaveUnknown" : ExchangeFailureRows.Unanswered.MessageKey);
        }

        if (answer.Refusal is { } reason)
        {
            return new ExchangeSettingsResult(false, ExchangeFailureRows.OfWire(reason).MessageKey);
        }

        if (answer.Saved is { } saved)
        {
            return new ExchangeSettingsResult(true, "ExchangeSettingsSavedAt", saved.UpdatedAt);
        }

        var payload = answer.Payload!;
        var read = StandardSettings.Parse(payload.FormatVersion, payload.SettingsPayload);
        return read.Failure switch
        {
            StandardSettingsReadFailure.FormatUnsupported =>
                new ExchangeSettingsResult(false, ExchangeFailureRows.FormatUnsupported.MessageKey),
            StandardSettingsReadFailure.Damaged => new ExchangeSettingsResult(false, "ExchangeSettingsReadDamaged"),
            _ => new ExchangeSettingsResult(true, "ExchangeSettingsReadAt", payload.UpdatedAt, read.Document)
        };
    }

    /// <summary>
    /// <c>settings_saved</c> or <c>settings_standard_payload</c>, read in the control loop while the frame is alive. An
    /// answer nothing asked for, of the other kind, or of another <c>appId</c> is read and dropped.
    /// </summary>
    private void HandleSettingsAnswer(JsonElement root, bool saved)
    {
        var pending = _pendingSettings;
        if (pending is null || pending.Save != saved)
        {
            return;
        }

        SettingsAnswer answer;
        if (saved)
        {
            var savedAnswer = ExchangeSettingsFrames.ReadSaved(root);
            if (savedAnswer is null)
            {
                return;
            }

            answer = new SettingsAnswer(null, savedAnswer, null);
        }
        else
        {
            var payload = ExchangeSettingsFrames.ReadPayload(root);
            if (payload is null)
            {
                return;
            }

            answer = new SettingsAnswer(null, null, payload);
        }

        Interlocked.CompareExchange(ref _pendingSettings, null, pending);
        pending.Answer.TrySetResult(answer);
    }

    /// <summary>
    /// A <c>refused</c> that names no broadcast while a settings request is out is that request's answer (item EP): its
    /// own four reasons, <c>not-permitted</c> for a guest, and any other reason not about the session itself, shown as
    /// the generic refusal when unknown. Returns true when it was consumed.
    /// </summary>
    private bool TryAnswerSettings(string? reason)
    {
        var pending = _pendingSettings;
        if (pending is null || IsSessionRefusal(reason))
        {
            return false;
        }

        Interlocked.CompareExchange(ref _pendingSettings, null, pending);
        pending.Answer.TrySetResult(new SettingsAnswer(reason ?? "refused", null, null));
        return true;
    }

    /// <summary>The session ended with a settings request unanswered: whoever waits is told, and the flight is over.</summary>
    private void AbandonSettings()
    {
        var pending = Interlocked.Exchange(ref _pendingSettings, null);
        pending?.Answer.TrySetResult(null);
    }
}
