using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>Why a sealed box was not opened (`DEVICE-EXCHANGE` item DH). Every one of them shows `sealed-failed`.</summary>
public enum ExchangeSealedFailure
{
    None,
    /// <summary>The box is not shaped as item DH writes it, or its version is not one this build reads.</summary>
    Malformed,
    /// <summary>No entry of the box names this device.</summary>
    NoEntry,
    /// <summary>`fromKey` is not the key this device trusts for `from` (item DG; for a guest, item DJ).</summary>
    UntrustedSender,
    /// <summary>The key, the context or the ciphertext does not authenticate.</summary>
    BadTag
}

/// <summary>The contexts of item DH. A reader passes the one it expects for the place the box was found in, never one read from the box.</summary>
public static class ExchangeSealedContext
{
    public static string Access(string resourceId) => "access:" + resourceId;
    public static string Grant(string grantId) => "grant:" + grantId;
    public static string Broadcast(string broadcastId) => "broadcast:" + broadcastId;
    public const string Clipboard = "clipboard";
}

/// <summary>One recipient entry of a box: the device it is for, the encapsulated key and the wrapped content key.</summary>
public sealed record ExchangeSealedBoxKey(string To, string Enc, string Wk);

/// <summary>The sealed box of item DH as it travels: every binary member is unpadded base64url.</summary>
public sealed record ExchangeSealedBox(
    int V, string From, string FromKey, string N, string Ct, IReadOnlyList<ExchangeSealedBoxKey> Keys)
{
    /// <summary>Item DH: a box addresses at most 40 recipients.</summary>
    public const int MaximumRecipients = 40;

    public const int CurrentVersion = 1;

    /// <summary>Reads a box member leniently about unknown keys and strictly about the shape; null when it is not a box.</summary>
    public static ExchangeSealedBox? TryRead(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            if (!element.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v)
                || Text(element, "from") is not { Length: > 0 } from
                || Text(element, "fromKey") is not { } fromKey
                || Text(element, "n") is not { } nonce
                || Text(element, "ct") is not { } ciphertext
                || !element.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var keys = new List<ExchangeSealedBoxKey>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || Text(entry, "to") is not { Length: > 0 } to
                    || Text(entry, "enc") is not { } enc
                    || Text(entry, "wk") is not { } wk)
                {
                    return null;
                }

                keys.Add(new ExchangeSealedBoxKey(to, enc, wk));
            }

            return new ExchangeSealedBox(v, from, fromKey, nonce, ciphertext, keys);
        }
        catch (InvalidOperationException)
        {
            // A lone-surrogate escape in a text member is not a box this build can hold.
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A device a box is sealed to: its id and its trusted public point.</summary>
public sealed record ExchangeSealedRecipient(string DeviceId, byte[] PublicPoint);

/// <summary>The result of opening a box: the payload bytes, or the one reason there are none. Nothing partial is ever returned.</summary>
public sealed record ExchangeSealedOpening(byte[]? Payload, ExchangeSealedFailure Failure)
{
    public bool Opened => Payload is not null;
}

/// <summary>
/// Seals and opens the box of `DEVICE-EXCHANGE` item DH: the payload under a fresh AES-256-GCM content key with
/// the box's context as associated data, and that content key to each recipient by HPKE Auth mode. The
/// server stores a box as an opaque member and never calls any of this.
/// </summary>
public static class ExchangeSealedBoxes
{
    private static readonly byte[] HpkeInfo = Encoding.ASCII.GetBytes("fmsx-e2e-v1");

    /// <summary>
    /// Seals <paramref name="payload"/> (the UTF-8 bytes of one JSON object) to the recipients, at most 40;
    /// the caller orders them most recently seen first, because a longer list is cut here, not chosen.
    /// </summary>
    public static ExchangeSealedBox Seal(
        ReadOnlySpan<byte> payload, string context, string senderDeviceId, ExchangeE2eKeyPair sender,
        IEnumerable<ExchangeSealedRecipient> recipients)
    {
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var aad = Encoding.UTF8.GetBytes(context);
        try
        {
            var sealedPayload = new byte[payload.Length + 16];
            using (var aead = new AesGcm(contentKey, 16))
            {
                aead.Encrypt(nonce, payload, sealedPayload.AsSpan(0, payload.Length), sealedPayload.AsSpan(payload.Length, 16), aad);
            }

            var keys = new List<ExchangeSealedBoxKey>();
            foreach (var recipient in recipients.Take(ExchangeSealedBox.MaximumRecipients))
            {
                var (enc, wrapped) = ExchangeHpke.SealAuth(recipient.PublicPoint, sender, HpkeInfo, aad, contentKey);
                keys.Add(new ExchangeSealedBoxKey(recipient.DeviceId, ExchangeBase64Url.Encode(enc), ExchangeBase64Url.Encode(wrapped)));
            }

            return new ExchangeSealedBox(ExchangeSealedBox.CurrentVersion, senderDeviceId, sender.Kem,
                ExchangeBase64Url.Encode(nonce), ExchangeBase64Url.Encode(sealedPayload), keys);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>
    /// Opens a box as <paramref name="selfDeviceId"/>. <paramref name="trustedSenderPoint"/> is the point this
    /// device trusts for the box's `from` (null when it trusts none); a different `fromKey` opens nothing.
    /// </summary>
    public static ExchangeSealedOpening Open(
        ExchangeSealedBox box, string context, string selfDeviceId, ExchangeE2eKeyPair self,
        ReadOnlySpan<byte> trustedSenderPoint)
    {
        if (box.V != ExchangeSealedBox.CurrentVersion)
        {
            return new(null, ExchangeSealedFailure.Malformed);
        }

        var entry = box.Keys.FirstOrDefault(key => string.Equals(key.To, selfDeviceId, StringComparison.Ordinal));
        if (entry is null)
        {
            return new(null, ExchangeSealedFailure.NoEntry);
        }

        if (!ExchangeBase64Url.TryDecode(box.FromKey, out var fromKey) || fromKey.Length != ExchangeE2eKeyPair.PublicLength
            || trustedSenderPoint.Length != ExchangeE2eKeyPair.PublicLength
            || !CryptographicOperations.FixedTimeEquals(fromKey, trustedSenderPoint))
        {
            return new(null, ExchangeSealedFailure.UntrustedSender);
        }

        if (!ExchangeBase64Url.TryDecode(entry.Enc, out var enc) || enc.Length != ExchangeHpke.EncLength
            || !ExchangeBase64Url.TryDecode(entry.Wk, out var wrapped) || wrapped.Length != 32 + ExchangeHpke.TagLength
            || !ExchangeBase64Url.TryDecode(box.N, out var nonce) || nonce.Length != 12
            || !ExchangeBase64Url.TryDecode(box.Ct, out var sealedPayload) || sealedPayload.Length < 16)
        {
            return new(null, ExchangeSealedFailure.Malformed);
        }

        var aad = Encoding.UTF8.GetBytes(context);
        byte[]? contentKey = null;
        try
        {
            contentKey = ExchangeHpke.OpenAuth(enc, self, fromKey, HpkeInfo, aad, wrapped);
            var payload = new byte[sealedPayload.Length - 16];
            using var aead = new AesGcm(contentKey, 16);
            aead.Decrypt(nonce, sealedPayload.AsSpan(0, payload.Length), sealedPayload.AsSpan(payload.Length, 16), payload, aad);
            return new(payload, ExchangeSealedFailure.None);
        }
        catch (CryptographicException)
        {
            return new(null, ExchangeSealedFailure.BadTag);
        }
        finally
        {
            if (contentKey is not null)
            {
                CryptographicOperations.ZeroMemory(contentKey);
            }
        }
    }
}
