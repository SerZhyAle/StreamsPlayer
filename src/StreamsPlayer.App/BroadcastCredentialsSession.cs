using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0215 requirement 7: the protected-broadcast credentials one playback session answers with -
/// the pair resolved when the person asked for the channel, the re-resolve a hand retry asks for,
/// and the forgetting a refusal owes (the password the relay refused is the broadcaster's old one).
/// The password itself never leaves MainWindow's store and prompt; this object carries only the pair
/// for the session's own legs.
/// </summary>
internal sealed class BroadcastCredentialsSession(
    ExchangeRelayCredentials? credentials,
    Func<Guid, ExchangeRelayCredentials?> resolve,
    Action<Guid> forget)
{
    private readonly Func<Guid, ExchangeRelayCredentials?> _resolve = resolve;
    private readonly Action<Guid> _forget = forget;

    /// <summary>The pair this session's legs answer the relay's challenge with; null when none was given.</summary>
    public ExchangeRelayCredentials? Credentials { get; private set; } = credentials;

    /// <summary>
    /// The relay refused the pair this session sent: the stored password is stale, and keeping it would
    /// send the next play to a door that always refuses - so the next play, or its Retry, is the one new prompt.
    /// </summary>
    public void NoteRefused(Guid channelId) => _forget(channelId);

    /// <summary>The re-resolve of a hand retry: remembered first, the one prompt when the refusal just took the stale one away.</summary>
    public ExchangeRelayCredentials? ResolveForRetry(Guid channelId) => Credentials = _resolve(channelId);
}
