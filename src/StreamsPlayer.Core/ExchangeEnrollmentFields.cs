using System.Buffers;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// The members of an `enroll` frame (`DEVICE-EXCHANGE` 7.2) other than the credential, in one place so the key
/// rule of item DF cannot drift between callers: a device that holds a key declares the capability `e2e-v1` and
/// carries `e2eKey: {v: 1, kem}`; a device that holds none (an entry made before sealing existed) sends neither,
/// never a null member and never a capability it cannot honour.
/// </summary>
public static class ExchangeEnrollmentFields
{
    public const string Capability = "e2e-v1";

    /// <summary>AM bounds before opening a connection: UTF-8 bytes for passwords, Unicode characters for pairing codes.</summary>
    public static bool IsValidSecret(ReadOnlySpan<char> secret, bool pairingCode)
    {
        var characters = 0;
        var bytes = 0;
        while (!secret.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(secret, out var rune, out var consumed) != OperationStatus.Done || Rune.IsControl(rune))
            {
                return false;
            }

            characters++;
            bytes += rune.Utf8SequenceLength;
            if (pairingCode ? characters > 64 : bytes > 1024)
            {
                return false;
            }

            secret = secret[consumed..];
        }

        return characters > 0;
    }

    public static Dictionary<string, object> Build(
        string login, string deviceId, string deviceName, string product, string productVersion, string platform,
        IReadOnlyList<string> roles, object receiver, int keepaliveSeconds, ExchangeE2eKeyPair? deviceKey)
    {
        var fields = new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["type"] = "enroll",
            ["login"] = login,
            ["deviceId"] = deviceId,
            ["deviceName"] = deviceName,
            ["product"] = product,
            ["productVersion"] = productVersion,
            ["platform"] = platform,
            ["roles"] = roles,
            ["receiver"] = receiver,
            ["keepaliveSeconds"] = keepaliveSeconds
        };
        if (deviceKey is not null)
        {
            fields["capabilities"] = new[] { Capability };
            fields["e2eKey"] = new Dictionary<string, object> { ["v"] = 1, ["kem"] = deviceKey.Kem };
        }

        return fields;
    }
}
