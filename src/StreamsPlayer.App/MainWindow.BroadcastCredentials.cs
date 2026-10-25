using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0215 requirement 7: the receiver's password rule of a protected broadcast (LIVE-BROADCAST item M,
/// DEVICE-EXCHANGE 20 item BF) - one prompt on a play the person asked for, the remembered password on
/// every reconnect, a refusal that forgets the stale one so the next play is one new prompt, and no
/// account read, written or enrolled anywhere in it.
/// </summary>
public partial class MainWindow
{
    private ExchangeBroadcastPasswordStore? _broadcastPasswordStore;

    /// <summary>The credentials the current audio session answers a protected relay's challenge with.</summary>
    private ExchangeRelayCredentials? _playingBroadcastCredentials;

    private ExchangeBroadcastPasswordStore BroadcastPasswords =>
        _broadcastPasswordStore ??= new ExchangeBroadcastPasswordStore(AppPaths.DataDirectory);

    /// <summary>Item M: whether this channel's descriptor carries the password challenge this build answers.</summary>
    private static bool IsPasswordProtected(StreamChannel channel) =>
        channel.FastMediaSorterBroadcast?.Auth is { } auth &&
        string.Equals(auth.Scheme, ExchangeBroadcastAuth.SupportedScheme, StringComparison.Ordinal);

    /// <summary>
    /// The credentials a protected play answers with - the remembered password when there is one, otherwise
    /// the one prompt, and only on a play the person asked for: a reconnect, a quiet resume and a hunt play
    /// on what is remembered and report the relay's refusal when it is stale. The password is remembered
    /// with the channel under the platform secret store, never in the account, a log or a diagnostic bundle.
    /// </summary>
    /// <paramref name="declined"/> is true when the person dismissed the one prompt: the play stops there,
    /// with no failure record - the broadcast asked, the person did not answer.
    private ExchangeRelayCredentials? ResolveBroadcastCredentials(StreamChannel channel, bool interactive, out bool declined)
    {
        declined = false;
        if (channel.FastMediaSorterBroadcast?.Auth is not { } auth ||
            !string.Equals(auth.Scheme, ExchangeBroadcastAuth.SupportedScheme, StringComparison.Ordinal))
        {
            return null;
        }

        if (!channel.FastMediaSorterBroadcast.PlaybackAttemptEndpoints(channel.Url)
            .Any(endpoint => ExchangeBroadcastKeys.Decide(endpoint).RelayOptions(endpoint.Url) is not null))
        {
            // Unsupported protected LAN/tunnel routes report their own refusal without asking for an unusable password.
            return null;
        }

        var remembered = BroadcastPasswords.Load(channel.Id);
        if (remembered is { Length: > 0 })
        {
            return new ExchangeRelayCredentials(auth.User, remembered);
        }

        if (!interactive)
        {
            return null;
        }

        // S12-1: DialogOwner, not this - in the compact panel the catalog is hidden and a prompt owned by it has no home (SP-0182).
        var dialog = new BroadcastPasswordWindow(channel.Title) { Owner = DialogOwner };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Password))
        {
            BroadcastPasswords.Save(channel.Id, dialog.Password);
            return new ExchangeRelayCredentials(auth.User, dialog.Password);
        }

        declined = true;
        return null;
    }

    /// <summary>The re-resolve a hand retry asks for; the resolver of a session's <see cref="BroadcastCredentialsSession"/>.</summary>
    private ExchangeRelayCredentials? ResolveBroadcastCredentialsForRetry(Guid channelId)
    {
        var channel = (_stateCommitter?.Requested ?? _state).Channels.FirstOrDefault(item => item.Id == channelId);
        return channel is null ? null : ResolveBroadcastCredentials(channel, interactive: true, out _);
    }

    private void ForgetBroadcastPassword(Guid channelId) => BroadcastPasswords.Delete(channelId);

    /// <summary>
    /// SP-0226: the keys a sealed link carries in its endpoints, kept under the secret store the moment the link is read,
    /// because the channel made from it keeps none of them (LIVE-BROADCAST item Q). Nothing is logged.
    /// </summary>
    private static void RememberLinkKeys(FastMediaSorterBroadcast? broadcast)
    {
        if (broadcast is not null)
        {
            ExchangeBroadcastKeys.RememberLink(broadcast.Endpoints, ExchangeLinkKeyStore.For(AppPaths.DataDirectory).Save);
        }
    }
}
