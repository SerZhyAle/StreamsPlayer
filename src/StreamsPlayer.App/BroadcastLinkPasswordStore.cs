using System.IO;
using System.Security.Cryptography;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// LIVE-BROADCAST item R: the one password the person set for the outward link, kept only under the platform secret
/// store (Windows DPAPI, current user) - never in the settings file, a backup, a log or a diagnostic dump. A restored
/// settings file carries the choice and finds no password here, and the producer then uses a generated one until the
/// person types it again.
/// </summary>
internal sealed class BroadcastLinkPasswordStore(string directory)
{
    private const long MaximumFileBytes = 4096;
    private readonly string _path = Path.Combine(directory, "broadcast-link-password.bin");

    /// <summary>The saved password, or null when none was ever saved, the file is not this Windows user's, or it is not an acceptable password.</summary>
    internal string? Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length is 0 or > MaximumFileBytes)
            {
                return null;
            }

            var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            try
            {
                var text = Encoding.UTF8.GetString(clear);
                return BroadcastLinkPassword.IsAcceptable(text) ? text : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Another Windows user's file, or one that no longer reads: both mean the person has to type it again.
            return null;
        }
    }

    internal void Save(string password)
    {
        if (!BroadcastLinkPassword.IsAcceptable(password))
        {
            throw new ArgumentException("Not an acceptable link password.", nameof(password));
        }

        var clear = Encoding.UTF8.GetBytes(password);
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

    internal bool Exists => File.Exists(_path);

    internal void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
