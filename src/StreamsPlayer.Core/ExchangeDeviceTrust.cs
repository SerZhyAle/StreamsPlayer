using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>What a key seen for a device or a grant means against the one first seen (`DEVICE-EXCHANGE` items DG and DJ).</summary>
public enum ExchangeKeyObservation
{
    /// <summary>The key is the one first seen.</summary>
    Trusted,
    /// <summary>First time this id is seen: its key is now the trusted one (trust on first use).</summary>
    NewlyTrusted,
    /// <summary>Another key for an id that already has one. A key lives as long as its id, so this is never legitimate.</summary>
    KeyChanged
}

/// <summary>The persisted shape of the trust of one account entry: id to unpadded base64url point.</summary>
public sealed record ExchangeDeviceTrustData(
    Dictionary<string, string>? Devices = null,
    Dictionary<string, string>? Grants = null);

/// <summary>
/// The keys one account entry has seen: per `deviceId` (item DG) and, for a guest that knows none of the
/// owner's devices, per `grantId` (item DJ). First sight trusts; a different key afterwards is
/// <see cref="ExchangeKeyObservation.KeyChanged"/> and never replaces the trusted one - only forgetting the
/// device (its removal from the account, which also drops the kept key) lets a new key in. Pure state: the
/// application decides where it is stored.
/// </summary>
public sealed class ExchangeDeviceTrust
{
    private readonly Dictionary<string, byte[]> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _grants = new(StringComparer.Ordinal);

    /// <summary>Whether anything changed since the last <see cref="ToData"/>; the store saves only then.</summary>
    public bool IsDirty { get; private set; }

    public ExchangeKeyObservation ObserveDevice(string deviceId, ReadOnlySpan<byte> point) => Observe(_devices, deviceId, point);

    public ExchangeKeyObservation ObserveGrant(string grantId, ReadOnlySpan<byte> point) => Observe(_grants, grantId, point);

    public byte[]? TrustedDevice(string deviceId) => _devices.TryGetValue(deviceId, out var point) ? point : null;

    /// <summary>The ids of every device whose key is held.</summary>
    public IReadOnlyCollection<string> DeviceIds => _devices.Keys;

    public byte[]? TrustedGrant(string grantId) => _grants.TryGetValue(grantId, out var point) ? point : null;

    /// <summary>Drops the kept key of a device that left the account (`revoke`); true when there was one.</summary>
    public bool ForgetDevice(string deviceId) => Forget(_devices, deviceId);

    /// <summary>Drops the kept key of a grant that was withdrawn.</summary>
    public bool ForgetGrant(string grantId) => Forget(_grants, grantId);

    public ExchangeDeviceTrustData ToData()
    {
        IsDirty = false;
        return new ExchangeDeviceTrustData(
            _devices.ToDictionary(pair => pair.Key, pair => ExchangeBase64Url.Encode(pair.Value), StringComparer.Ordinal),
            _grants.ToDictionary(pair => pair.Key, pair => ExchangeBase64Url.Encode(pair.Value), StringComparer.Ordinal));
    }

    /// <summary>Rebuilds the trust from its stored shape; an entry that is not a P-256 point is dropped, never trusted blindly.</summary>
    public static ExchangeDeviceTrust FromData(ExchangeDeviceTrustData? data)
    {
        var trust = new ExchangeDeviceTrust();
        Load(trust._devices, data?.Devices);
        Load(trust._grants, data?.Grants);
        return trust;
    }

    private ExchangeKeyObservation Observe(Dictionary<string, byte[]> book, string id, ReadOnlySpan<byte> point)
    {
        if (!book.TryGetValue(id, out var kept))
        {
            book[id] = point.ToArray();
            IsDirty = true;
            return ExchangeKeyObservation.NewlyTrusted;
        }

        return CryptographicOperations.FixedTimeEquals(kept, point) ? ExchangeKeyObservation.Trusted : ExchangeKeyObservation.KeyChanged;
    }

    private bool Forget(Dictionary<string, byte[]> book, string id)
    {
        if (!book.Remove(id))
        {
            return false;
        }

        IsDirty = true;
        return true;
    }

    private static void Load(Dictionary<string, byte[]> book, Dictionary<string, string>? stored)
    {
        foreach (var (id, text) in stored ?? [])
        {
            if (ExchangeBase64Url.TryDecode(text, out var point) && ExchangeE2eKeyPair.IsValidPublicPoint(point))
            {
                book[id] = point;
            }
        }
    }
}
