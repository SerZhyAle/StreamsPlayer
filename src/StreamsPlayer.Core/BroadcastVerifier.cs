using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// One entry of the envelope's <c>verifiers</c> member (DEVICE-EXCHANGE item BF, empty list item DV): what the relay
/// checks a listener's Basic credentials against, so the relay never holds a password. Salt and hash are base64url
/// without padding.
/// </summary>
/// <remarks>
/// Making one runs PBKDF2 at 600000 rounds, about 100 to 300 ms of CPU: call <see cref="Create"/> and
/// <see cref="CreateAll"/> off the UI thread and keep the result until the pair changes - the same pair needs
/// no second derivation, and this type deliberately caches nothing.
/// </remarks>
public sealed record BroadcastVerifier(string User, string Salt, string Hash)
{
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>The relay measures the user in UTF-8 bytes, so the bound is bytes, not characters.</summary>
    public const int MaximumUserBytes = 64;

    /// <summary>The relay refuses a frame that carries more entries.</summary>
    public const int MaximumEntries = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // A salt and a hash are values to test a guess against; a record prints every member, so none of them may reach a log line.
    public override string ToString() => nameof(BroadcastVerifier) + " { redacted }";

    /// <summary>1 to 64 UTF-8 bytes, no ':' (a Basic user-id cannot hold one), no control character - the relay's bounds.</summary>
    public static bool IsValidUser(string? user) =>
        !string.IsNullOrEmpty(user)
        && user.Length <= MaximumUserBytes
        && !user.Contains(':')
        && BroadcastLinkPassword.TryCountPlainText(user, out _)
        && Encoding.UTF8.GetByteCount(user) <= MaximumUserBytes;

    /// <summary>A non-empty text with no unpaired surrogate: one that has exactly one UTF-8 spelling to hash.</summary>
    internal static bool IsEncodablePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(password);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <param name="salt">Exactly 16 bytes, or empty for a fresh random salt; a given salt exists for known-answer tests.</param>
    /// <exception cref="ArgumentException">A user the relay would refuse, an empty or unencodable password, or a salt of the wrong size.</exception>
    public static BroadcastVerifier Create(ExchangeRelayCredentials pair, ReadOnlySpan<byte> salt = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsValidUser(pair.User))
        {
            throw new ArgumentException("The user is outside the relay's bounds for a verifier.", nameof(pair));
        }

        if (!IsEncodablePassword(pair.Password))
        {
            throw new ArgumentException("A verifier needs a non-empty password that is valid Unicode text.", nameof(pair));
        }

        if (!salt.IsEmpty && salt.Length != SaltBytes)
        {
            throw new ArgumentException("The salt is " + SaltBytes + " bytes.", nameof(salt));
        }

        var saltBytes = salt.IsEmpty ? RandomNumberGenerator.GetBytes(SaltBytes) : salt.ToArray();
        var password = StrictUtf8.GetBytes(pair.Password);
        try
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, HashBytes);
            return new BroadcastVerifier(pair.User, ExchangeBase64Url.Encode(saltBytes), ExchangeBase64Url.Encode(hash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    /// <summary>One verifier per pair, in order. A user that appears twice could not be revoked alone, so it is refused.</summary>
    /// <exception cref="ArgumentException">A duplicate user, more than <see cref="MaximumEntries"/> pairs, or any pair <see cref="Create"/> refuses.</exception>
    public static IReadOnlyList<BroadcastVerifier> CreateAll(IEnumerable<ExchangeRelayCredentials> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var list = pairs.ToList();
        if (list.Count > MaximumEntries)
        {
            throw new ArgumentException("The relay accepts at most " + MaximumEntries + " verifiers.", nameof(pairs));
        }

        var users = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in list)
        {
            if (pair is null || !users.Add(pair.User))
            {
                throw new ArgumentException("A pair is missing or a user appears twice.", nameof(pairs));
            }
        }

        return [.. list.Select(pair => Create(pair))];
    }

    /// <summary>
    /// The envelope's <c>verifiers</c> member: an array of <c>{ "user", "salt", "hash" }</c>. An empty list is written as
    /// <c>[]</c> and not left out - on <c>broadcast-update</c> a missing member means "unchanged" to a relay (item DV).
    /// </summary>
    public static JsonArray ToEnvelopeMember(IEnumerable<BroadcastVerifier> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        var array = new JsonArray();
        foreach (var verifier in verifiers)
        {
            array.Add(new JsonObject { ["user"] = verifier.User, ["salt"] = verifier.Salt, ["hash"] = verifier.Hash });
        }

        return array;
    }
}
