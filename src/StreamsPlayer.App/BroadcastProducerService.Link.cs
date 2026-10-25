using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// LIVE-BROADCAST items M and R, `DEVICE-EXCHANGE` items BF and DV, the producer's side. Every broadcast holds an own pair
/// (for the account's devices, sealed into its record) and, unless the person chose "none", a link pair (for anyone the
/// person gave the link to). A broadcast is protected from the moment it carries an exchange endpoint: then the LAN
/// server, which the tunnel bridges to as well, demands Digest, the relay is told verifiers, and the description carries
/// `auth`. A LAN-only broadcast stays the secret link. Nothing here ever logs, stores or sends a password in clear.
/// </summary>
internal sealed partial class BroadcastProducerService
{
    /// <summary>The pair an outsider answers with, as the person chose it; null for "none".</summary>
    private ExchangeRelayCredentials? NewLinkPair(out bool generated)
    {
        generated = true;
        switch (Settings.Choice)
        {
            case BroadcastLinkChoice.None:
                return null;
            case BroadcastLinkChoice.Custom when _passwords.Load() is { } saved:
                generated = false;
                return new(BroadcastLinkPassword.LinkUser, saved);
            default:
                // "Set by the person" with no password in the secret store (a restored backup) falls back to a
                // generated one until the person types it again (item R, Storage).
                return new(BroadcastLinkPassword.LinkUser, BroadcastLinkPassword.Generate());
        }
    }

    /// <summary>True when a broadcast started now would carry a RELAY or TUNNEL endpoint - the same conditions the exchange half checks.</summary>
    private bool WillAnnounce() =>
        _exchange.PublicEndpoint is not null && _exchange.MayBroadcast && _exchange.ServerSeals && !_exchange.OwnKeyMissing
        && (_exchange.Features.Contains("relay", StringComparer.Ordinal) || _exchange.Features.Contains("tunnel", StringComparer.Ordinal));

    /// <summary>
    /// Makes the pair set of one broadcast true on every endpoint this program runs: protection on or off, the LAN
    /// server's credentials and the verifiers the relay is told. PBKDF2 runs once per pair, off the caller's thread.
    /// </summary>
    private async Task ApplyProtectionAsync(BroadcastRunningSource state)
    {
        state.Protected = state.LinkPair is not null && (state.Announced || WillAnnounce());
        ExchangeRelayCredentials[] pairs = state.Protected ? [state.OwnPair, state.LinkPair!] : [];
        state.Verifiers = pairs.Length == 0 ? [] : await Task.Run(() => VerifiersOf(state, pairs)).ConfigureAwait(false);
        state.LanServer?.SetCredentials(pairs);
    }

    private static IReadOnlyList<BroadcastVerifier> VerifiersOf(BroadcastRunningSource state, ExchangeRelayCredentials[] pairs)
    {
        var result = new List<BroadcastVerifier>(pairs.Length);
        foreach (var pair in pairs)
        {
            BroadcastVerifier? cached;
            lock (state.VerifierCache)
            {
                state.VerifierCache.TryGetValue(pair, out cached);
            }

            if (cached is null)
            {
                cached = BroadcastVerifier.Create(pair);
                lock (state.VerifierCache)
                {
                    state.VerifierCache[pair] = cached;
                }
            }

            result.Add(cached);
        }

        // A revoked pair's verifier must not outlive it in memory.
        lock (state.VerifierCache)
        {
            foreach (var stale in state.VerifierCache.Keys.Except(pairs).ToArray())
            {
                state.VerifierCache.Remove(stale);
            }
        }

        return result;
    }

    /// <summary>The plan, the description and the pair set of a live broadcast as they are now.</summary>
    private void RebuildPlan(BroadcastRunningSource state)
    {
        var online = state.ExchangeStarted;
        state.Plan = PlanOf(state, online ? _exchange.PublicEndpoint : null,
            online && _exchange.Features.Contains("relay"), online && _exchange.Features.Contains("tunnel"),
            state.Sealing is null ? BroadcastSealMode.None : BroadcastSealMode.Record);
        state.DescriptorJson = BroadcastDescriptorWriter.Write(state.Plan);
    }

    // --- the person's choice, on the air or not ------------------------------------

    /// <summary>
    /// Records the choice (and, for "set by me", the password in the secret store) and applies it to every broadcast on
    /// the air: the previous link pair stops working at once (item R, A change on air).
    /// </summary>
    internal async Task SetLinkChoiceAsync(BroadcastLinkChoice choice, string? customPassword)
    {
        if (customPassword is not null)
        {
            _passwords.Save(customPassword);
        }

        await UpdateSettingsAsync(settings => settings with { LinkChoice = BroadcastProducerSettings.LinkChoiceName(choice) })
            .ConfigureAwait(false);
        await ChangeLinkAsync(renew: false).ConfigureAwait(false);
    }

    /// <summary>A new generated password for every broadcast on the air; the old one is revoked the same way a change of choice revokes it.</summary>
    internal Task RenewLinkPasswordAsync() => ChangeLinkAsync(renew: true);

    private async Task ChangeLinkAsync(bool renew)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try
        {
            List<BroadcastRunningSource> states;
            lock (_gate)
            {
                states = [.. _running.Values];
            }

            var relayTold = true;
            foreach (var state in states)
            {
                var keepGenerated = !renew && Settings.Choice == BroadcastLinkChoice.Generated
                    && state.LinkPair is not null && state.LinkIsGenerated;
                if (!keepGenerated)
                {
                    state.LinkPair = NewLinkPair(out var generated);
                    state.LinkIsGenerated = generated;
                }

                await ApplyProtectionAsync(state).ConfigureAwait(false);
                RebuildPlan(state);
                if (state.ExchangeStarted)
                {
                    using var timeout = new CancellationTokenSource(EndWriteTimeout);
                    // S5-3: the previous password keeps working on the relay until it is told; never report it revoked when it was not.
                    relayTold &= await PostRecordAsync(state, "broadcast-update", timeout.Token).ConfigureAwait(false);
                }
            }

            if (states.Count > 0)
            {
                await PublishDriveAsync().ConfigureAwait(false);
                Report(relayTold ? "BroadcastLinkChanged" : "ExchangeUnavailable");
            }
        }
        finally { _sourceOperations.Release(); }
    }

    /// <summary>
    /// The password the person hands over with the link of a running broadcast; null when nothing asks for one - the person
    /// chose "none", or the broadcast has no exchange endpoint yet and stays the secret link (item M).
    /// </summary>
    internal string? LinkPassword(BroadcastSource source)
    {
        lock (_gate)
        {
            return _running.TryGetValue(source, out var state) && state.Protected ? state.LinkPair?.Password : null;
        }
    }
}
