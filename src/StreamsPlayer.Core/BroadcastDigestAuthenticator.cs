using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// One accepted credential pair as the authenticator holds it: the user and H(user:realm:password) per algorithm, never
/// the password. Opaque to a caller, who keeps it to ask <see cref="BroadcastDigestAuthenticator.IsCurrent"/> later.
/// </summary>
public sealed class BroadcastAuthGrant
{
    internal BroadcastAuthGrant(string user, string ha1Sha256, string ha1Md5)
    {
        User = user;
        Ha1Sha256 = ha1Sha256;
        Ha1Md5 = ha1Md5;
    }

    public string User { get; }

    internal string Ha1Sha256 { get; }

    internal string Ha1Md5 { get; }

    internal string Ha1(string algorithm) => algorithm == BroadcastDigest.Sha256 ? Ha1Sha256 : Ha1Md5;

    public override string ToString() => nameof(BroadcastAuthGrant) + " { " + User + " }";
}

/// <param name="Allowed">True when no authentication is required, or the answer was right.</param>
/// <param name="AuthenticatedUser">The user the answer proved; null when none was asked for or the request was refused.</param>
/// <param name="Challenge">The <c>WWW-Authenticate</c> field values to send with a 401 - the SHA-256 one first - and none when allowed.</param>
public sealed record AuthResult(bool Allowed, string? AuthenticatedUser, IReadOnlyList<string> Challenge)
{
    /// <summary>The credential an allowed answer proved; null when authentication was not required.</summary>
    public BroadcastAuthGrant? Grant { get; init; }

    /// <summary>
    /// Credentials were presented and refused, as opposed to none presented or a right answer to an old nonce. It is
    /// the only case a caller slows down: a guess, not a client that has yet to be told the realm.
    /// </summary>
    public bool CredentialsRefused { get; init; }
}

