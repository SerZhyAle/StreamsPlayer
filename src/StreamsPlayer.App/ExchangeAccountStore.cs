using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// One account entry of this device (DEVICE-EXCHANGE item AA). <c>Pin</c> is the accepted leaf's fingerprint and
/// <c>PinKey</c> the <c>SHA256:</c> of that leaf's public key, kept so a renewal with the same key needs no new
/// confirmation (item AY). <c>Guest</c> is the account's kind (item AO). <c>SignedOut</c> records that the
/// account ended this device's sign-in (item AT) - the entry, its device id and its server stay, only the token
/// is gone - so the message and the prompt survive a restart. The last three default, so an older file reads
/// unchanged.
/// </summary>
internal sealed record ExchangeAccount(string DeviceId, string Host = "", int Port = 0, string Login = "",
    string? Token = null, string? Pin = null, bool Enabled = false, bool AutoAcceptCasts = false,
    string? PinKey = null, bool Guest = false, bool SignedOut = false,
    string? E2ePrivateKey = null, string? E2eKem = null)
{
    /// <summary>
    /// SP-0226 / item DF: this entry's device key pair, or null for an entry made before sealing existed (the
    /// server knows its device id with no key, and a known id cannot be given one). The private scalar lives only
    /// in this DPAPI-protected record, as the token does; a stored pair whose halves disagree reads as none.
    /// The caller owns the returned pair and disposes it.
    /// </summary>
    internal ExchangeE2eKeyPair? LoadE2eKey() =>
        ExchangeBase64Url.TryDecode(E2ePrivateKey, out var scalar) && ExchangeBase64Url.TryDecode(E2eKem, out var point)
            ? ExchangeE2eKeyPair.TryImport(scalar, point)
            : null;

    /// <summary>The same entry carrying <paramref name="pair"/> (or none).</summary>
    internal ExchangeAccount WithE2eKey(ExchangeE2eKeyPair? pair)
    {
        if (pair is null)
        {
            return this with { E2ePrivateKey = null, E2eKem = null };
        }

        var scalar = pair.ExportPrivateScalar();
        try
        {
            return this with { E2ePrivateKey = ExchangeBase64Url.Encode(scalar), E2eKem = pair.Kem };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>An entry that was enrolled before keys existed: the server holds its id, this record holds no key.</summary>
    internal bool IsKeylessLegacy => E2ePrivateKey is null && Host.Length > 0;

    // The synthesized text would print the token and the private key; a log line or a failure text must not.
    public override string ToString() => $"ExchangeAccount {{ DeviceId = {DeviceId}, Host = {Host}, Port = {Port}, Guest = {Guest} }}";
}

internal sealed class ExchangeAccountStore(string directory, string fileName = "exchange-account.bin")
{
    private readonly string _path = Path.Combine(directory, fileName);
    internal bool ProtectionFailed { get; private set; }

    /// <summary>
    /// True when the last <see cref="Load"/> found no usable file and returned a new account, whose device
    /// id exists nowhere yet (SP-0212 L9): only such an account needs its first save; one read from the
    /// file is already stored, byte for byte.
    /// </summary>
    internal bool CreatedFresh { get; private set; }

    internal ExchangeAccount Load()
    {
        if (!File.Exists(_path))
        {
            CreatedFresh = true;
            return new ExchangeAccount(ExchangeProtocol.NewDeviceId());
        }

        if (new FileInfo(_path).Length > 65536)
        {
            throw new IOException("Protected exchange account exceeds the size limit.");
        }

        var protectedBytes = File.ReadAllBytes(_path);
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var account = JsonSerializer.Deserialize<ExchangeAccount>(clear, ExchangeProtocol.JsonOptions)
                ?? throw new JsonException();
            if (account.DeviceId.Length != 22 || (account.Token is not null && account.Token.Length != 43)
                || (account.Token is not null && (string.IsNullOrWhiteSpace(account.Host) || account.Port is < 1 or > 65535)))
            {
                throw new JsonException();
            }

            CreatedFresh = false;
            return account;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Reusing an identity whose token belongs to another Windows user would rotate that user's token.
            ProtectionFailed = true;
            CreatedFresh = true;
            return new ExchangeAccount(ExchangeProtocol.NewDeviceId());
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    internal void Save(ExchangeAccount account)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(account, ExchangeProtocol.JsonOptions);
        var temporary = _path + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                file.Write(protectedBytes);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
