using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// HPKE (RFC 9180) in the one suite `DEVICE-EXCHANGE` item DH names: Auth mode, DHKEM(P-256, HKDF-SHA256)
/// `0x0010`, HKDF-SHA256 `0x0001`, AES-128-GCM `0x0001`, single-shot (sequence number 0). Written from the
/// RFC on the base class library because .NET has no HPKE and a package would add a third-party binary for
/// one fixed suite; item DH allows either and asks that Appendix A.3 passes first (the suite's tests do).
/// The type holds no state and logs nothing.
/// </summary>
public static class ExchangeHpke
{
    public const int EncLength = ExchangeE2eKeyPair.PublicLength;
    public const int KeyLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    private const ushort KemId = 0x0010;
    private const ushort KdfId = 0x0001;
    private const ushort AeadId = 0x0001;
    private const byte ModeAuth = 0x02;
    private const int SharedSecretLength = 32;

    private static readonly byte[] LabelPrefix = Encoding.ASCII.GetBytes("HPKE-v1");
    private static readonly byte[] KemSuite = Concat(Encoding.ASCII.GetBytes("KEM"), I2Osp2(KemId));
    private static readonly byte[] HpkeSuite = Concat(Encoding.ASCII.GetBytes("HPKE"), I2Osp2(KemId), I2Osp2(KdfId), I2Osp2(AeadId));

    /// <summary>The intermediate values of one key schedule, exposed to the suite's own tests only.</summary>
    internal sealed record Schedule(byte[] SharedSecret, byte[] KeyScheduleContext, byte[] Secret, byte[] Key, byte[] BaseNonce);

    /// <summary>
    /// `SealAuth`: encrypts <paramref name="plaintext"/> to the recipient's public point, authenticated by the
    /// sender's private key. Returns the encapsulated key (65 bytes) and the ciphertext with its tag.
    /// </summary>
    public static (byte[] Enc, byte[] Ciphertext) SealAuth(
        ReadOnlySpan<byte> recipientPublic, ExchangeE2eKeyPair sender,
        ReadOnlySpan<byte> info, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        using var ephemeral = ExchangeE2eKeyPair.Generate();
        return SealAuth(recipientPublic, sender, ephemeral, info, aad, plaintext);
    }

    internal static (byte[] Enc, byte[] Ciphertext) SealAuth(
        ReadOnlySpan<byte> recipientPublic, ExchangeE2eKeyPair sender, ExchangeE2eKeyPair ephemeral,
        ReadOnlySpan<byte> info, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        var schedule = AuthEncapSchedule(recipientPublic, sender, ephemeral, info);
        var ciphertext = new byte[plaintext.Length + TagLength];
        using var aead = new AesGcm(schedule.Key, TagLength);
        aead.Encrypt(schedule.BaseNonce, plaintext, ciphertext.AsSpan(0, plaintext.Length),
            ciphertext.AsSpan(plaintext.Length, TagLength), aad);
        return (ephemeral.PublicPoint.ToArray(), ciphertext);
    }

    /// <summary>
    /// `OpenAuth`: the inverse. Throws <see cref="CryptographicException"/> for a malformed point, a wrong
    /// sender or recipient key, a different context, or a failed tag - the caller maps every one of them to
    /// the same `sealed-failed` outcome and uses nothing partial.
    /// </summary>
    public static byte[] OpenAuth(
        ReadOnlySpan<byte> enc, ExchangeE2eKeyPair recipient, ReadOnlySpan<byte> senderPublic,
        ReadOnlySpan<byte> info, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagLength)
        {
            throw new CryptographicException("The sealed key is shorter than its tag.");
        }

        var schedule = AuthDecapSchedule(enc, recipient, senderPublic, info);
        var plaintext = new byte[ciphertext.Length - TagLength];
        using var aead = new AesGcm(schedule.Key, TagLength);
        aead.Decrypt(schedule.BaseNonce, ciphertext[..^TagLength], ciphertext[^TagLength..], plaintext, aad);
        return plaintext;
    }

    internal static Schedule AuthEncapSchedule(
        ReadOnlySpan<byte> recipientPublic, ExchangeE2eKeyPair sender, ExchangeE2eKeyPair ephemeral, ReadOnlySpan<byte> info)
    {
        var dh = Concat(Dh(ephemeral, recipientPublic), Dh(sender, recipientPublic));
        var kemContext = Concat(ephemeral.PublicPoint, recipientPublic.ToArray(), sender.PublicPoint);
        return KeySchedule(ExtractAndExpand(dh, kemContext), info);
    }

    internal static Schedule AuthDecapSchedule(
        ReadOnlySpan<byte> enc, ExchangeE2eKeyPair recipient, ReadOnlySpan<byte> senderPublic, ReadOnlySpan<byte> info)
    {
        var dh = Concat(Dh(recipient, enc), Dh(recipient, senderPublic));
        var kemContext = Concat(enc.ToArray(), recipient.PublicPoint, senderPublic.ToArray());
        return KeySchedule(ExtractAndExpand(dh, kemContext), info);
    }

    private static byte[] Dh(ExchangeE2eKeyPair own, ReadOnlySpan<byte> peerPublic)
    {
        using var key = own.CreateKey();
        using var peer = ECDiffieHellman.Create();
        peer.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = ExchangeE2eKeyPair.Decode(peerPublic) });
        return key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    private static byte[] ExtractAndExpand(byte[] dh, byte[] kemContext)
    {
        var prk = LabeledExtract(KemSuite, [], "eae_prk", dh);
        return LabeledExpand(KemSuite, prk, "shared_secret", kemContext, SharedSecretLength);
    }

    private static Schedule KeySchedule(byte[] sharedSecret, ReadOnlySpan<byte> info)
    {
        var pskIdHash = LabeledExtract(HpkeSuite, [], "psk_id_hash", []);
        var infoHash = LabeledExtract(HpkeSuite, [], "info_hash", info.ToArray());
        var context = Concat([ModeAuth], pskIdHash, infoHash);
        var secret = LabeledExtract(HpkeSuite, sharedSecret, "secret", []);
        return new Schedule(sharedSecret, context, secret,
            LabeledExpand(HpkeSuite, secret, "key", context, KeyLength),
            LabeledExpand(HpkeSuite, secret, "base_nonce", context, NonceLength));
    }

    private static byte[] LabeledExtract(byte[] suite, byte[] salt, string label, byte[] ikm) =>
        HKDF.Extract(HashAlgorithmName.SHA256,
            Concat(LabelPrefix, suite, Encoding.ASCII.GetBytes(label), ikm), salt);

    private static byte[] LabeledExpand(byte[] suite, byte[] prk, string label, byte[] info, int length)
    {
        var labeledInfo = Concat(I2Osp2((ushort)length), LabelPrefix, suite, Encoding.ASCII.GetBytes(label), info);
        return HKDF.Expand(HashAlgorithmName.SHA256, prk, length, labeledInfo);
    }

    private static byte[] I2Osp2(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