/// <summary>
/// The producer's HTTP Digest check (LIVE-BROADCAST item M, RFC 7616) for a protected broadcast: realm
/// <c>fms-broadcast</c>, two challenges (SHA-256 first, MD5 beside it), <c>qop=auth</c>, nothing finer than 401.
/// </summary>
/// <remarks>
/// A nonce is stateless - a timestamp, random bytes and an HMAC under a key made at construction - so a challenge costs
/// the server nothing and an unauthenticated peer can fill no table. Only an answer that proved the password enters the
/// bounded <c>nc</c> table, which refuses a repeated or lower count (a captured answer replayed). A user that is not held
/// pays the same hashing as a wrong password. The password is not kept, only H(user:realm:password) per algorithm.
/// </remarks>
public sealed class BroadcastDigestAuthenticator
{
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);

    private const int NonceBytes = 32;
    private const int NonceTagBytes = 16;
    private const long FutureToleranceMilliseconds = 5_000;
    private const int DefaultReplayCapacity = 2048;

    private readonly TimeProvider _time;
    private readonly byte[] _nonceKey = RandomNumberGenerator.GetBytes(32);
    private readonly BroadcastAuthGrant _unknownUser;
    private readonly int _replayCapacity;
    private readonly object _swapGate = new();
    private readonly object _replayGate = new();
    private readonly Dictionary<string, NonceCount> _counts = new(StringComparer.Ordinal);
    private PairSet _pairs = new(0, new Dictionary<string, BroadcastAuthGrant>(StringComparer.Ordinal));
    private long _evictedThroughMilliseconds = long.MinValue;

    public BroadcastDigestAuthenticator(TimeProvider? timeProvider = null)
        : this(timeProvider, DefaultReplayCapacity)
    {
    }

    internal BroadcastDigestAuthenticator(TimeProvider? timeProvider, int replayCapacity)
    {
        _time = timeProvider ?? TimeProvider.System;
        _replayCapacity = Math.Max(1, replayCapacity);
        // The stand-in for a user that is not held: random hashes of the real length, so its check costs the same.
        _unknownUser = new BroadcastAuthGrant(
            "", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
    }

    /// <summary>True while at least one pair is set. With none, every request is allowed and no challenge is sent.</summary>
    public bool Required => Volatile.Read(ref _pairs).Entries.Count > 0;

    /// <summary>Counts every change of the pair set, so a holder of the state can tell that it moved.</summary>
    public long Revision => Volatile.Read(ref _pairs).Revision;

    /// <summary>Raised after a swap that changed the set (not for one that set the same pairs again), on the caller's thread.</summary>
    public event Action? PairsChanged;

    /// <summary>
    /// Replaces the whole set at once; an empty list means no authentication. The old password stops working for the
    /// next request, and <see cref="IsCurrent"/> turns false for a grant whose user or password is gone.
    /// </summary>
    /// <exception cref="ArgumentException">A user outside <see cref="BroadcastVerifier.IsValidUser"/>, an empty password or a user twice; nothing is swapped.</exception>
    public void SetPairs(IReadOnlyList<ExchangeRelayCredentials> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        lock (_swapGate)
        {
            var current = Volatile.Read(ref _pairs);
            var built = new Dictionary<string, BroadcastAuthGrant>(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                if (pair is null || !BroadcastVerifier.IsValidUser(pair.User) || !BroadcastVerifier.IsEncodablePassword(pair.Password))
                {
                    throw new ArgumentException("A pair is missing, or its user or password cannot be answered with.", nameof(pairs));
                }

                var sha256 = BroadcastDigest.Ha1(BroadcastDigest.Sha256, pair.User, BroadcastDigest.Realm, pair.Password);
                var md5 = BroadcastDigest.Ha1(BroadcastDigest.Md5, pair.User, BroadcastDigest.Realm, pair.Password);
                // An unchanged pair keeps its grant, so a listener admitted under it is not revoked by an unrelated change.
                var grant = current.Entries.TryGetValue(pair.User, out var held) && held.Ha1Sha256 == sha256 && held.Ha1Md5 == md5
                    ? held
                    : new BroadcastAuthGrant(pair.User, sha256, md5);
                if (!built.TryAdd(pair.User, grant))
                {
                    throw new ArgumentException("A user appears twice.", nameof(pairs));
                }
            }

            if (built.Count == current.Entries.Count && built.All(entry => current.Entries.TryGetValue(entry.Key, out var held) && ReferenceEquals(held, entry.Value)))
            {
                return;
            }

            Volatile.Write(ref _pairs, new PairSet(current.Revision + 1, built));
        }

        PairsChanged?.Invoke();
    }

    /// <summary>True while <paramref name="grant"/>'s user is still held with the same password.</summary>
    public bool IsCurrent(BroadcastAuthGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Volatile.Read(ref _pairs).Entries.TryGetValue(grant.User, out var held) && ReferenceEquals(held, grant);
    }

    /// <param name="method">The request method as received; it is part of what the client hashed.</param>
    /// <param name="requestTarget">The request-target as received, query included; the answer's <c>uri</c> must be this exact text.</param>
    /// <param name="authorizationHeader">The value of the request's <c>Authorization</c> field, or null when it has none.</param>
    public AuthResult Check(string method, string requestTarget, string? authorizationHeader)
    {
        var pairs = Volatile.Read(ref _pairs);
        if (pairs.Entries.Count == 0)
        {
            return new AuthResult(true, null, []);
        }

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return Challenged(now, stale: false, refused: false);
        }

        var answer = BroadcastDigest.ParseAnswer(authorizationHeader);
        if (answer is null
            || !string.Equals(answer.Uri, requestTarget, StringComparison.Ordinal)
            || !TryReadNonce(answer.Nonce, out var issued)
            || issued > now + FutureToleranceMilliseconds)
        {
            return Challenged(now, stale: false, refused: true);
        }

        var held = pairs.Entries.TryGetValue(answer.User, out var entry);
        var expected = BroadcastDigest.Response(
            answer.Algorithm, (entry ?? _unknownUser).Ha1(answer.Algorithm), method, answer.Uri, answer.Nonce, answer.NcText, answer.Cnonce);
        var matches = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(answer.Response));
        if (!(held & matches))
        {
            return Challenged(now, stale: false, refused: true);
        }

        // The password is proved from here on, so what follows may say more than "no": the nonce is spent, not the client wrong.
        if (now - issued > (long)NonceLifetime.TotalMilliseconds || !RegisterCount(answer.Nonce, issued, answer.Nc, now))
        {
            return Challenged(now, stale: true, refused: false);
        }

        return new AuthResult(true, entry!.User, []) { Grant = entry };
    }

    private AuthResult Challenged(long now, bool stale, bool refused)
    {
        var nonce = NewNonce(now);
        var suffix = stale ? ", stale=true" : "";
        return new AuthResult(false, null,
        [
            $"Digest realm=\"{BroadcastDigest.Realm}\", qop=\"auth\", algorithm={BroadcastDigest.Sha256}, nonce=\"{nonce}\"{suffix}",
            $"Digest realm=\"{BroadcastDigest.Realm}\", qop=\"auth\", algorithm={BroadcastDigest.Md5}, nonce=\"{nonce}\"{suffix}"
        ])
        { CredentialsRefused = refused };
    }

    private string NewNonce(long nowMilliseconds)
    {
        Span<byte> raw = stackalloc byte[NonceBytes];
        BinaryPrimitives.WriteInt64BigEndian(raw, nowMilliseconds);
        RandomNumberGenerator.Fill(raw[8..16]);
        Span<byte> tag = stackalloc byte[32];
        HMACSHA256.HashData(_nonceKey, raw[..16], tag);
        tag[..NonceTagBytes].CopyTo(raw[16..]);
        return ExchangeBase64Url.Encode(raw);
    }

    private bool TryReadNonce(string nonce, out long issuedMilliseconds)
    {
        issuedMilliseconds = 0;
        if (!ExchangeBase64Url.TryDecode(nonce, out var raw) || raw.Length != NonceBytes)
        {
            return false;
        }

        Span<byte> tag = stackalloc byte[32];
        HMACSHA256.HashData(_nonceKey, raw.AsSpan(0, 16), tag);
        if (!CryptographicOperations.FixedTimeEquals(tag[..NonceTagBytes], raw.AsSpan(16, NonceTagBytes)))
        {
            return false;
        }

        issuedMilliseconds = BinaryPrimitives.ReadInt64BigEndian(raw);
        return true;
    }

    /// <summary>
    /// Records <paramref name="count"/> for a nonce whose answer was right, or says it is not above the last one seen.
    /// A full table drops what it must, oldest nonce first, and then no nonce issued up to that one is honoured: with its
    /// count forgotten it could be replayed, so it is answered as stale and the client takes a new one.
    /// </summary>
    private bool RegisterCount(string nonce, long issued, long count, long now)
    {
        lock (_replayGate)
        {
            if (issued <= _evictedThroughMilliseconds)
            {
                return false;
            }

            if (_counts.TryGetValue(nonce, out var seen))
            {
                if (count <= seen.Last)
                {
                    return false;
                }

                _counts[nonce] = seen with { Last = count };
                return true;
            }

            if (_counts.Count >= _replayCapacity)
            {
                MakeRoom(now);
                if (issued <= _evictedThroughMilliseconds)
                {
                    return false;
                }
            }

            _counts[nonce] = new NonceCount(issued, count);
            return true;
        }
    }

    private void MakeRoom(long now)
    {
        var lifetime = (long)NonceLifetime.TotalMilliseconds;
        foreach (var expired in _counts.Where(entry => now - entry.Value.Issued > lifetime).Select(entry => entry.Key).ToList())
        {
            _counts.Remove(expired);
        }

        if (_counts.Count < _replayCapacity)
        {
            return;
        }

        _evictedThroughMilliseconds = Math.Max(_evictedThroughMilliseconds, _counts.Values.Min(value => value.Issued));
        foreach (var unreachable in _counts.Where(entry => entry.Value.Issued <= _evictedThroughMilliseconds).Select(entry => entry.Key).ToList())
        {
            _counts.Remove(unreachable);
        }
    }

    private sealed record PairSet(long Revision, IReadOnlyDictionary<string, BroadcastAuthGrant> Entries);

    private readonly record struct NonceCount(long Issued, long Last);
}
