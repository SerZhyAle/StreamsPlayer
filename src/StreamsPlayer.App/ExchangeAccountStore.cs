using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed record ExchangeAccount(string DeviceId, string Host = "", int Port = 0, string Login = "",
    string? Token = null, string? Pin = null, bool Enabled = false, bool AutoAcceptCasts = false);

internal sealed class ExchangeAccountStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "exchange-account.bin");
    internal bool ProtectionFailed { get; private set; }

    internal ExchangeAccount Load()
    {
        if (!File.Exists(_path))
        {
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

            return account;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Reusing an identity whose token belongs to another Windows user would rotate that user's token.
            ProtectionFailed = true;
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
