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

    internal ExchangeGuestEntries(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, "exchange-guests.bin");
        _entries = Load();
    }

    /// <summary>True when the file existed but could not be read (another Windows user's, or damaged): the entries are gone.</summary>
    internal bool ProtectionFailed { get; private set; }

    internal IReadOnlyList<ExchangeAccount> Entries => _entries;
    internal bool IsFull => _entries.Length >= ExchangeEntryLimits.MaximumGuestEntries;

    internal ExchangeAccount? Find(string host, int port, string login) => _entries.FirstOrDefault(entry =>
        ExchangeAccountIdentity.SameAccount(entry.Host, entry.Port, entry.Login, host, port, login));

    /// <summary>Adds the entry, or replaces the one with the same identity (a re-sign-in rotates the token, same device id).</summary>
    internal void Upsert(ExchangeAccount entry)
    {
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

    private ExchangeAccount[] Load()
    {
        if (!File.Exists(_path))
        {
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
            return entries;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException
            or UnauthorizedAccessException)
        {
            // An entry whose token belongs to another Windows user is not ours to rotate; the file is dropped.
            ProtectionFailed = true;
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
