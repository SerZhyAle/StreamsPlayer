using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226 / `LIVE-BROADCAST` item Q: the keys of a sealed link a person imported - the relay's group key and the
/// tunnel's pin, which the link carries in its endpoints and the channel does not keep (they are not serialized
/// with it). They are a secret that opens the broadcast, so they sit under the platform secret store (Windows DPAPI,
/// current user) beside the account files, keyed by the broadcast id the relay and tunnel addresses carry, and are
/// never logged. Losing the file costs only the plays that needed it: the person imports the link again.
/// </summary>
internal sealed class ExchangeLinkKeyStore
{
    private const long MaximumFileBytes = 256 * 1024;
    private const int MaximumEntries = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ExchangeLinkKeyStore> Stores = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;

    private ExchangeLinkKeyStore(string directory) => _path = Path.Combine(directory, "broadcast-link-keys.bin");

    /// <summary>The store of a data folder; one instance per folder, so every window and the lookup share one file's lock.</summary>
    internal static ExchangeLinkKeyStore For(string directory)
    {
        lock (Gate)
        {
            if (!Stores.TryGetValue(directory, out var store))
            {
                Stores[directory] = store = new ExchangeLinkKeyStore(directory);
            }

            return store;
        }
    }

    private sealed record StoredKeys(string? RelayKey, string? TunnelPin);

    internal ExchangeBroadcastSecrets? Load(string broadcastId)
    {
        lock (Gate)
        {
            if (Read() is not { } keys || !keys.TryGetValue(broadcastId, out var stored))
            {
                return null;
            }

            byte[]? relayKey = null;
            if (stored.RelayKey is not null && !ExchangeBase64Url.TryDecode(stored.RelayKey, out relayKey!))
            {
                return null;
            }

            return new ExchangeBroadcastSecrets(relayKey, stored.TunnelPin, null);
        }
    }

    internal void Save(string broadcastId, ExchangeBroadcastSecrets secrets)
    {
        lock (Gate)
        {
            // S7-11: a file that exists but could not be read just now is not an empty store. Writing from an empty copy
            // would drop the keys of every other imported link, so these keys stay in memory for this run instead.
            if (Read() is not { } existing)
            {
                return;
            }

            var entries = new Dictionary<string, StoredKeys>(existing, StringComparer.Ordinal)
            {
                [broadcastId] = new StoredKeys(secrets.RelayKey is null ? null : ExchangeBase64Url.Encode(secrets.RelayKey), secrets.TunnelPin)
            };
            while (entries.Count > MaximumEntries)
            {
                entries.Remove(entries.Keys.First());
            }

            Write(entries);
        }
    }

    /// <summary>The stored keys; empty when there is no usable file, and null when the file could not be read right now.</summary>
    private Dictionary<string, StoredKeys>? Read()
    {
        byte[]? clear = null;
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumFileBytes)
            {
                return [];
            }

            clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, StoredKeys>>(clear, ExchangeProtocol.JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Damaged, or another Windows user's: nothing in it can be recovered, so a fresh store is correct.
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private void Write(Dictionary<string, StoredKeys> entries)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(entries, ExchangeProtocol.JsonOptions);
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The keys stay in memory for this session; the person imports the link again after a restart.
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
