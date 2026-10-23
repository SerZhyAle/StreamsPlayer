namespace StreamsPlayer.Core;

/// <summary>
/// The relay's Basic credentials of a protected broadcast (LIVE-BROADCAST item M, DEVICE-EXCHANGE 20
/// item BF). The pair exists only in memory and under the platform secret store: it is never written
/// to a log, a diagnostic bundle or a URL this product logs or shows.
/// <para>SP-0222: a directory record hands an enrolled device the same pair (item BF for an own device, item BD
/// for a guest's grant), so a record that carries it holds this type and must print nothing of it.</para>
/// </summary>
public sealed record ExchangeRelayCredentials(string User, string Password)
{
    /// <summary>A pair outside these limits is not one this build answers a challenge with.</summary>
    public const int MaximumUserLength = 256;

    public const int MaximumPasswordLength = 1024;

    // A record prints every member, and the generated text of a record that holds this one would carry the
    // password into whatever formats it (an assertion message, a log line, a debugger tooltip).
    public override string ToString() => nameof(ExchangeRelayCredentials) + " { redacted }";

    /// <summary>
    /// A non-empty user and a non-empty password within the limits, or <c>null</c>: foreign input costs the
    /// pair, never the record and never the connection.
    /// </summary>
    public static ExchangeRelayCredentials? Create(string? user, string? password) =>
        user is { Length: > 0 and <= MaximumUserLength } && password is { Length: > 0 and <= MaximumPasswordLength }
            ? new ExchangeRelayCredentials(user, password)
            : null;
}

/// <summary>
/// SP-0215 requirement 7: the receiver's rules of a protected broadcast - the named refusals a leg
/// reports, the localization keys they map to, and the one Basic value both relay transports send.
/// </summary>
public static class ExchangeBroadcastAuth
{
    /// <summary>Item M: the only <c>auth.scheme</c> value this revision defines.</summary>
    public const string SupportedScheme = "password-v1";

    /// <summary>The relay answered the challenge and the leg held no password to answer with.</summary>
    public const string PasswordRequiredReason = "relay-password-required";

    /// <summary>The relay refused the pair the leg sent - a wrong password, or one the broadcaster changed.</summary>
    public const string PasswordRefusedReason = "relay-password-refused";

    public const string UnsupportedEndpointReason = "broadcast-auth-unsupported";

    public static string? MessageKey(string? reason) => reason switch
    {
        PasswordRequiredReason => "BroadcastPasswordRequired",
        PasswordRefusedReason => "BroadcastPasswordRefused",
        UnsupportedEndpointReason => "BroadcastProtectedEndpointUnsupported",
        _ => null
    };

    /// <summary>
    /// Item M's Basic answer to the relay's challenge - sent only over the endpoint's own verified TLS
    /// connection, so the trust rule of the endpoint (SP-0215 requirement 3) has always passed before
    /// these bytes leave this device.
    /// </summary>
    public static string BasicValue(ExchangeRelayCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials.User + ":" + credentials.Password));
    }
}
