using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed record DriveAccount(string DeviceId, string ClientHash, string RefreshToken, Dictionary<string, string> OwnedFiles)
{
    public override string ToString() => nameof(DriveAccount);
}

internal sealed class DriveAccountStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "drive-account.bin");

    internal DriveAccount? Load(string clientHash)
    {
        if (!File.Exists(_path))
            return null;
        if (new FileInfo(_path).Length > 65536)
            throw new InvalidDataException("Protected Drive account exceeds the limit.");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
        try
        {
            var account = JsonSerializer.Deserialize<DriveAccount>(clear);
            return account is { RefreshToken.Length: > 0, DeviceId.Length: 22, OwnedFiles: not null }
                && account.ClientHash == clientHash ? account : null;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal async Task SaveAsync(DriveAccount account, CancellationToken cancellation)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(account);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(directory);
        var temporary = _path + ".new";
        // S2-6: the grant must be on disk before the rename publishes it, as ExchangeAccountStore.Save does; a
        // power loss between an unflushed write and the move could otherwise leave a zero-length account file.
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await file.WriteAsync(protectedBytes, cancellation).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    internal void Forget()
    {
        File.Delete(_path);
        File.Delete(_path + ".new");
    }
}
