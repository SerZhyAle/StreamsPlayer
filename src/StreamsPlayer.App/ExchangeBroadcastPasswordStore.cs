using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0215 requirement 7: the passwords of protected broadcasts a person chose to remember, kept with
/// the imported channel the way item M states - under the platform secret store (Windows DPAPI,
/// current user), as a PIN is kept. One file beside the account store; the account itself is never
/// read or written here, so a foreign broadcast's password never touches the device's own account.
/// </summary>
internal sealed class ExchangeBroadcastPasswordStore(string directory)
{
    private const long MaximumFileBytes = 65536;
    private readonly string _path = Path.Combine(directory, "broadcast-passwords.bin");

    /// <summary>One entry per remembered channel: the password only - the user name always comes from the descriptor's <c>auth</c>, which may change with it.</summary>
    private sealed record StoredPasswords(Dictionary<Guid, string> Entries);

    /// <summary>The remembered password for a channel, or null when the person never typed one (or declined to keep it).</summary>
    internal string? Load(Guid channelId)
    {
        var entries = ReadEntries();
        return entries.TryGetValue(channelId, out var password) ? password : null;
    }

    internal void Save(Guid channelId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var entries = new Dictionary<Guid, string>(ReadEntries());
        entries[channelId] = password;
        WriteEntries(entries);
    }

    /// <summary>
    /// A password the relay refused is the broadcaster's old one: keeping it would send a listener to a
    /// door that always refuses, so the next explicit play prompts once again.
    /// </summary>
    internal void Delete(Guid channelId)
    {
        var entries = new Dictionary<Guid, string>(ReadEntries());
        if (entries.Remove(channelId))
        {
            WriteEntries(entries);
        }
    }

    private IReadOnlyDictionary<Guid, string> ReadEntries()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<Guid, string>();
        }

        if (new FileInfo(_path).Length > MaximumFileBytes)
        {
            throw new IOException("Protected broadcast passwords exceed the size limit.");
        }

        var protectedBytes = File.ReadAllBytes(_path);
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredPasswords>(clear, ExchangeProtocol.JsonOptions);
            return stored?.Entries ?? new Dictionary<Guid, string>();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // The file belongs to this Windows user or no longer parses: both mean no remembered
            // password, which is the state the person started from. The broadcast asks again.
            return new Dictionary<Guid, string>();
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private void WriteEntries(Dictionary<Guid, string> entries)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(new StoredPasswords(entries), ExchangeProtocol.JsonOptions);
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
