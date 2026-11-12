using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0221 requirement 9: the guest entries a PC holds beside its one own entry (DEVICE-EXCHANGE items AO and
/// AA). An entry is an account of kind <c>guest</c>: it never broadcasts, announces nothing and is never left
/// when the own entry joins another server. They live in one DPAPI file of their own, so the own entry's file -
/// and every test and tool that reads it - is unchanged, and losing this one costs the guest sign-ins and
/// nothing else. Mutations run under <see cref="ExchangeSourceService"/>'s operations gate; the list a reader
/// sees is an immutable snapshot.
/// </summary>
internal sealed class ExchangeGuestEntries
{
    private const int MaximumFileBytes = 65536;
    private readonly string _directory;
    private readonly string _path;
    private ExchangeAccount[] _entries = [];

    // S2-5: the file exists but could not be read just now (a sharing violation, a locked folder). That is not
    // an empty list: writing from it would destroy every stored guest token and key, which are single-use.
    private bool _loadFailed;

    // SP-0246 R2-4: when the current run of failed reads began. A file that stays unreadable would otherwise refuse
    // every guest sign-in for as long as it stays that way; after the grace it is set aside (kept, not deleted).
    private long? _unreadableSince;

    internal ExchangeGuestEntries(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, "exchange-guests.bin");
        _entries = Load();
    }

    /// <summary>How long a file may stay unreadable before a mutation sets it aside; settable so a test need not wait for it.</summary>
    internal TimeSpan UnreadableGrace { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The name an unreadable file is kept under once it has been set aside.</summary>
    internal string SetAsidePath => _path + ".unreadable";

    /// <summary>
    /// True when the file existed but could not be decrypted or parsed (another Windows user's, or damaged), or stayed
    /// unreadable past <see cref="UnreadableGrace"/> and was set aside: the entries are gone. A read that only failed on
    /// the disk just now is not this; the file is kept and not written over.
    /// </summary>
    internal bool ProtectionFailed { get; private set; }

    internal IReadOnlyList<ExchangeAccount> Entries => _entries;
    internal bool IsFull => _entries.Length >= ExchangeEntryLimits.MaximumGuestEntries;

    internal ExchangeAccount? Find(string host, int port, string login) => _entries.FirstOrDefault(entry =>
        ExchangeAccountIdentity.SameAccount(entry.Host, entry.Port, entry.Login, host, port, login));

    /// <summary>Adds the entry, or replaces the one with the same identity (a re-sign-in rotates the token, same device id).</summary>
    internal void Upsert(ExchangeAccount entry)
    {
        RequireReadable();
        var existing = Find(entry.Host, entry.Port, entry.Login);
        if (existing is null && IsFull)
        {
            throw new InvalidOperationException("The guest entries are full.");
        }

        var next = _entries.Where(item => item != existing).Append(entry).ToArray();
        Save(next);
        _entries = next;
    }

    internal bool Remove(string host, int port, string login)
    {
        RequireReadable();
        var existing = Find(host, port, login);
        if (existing is null)
        {
            return false;
        }

        var next = _entries.Where(item => item != existing).ToArray();
        Save(next);
        _entries = next;
        return true;
    }

    /// <summary>
    /// S2-5: a mutation never writes from a list that was not read. After a transient read failure the file is read again
    /// first; if it still cannot be read the write is refused as a failed write is (an <see cref="IOException"/>), so the
    /// stored entries survive until a later read succeeds. R2-4: a read that keeps failing for <see cref="UnreadableGrace"/>
    /// is not a passing lock: the file is moved aside, so the entries it held are gone but the person can sign in again.
    /// </summary>
    private void RequireReadable()
    {
        if (!_loadFailed)
        {
            return;
        }

        _entries = Load();
        if (_loadFailed)
        {
            if (_unreadableSince is { } since
                && Environment.TickCount64 - since >= UnreadableGrace.TotalMilliseconds
                && SetAside())
            {
                return;
            }

            throw new IOException("The guest entries could not be read, so they are not overwritten.");
        }
    }

    private bool SetAside()
    {
        try
        {
            File.Move(_path, SetAsidePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false; // Not even renamable: the file stays where it is, and so does the refusal.
        }

        _entries = [];
        _loadFailed = false;
        _unreadableSince = null;
        ProtectionFailed = true;
        return true;
    }

    private ExchangeAccount[] Load()
    {
        _loadFailed = false;
        if (!File.Exists(_path))
        {
            _unreadableSince = null;
            return [];
        }

        byte[]? clear = null;
        try
        {
            if (new FileInfo(_path).Length > MaximumFileBytes)
            {
                throw new JsonException();
            }

            clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            var document = JsonSerializer.Deserialize<GuestFile>(clear, ExchangeProtocol.JsonOptions) ?? throw new JsonException();
            var entries = (document.Entries ?? []).Where(Plausible)
                .GroupBy(entry => (ExchangeAccountIdentity.NormalizeHost(entry.Host), entry.Port, entry.Login.ToLowerInvariant()))
                .Select(group => group.First()).Take(ExchangeEntryLimits.MaximumGuestEntries).ToArray();
            _unreadableSince = null;
            return entries;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // An entry whose token belongs to another Windows user is not ours to rotate; the file is dropped.
            ProtectionFailed = true;
            _unreadableSince = null;
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // S2-5: not readable now is not undecryptable. The file is left as it is, and nothing is written over it
            // until a later read succeeds (RequireReadable) or, R2-4, the failure has outlasted the grace.
            _loadFailed = true;
            _unreadableSince ??= Environment.TickCount64;
            return [];
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private static bool Plausible(ExchangeAccount entry) => entry.Guest && entry.DeviceId.Length == 22
        && entry.Token is { Length: 43 } && entry.Host.Length > 0 && entry.Port is >= 1 and <= 65535
        && entry.Login.Length > 0;

    private void Save(ExchangeAccount[] entries)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(new GuestFile(entries), ExchangeProtocol.JsonOptions);
        var temporary = _path + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(_directory);
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

    private sealed record GuestFile(ExchangeAccount[]? Entries);
}
