namespace StreamsPlayer.Core;

/// <summary>Where a row of the common table comes from (<c>DEVICE-EXCHANGE</c> item CZ, column "Kind").</summary>
public enum ExchangeFailureKind
{
    /// <summary>A <c>refused.reason</c> of section 7.10: the server answered.</summary>
    Wire,

    /// <summary>A reason of a <c>cast-result</c> (item R).</summary>
    Cast,

    /// <summary>A class this program names itself, with no wire reason.</summary>
    Local
}

/// <summary>
/// One row of the family's table of connection messages (item CZ) as this product raises it (SP-0225). A row says
/// what happened (its text) and names the step of the ladder that says what to try. <see cref="Walked"/> rows have
/// no reason of their own on the wire - the failure is walked from the step to the end (item CY).
/// </summary>
public sealed record ExchangeFailureRow(string Code, ExchangeFailureKind Kind, ExchangeDiagnosisStep? Step, bool Walked,
    string MessageKey);

public static class ExchangeFailureRows
{
    public static readonly ExchangeFailureRow BadCredentials = new("bad-credentials", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Credentials, false, "ExchangeBadCredentials");

    /// <summary>The same refusal for a one-time code (the pairing code), which the table words separately.</summary>
    public static readonly ExchangeFailureRow BadCredentialsCode = new("bad-credentials-code", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Credentials, false, "ExchangeBadCredentialsCode");

    public static readonly ExchangeFailureRow DeviceRevoked = new("device-revoked", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.DeviceRemoved, false, "ExchangeDeviceRevoked");

    public static readonly ExchangeFailureRow SignedOut = new("signed-out", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Credentials, false, "ExchangeSignedOut");

    public static readonly ExchangeFailureRow VersionUnsupported = new("version-unsupported", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangeVersionUnsupported");

    public static readonly ExchangeFailureRow TlsRequired = new("tls-required", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangeTlsRequired");

    public static readonly ExchangeFailureRow IdCollision = new("id-collision", ExchangeFailureKind.Wire,
        null, false, "ExchangeIdCollision");

    public static readonly ExchangeFailureRow Capacity = new("capacity", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangeCapacity");

    public static readonly ExchangeFailureRow PortUnavailable = new("port-unavailable", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangePortUnavailable");

    public static readonly ExchangeFailureRow Unavailable = new("unavailable", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.PcAsleep, false, "ExchangeUnavailable");

    public static readonly ExchangeFailureRow RateLimited = new("rate-limited", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.SignInRate, false, "ExchangeRateLimited");

    public static readonly ExchangeFailureRow PasswordPolicy = new("password-policy", ExchangeFailureKind.Wire,
        null, false, "ExchangePasswordPolicy");

    public static readonly ExchangeFailureRow LimitReached = new("limit-reached", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangeLimitReached");

    public static readonly ExchangeFailureRow NotPermitted = new("not-permitted", ExchangeFailureKind.Wire,
        null, false, "ExchangeNotPermitted");

    /// <summary>An unknown <c>refused.reason</c> is shown as a generic refusal (section 7.10), step "operator".</summary>
    public static readonly ExchangeFailureRow Refused = new("refused", ExchangeFailureKind.Wire,
        ExchangeDiagnosisStep.Operator, false, "ExchangeRefused");

    public static readonly ExchangeFailureRow CastDeclined = new("declined", ExchangeFailureKind.Cast,
        null, false, "BroadcastCastDeclined");

    public static readonly ExchangeFailureRow CastTimeout = new("timeout", ExchangeFailureKind.Cast,
        null, false, "BroadcastCastTimeout");

    public static readonly ExchangeFailureRow CastUnsupported = new("unsupported", ExchangeFailureKind.Cast,
        null, false, "BroadcastCastUnsupported");

    /// <summary>"Cannot reach the server": nothing answered at all. Walked from the first step.</summary>
    public static readonly ExchangeFailureRow Unreachable = new("unreachable-server", ExchangeFailureKind.Local,
        ExchangeDiagnosisStep.PcAsleep, true, "ExchangeUnreachable");

    /// <summary>A stream that closed with no frame after this product's own envelope (item AN).</summary>
    public static readonly ExchangeFailureRow Unanswered = new("unanswered", ExchangeFailureKind.Local,
        ExchangeDiagnosisStep.Operator, true, "ExchangeUnanswered");

    public static readonly ExchangeFailureRow TlsFailed = new("tls-failed", ExchangeFailureKind.Local,
        ExchangeDiagnosisStep.DeviceClock, true, "ExchangeTlsFailed");

    public static readonly ExchangeFailureRow CertificateChanged = new("certificate-changed", ExchangeFailureKind.Local,
        ExchangeDiagnosisStep.Certificate, false, "ExchangeCertificateChanged");

    public static readonly ExchangeFailureRow EmptyCredential = new("empty-credential", ExchangeFailureKind.Local,
        null, false, "ExchangeEmptyCredential");

    /// <summary>A higher <c>FMSEXCH</c> prefix or descriptor schema (amendment of 2026-10-17); no update is demanded.</summary>
    public static readonly ExchangeFailureRow FormatUnsupported = new("format-unsupported", ExchangeFailureKind.Local,
        null, false, "ExchangeFormatUnsupported");

    public static IReadOnlyList<ExchangeFailureRow> All { get; } = Array.AsReadOnly(new[]
    {
        BadCredentials, BadCredentialsCode, DeviceRevoked, SignedOut, VersionUnsupported, TlsRequired, IdCollision,
        Capacity, PortUnavailable, Unavailable, RateLimited, PasswordPolicy, LimitReached, NotPermitted, Refused,
        CastDeclined, CastTimeout, CastUnsupported, Unreachable, Unanswered, TlsFailed, CertificateChanged,
        EmptyCredential, FormatUnsupported
    });

    /// <summary>The row whose text a status line shows under <paramref name="key"/>; null for a key that is not a failure.</summary>
    public static ExchangeFailureRow? ByMessageKey(string? key) =>
        key is null ? null : All.FirstOrDefault(row => string.Equals(row.MessageKey, key, StringComparison.Ordinal));

    public static ExchangeFailureRow? ByCode(string? code) =>
        code is null ? null : All.FirstOrDefault(row => string.Equals(row.Code, code, StringComparison.Ordinal));

    /// <summary>The row of a <c>refused.reason</c>; an unknown or absent reason is the generic refusal.</summary>
    public static ExchangeFailureRow OfWire(string? reason, bool oneTimeCode = false) => reason switch
    {
        "bad-credentials" => oneTimeCode ? BadCredentialsCode : BadCredentials,
        "device-revoked" => DeviceRevoked,
        "signed-out" => SignedOut,
        "version-unsupported" => VersionUnsupported,
        "tls-required" => TlsRequired,
        "id-collision" => IdCollision,
        "capacity" => Capacity,
        "port-unavailable" => PortUnavailable,
        "unavailable" => Unavailable,
        "rate-limited" => RateLimited,
        "password-policy" => PasswordPolicy,
        "limit-reached" => LimitReached,
        "not-permitted" => NotPermitted,
        _ => Refused
    };

    /// <summary>The row of a <c>cast-result</c> reason (item R); an unknown reason is the generic one.</summary>
    public static ExchangeFailureRow OfCast(string? reason) => reason switch
    {
        "declined" => CastDeclined,
        "timeout" => CastTimeout,
        "unsupported" => CastUnsupported,
        "unavailable" => Unavailable,
        _ => Unavailable
    };
}
