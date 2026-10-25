using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226 / `DEVICE-EXCHANGE` items DG and DJ: the keys this PC has seen, per account entry (an entry is named by
/// its own device id, which is stable and unique to it). One DPAPI-protected file beside the account file. Losing
/// it costs only what it remembered - the next list trusts on first use again - so a file that cannot be read is
/// an empty book, never a failure that stops the exchange.
/// </summary>
internal sealed class ExchangeTrustBook(string directory, string fileName = "exchange-trust.bin")
{
    private const int MaximumFileBytes = 1024 * 1024;

    private readonly string _path = Path.Combine(directory, fileName);
    private readonly object _gate = new();
    private Dictionary<string, ExchangeDeviceTrust>? _entries;

    // S7-7: ToData() clears a trust's own dirty flag before the write, so a write that did not land is kept here;
    // without it the keys of that write would never be saved again.
    private bool _unsaved;

    /// <summary>The trust of one entry; created empty the first time it is asked for.</summary>
    internal ExchangeDeviceTrust For(string entryDeviceId)
    {
        lock (_gate)
        {
            var entries = Entries();
            if (!entries.TryGetValue(entryDeviceId, out var trust))
            {
                entries[entryDeviceId] = trust = new ExchangeDeviceTrust();
            }

            return trust;
        }
    }

    /// <summary>Forgets an entry that was left or signed out: its keys go with it.</summary>
    internal void Forget(string entryDeviceId)
    {
        lock (_gate)
        {
            if (Entries().Remove(entryDeviceId))
            {
                SaveLocked(force: true);
            }
        }
    }

    /// <summary>Writes the book when any entry changed since the last write.</summary>
    internal void SaveIfDirty()
    {
        lock (_gate)
        {
            SaveLocked(force: false);
        }
    }

    private Dictionary<string, ExchangeDeviceTrust> Entries()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = [];
        byte[]? clear = null;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaximumFileBytes)
            {
                clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
                var stored = JsonSerializer.Deserialize<Dictionary<string, ExchangeDeviceTrustData>>(clear, ExchangeProtocol.JsonOptions);
                foreach (var (entry, data) in stored ?? [])
                {
                    _entries[entry] = ExchangeDeviceTrust.FromData(data);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _entries = [];
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        return _entries;
    }

    private void SaveLocked(bool force)
    {
        var entries = Entries();
        if (!force && !_unsaved && !entries.Values.Any(trust => trust.IsDirty))
        {
            return;
        }

        _unsaved = true;
        Dictionary<string, ExchangeDeviceTrustData> data;
        try
        {
            data = entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToData());
        }
        catch (InvalidOperationException)
        {
            // S7-5: a guest viewer or a cast task changed a trust while it was being read - ExchangeDeviceTrust
            // has no lock of its own. Nothing was written and the book stays unsaved, so the next save reads it again.
            return;
        }

        var clear = JsonSerializer.SerializeToUtf8Bytes(data, ExchangeProtocol.JsonOptions);
        var temporary = _path + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
            _unsaved = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The book stays in memory for this session and stays unsaved (S7-7), so the next save tries again.
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
