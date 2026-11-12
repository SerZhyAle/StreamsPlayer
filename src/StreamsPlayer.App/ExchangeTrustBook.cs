using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226 / `DEVICE-EXCHANGE` items DG and DJ: the keys this PC has seen, per account entry (an entry is named by
/// its own device id, which is stable and unique to it). One DPAPI-protected file beside the account file. Losing
/// it costs only what it remembered - the next list trusts on first use again - so a file that cannot be decrypted is
/// an empty book, never a failure that stops the exchange. A file that cannot be read at all right now (locked,
/// denied) is not lost: the book works in memory, reads it again later and never writes over it until it has (S3-1).
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
    private long _retryAfter;

    // S3-1: the file existed but could not be read (a transient lock, a denied read). The empty in-memory book is
    // then not the book: it is read again after a short pause, and a save is refused until a read succeeds.
    private bool _loadFailed;
    private long _retryLoadAfter;
    private readonly HashSet<string> _forgottenWhileUnreadable = new(StringComparer.Ordinal);

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
            var removed = Entries().Remove(entryDeviceId);
            if (_loadFailed)
            {
                // RRB-3: the entry may not be in memory yet (the file could not be read), but it must not come back from
                // the file once it can be; the removal waits for the save that a successful read unlocks.
                _forgottenWhileUnreadable.Add(entryDeviceId);
                _unsaved = true;
            }

            if (removed)
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
        if (_entries is not null && (!_loadFailed || Environment.TickCount64 < _retryLoadAfter))
        {
            return _entries;
        }

        // What this session learned while the file could not be read; it keeps its identity, because callers hold
        // the trust objects that For() handed out.
        var entries = _entries ?? [];
        Dictionary<string, ExchangeDeviceTrustData>? stored = null;
        var unreadable = false;
        byte[]? clear = null;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaximumFileBytes)
            {
                clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
                stored = JsonSerializer.Deserialize<Dictionary<string, ExchangeDeviceTrustData>>(clear, ExchangeProtocol.JsonOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // S3-1: not readable now is not undecryptable. The file stays as it is, the keys seen meanwhile stay in
            // memory, and nothing is written over it until a later read succeeds (SaveLocked).
            unreadable = true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Undecryptable or corrupt: an empty book, the next list trusts on first use again.
            stored = null;
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        _loadFailed = unreadable;
        _retryLoadAfter = unreadable ? Environment.TickCount64 + 5_000 : 0;
        foreach (var (entry, data) in stored ?? [])
        {
            if (_forgottenWhileUnreadable.Contains(entry))
            {
                continue;
            }

            var trust = ExchangeDeviceTrust.FromData(data);
            if (!entries.TryGetValue(entry, out var current))
            {
                entries[entry] = trust;
                continue;
            }

            // The entry was used while the file was unreadable: what this session observed stays, and the keys the file
            // holds that it lacks are added (an observation never replaces a kept key).
            // RRB-4: where the session saw another key than the file holds, the file's key is the older pin and wins, so
            // the next list reports the change instead of this merge hiding it.
            foreach (var id in trust.DeviceIds)
            {
                if (trust.TrustedDevice(id) is { } point
                    && current.ObserveDevice(id, point) == ExchangeKeyObservation.KeyChanged)
                {
                    current.ForgetDevice(id);
                    current.ObserveDevice(id, point);
                }
            }

            foreach (var id in (IEnumerable<string>?)data?.Grants?.Keys ?? [])
            {
                if (trust.TrustedGrant(id) is { } point
                    && current.ObserveGrant(id, point) == ExchangeKeyObservation.KeyChanged)
                {
                    current.ForgetGrant(id);
                    current.ObserveGrant(id, point);
                }
            }
        }

        if (!unreadable)
        {
            _forgottenWhileUnreadable.Clear();
        }

        return _entries = entries;
    }

    private void SaveLocked(bool force)
    {
        var entries = Entries();
        // S3-1: a book whose file could not be read is never saved over it; the removal or keys wait in memory.
        if (_loadFailed)
        {
            if (force) _unsaved = true;
            return;
        }

        if (!force && Environment.TickCount64 < _retryAfter) return;
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
            _retryAfter = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Keep unsaved trust in memory, without serializing it on every directory/keepalive event.
            _retryAfter = Environment.TickCount64 + 30_000;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { /* A blocked temporary is retried with the book, after the backoff. */ }
        }
    }
}
