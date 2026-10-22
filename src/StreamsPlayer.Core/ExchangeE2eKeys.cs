using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// A device key pair of `DEVICE-EXCHANGE` item DF: P-256, the private scalar (32 bytes) and the public point
/// as the 65-byte uncompressed SEC1 form (`kem`). The pair is held together because the platform's key import
/// needs the public point and the sealed box needs both halves of the sender. The private bytes are zeroed
/// when the pair is disposed; nothing in this type is ever printed.
/// </summary>
public sealed class ExchangeE2eKeyPair : IDisposable
{
    public const int PrivateLength = 32;
    public const int PublicLength = 65;

    private readonly byte[] _private;

    private ExchangeE2eKeyPair(byte[] privateScalar, byte[] publicPoint)
    {
        _private = privateScalar;
        PublicPoint = publicPoint;
    }

    /// <summary>The 65-byte uncompressed SEC1 point, `0x04 || X || Y`.</summary>
    public byte[] PublicPoint { get; }

    /// <summary>The `kem` member of `e2eKey`: the public point, base64url without padding.</summary>
    public string Kem => ExchangeBase64Url.Encode(PublicPoint);

    /// <summary>The private scalar, for the cipher only - never copied out of Core except through <see cref="ExportPrivateScalar"/>.</summary>
    internal ReadOnlySpan<byte> PrivateScalar => _private;

    /// <summary>
    /// A copy of the private scalar for the platform's secret store, the one place it may go. The caller zeroes the
    /// copy as soon as it has been protected, and never logs, prints or sends it.
    /// </summary>
    public byte[] ExportPrivateScalar() => (byte[])_private.Clone();

    public static ExchangeE2eKeyPair Generate()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return new ExchangeE2eKeyPair(parameters.D!, Encode(parameters.Q));
    }

    /// <summary>
    /// Builds the pair from stored values. The public point must belong to the scalar: a pair whose halves
    /// disagree is refused here rather than failing every later seal with no explanation.
    /// </summary>
    public static ExchangeE2eKeyPair? TryImport(ReadOnlySpan<byte> privateScalar, ReadOnlySpan<byte> publicPoint)
    {
        if (privateScalar.Length != PrivateLength || publicPoint.Length != PublicLength || publicPoint[0] != 0x04)
        {
            return null;
        }

        var pair = new ExchangeE2eKeyPair(privateScalar.ToArray(), publicPoint.ToArray());
        try
        {
            using var own = pair.CreateKey();
            using var probe = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var probePublicKey = ECDiffieHellman.Create();
            probePublicKey.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = probe.ExportParameters(includePrivateParameters: false).Q
            });
            using var claimedPublicKey = ECDiffieHellman.Create();
            claimedPublicKey.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Decode(publicPoint) });
            // ECDH(own private, probe public) must equal ECDH(probe private, claimed public).
            var left = own.DeriveRawSecretAgreement(probePublicKey.PublicKey);
            var right = probe.DeriveRawSecretAgreement(claimedPublicKey.PublicKey);
            if (!CryptographicOperations.FixedTimeEquals(left, right))
            {
                pair.Dispose();
                return null;
            }

            return pair;
        }
        catch (CryptographicException)
        {
            pair.Dispose();
            return null;
        }
    }

    internal ECDiffieHellman CreateKey()
    {
        var key = ECDiffieHellman.Create();
        var scalar = _private.ToArray();
        try
        {
            key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar, Q = Decode(PublicPoint) });
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>
    /// The check code two screens can compare (item DG): the first 8 bytes of SHA-256 over
    /// `fmsx-e2e-v1 check` and the point, as an unsigned big-endian integer modulo 10^12, twelve digits in
    /// three groups of four. Showing it is optional; this is the one place its arithmetic is written.
    /// </summary>
    public static string CheckCode(ReadOnlySpan<byte> publicPoint)
    {
        var prefix = Encoding.ASCII.GetBytes("fmsx-e2e-v1 check");
        var input = new byte[prefix.Length + publicPoint.Length];
        prefix.CopyTo(input, 0);
        publicPoint.CopyTo(input.AsSpan(prefix.Length));
        var head = SHA256.HashData(input).AsSpan(0, 8);
        var number = new BigInteger(head, isUnsigned: true, isBigEndian: true) % 1_000_000_000_000;
        var digits = number.ToString().PadLeft(12, '0');
        return $"{digits[..4]} {digits[4..8]} {digits[8..]}";
    }

    internal static ECPoint Decode(ReadOnlySpan<byte> point)
    {
        if (point.Length != PublicLength || point[0] != 0x04)
        {
            throw new CryptographicException("A device key is a 65-byte uncompressed P-256 point.");
        }

        return new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() };
    }

    private static byte[] Encode(ECPoint point)
    {
        var bytes = new byte[PublicLength];
        bytes[0] = 0x04;
        point.X!.CopyTo(bytes, 1);
        point.Y!.CopyTo(bytes, 33);
        return bytes;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_private);
}
