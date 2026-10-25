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
        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellation).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    internal void Forget()
    {
        File.Delete(_path);
        File.Delete(_path + ".new");
    }
}
