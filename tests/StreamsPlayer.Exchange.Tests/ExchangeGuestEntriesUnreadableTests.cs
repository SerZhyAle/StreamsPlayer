using System.IO;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0246 R2-4: a guest file that stays unreadable must not refuse every guest sign-in for ever. The lock below is
/// the one a scanner or a backup tool takes: it blocks every reader and still lets the file be renamed.
/// </summary>
public sealed class ExchangeGuestEntriesUnreadableTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));

    public ExchangeGuestEntriesUnreadableTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "exchange-guests.bin");

    private static ExchangeAccount Guest(string login) =>
        new(ExchangeProtocol.NewDeviceId(), "example.test", 44022, login, new string('g', 43), Enabled: true, Guest: true);

    private static FileStream LockAgainstReaders(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.Delete);

    [Fact]
    public void AFileUnreadableForLessThanTheGraceIsKeptAndTheMutationRefused()
    {
        new ExchangeGuestEntries(_directory).Upsert(Guest("one"));
        var before = File.ReadAllBytes(FilePath);

        using (LockAgainstReaders(FilePath))
        {
            var entries = new ExchangeGuestEntries(_directory);
            Assert.Throws<IOException>(() => entries.Upsert(Guest("two")));
            Assert.False(entries.ProtectionFailed);
            Assert.False(File.Exists(entries.SetAsidePath));
        }

        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal("one", Assert.Single(new ExchangeGuestEntries(_directory).Entries).Login);
    }

    [Fact]
    public void AFileUnreadablePastTheGraceIsSetAsideKeptAndGuestSignInWorksAgain()
    {
        new ExchangeGuestEntries(_directory).Upsert(Guest("one"));
        var before = File.ReadAllBytes(FilePath);
        string setAside;

        using (LockAgainstReaders(FilePath))
        {
            var entries = new ExchangeGuestEntries(_directory) { UnreadableGrace = TimeSpan.Zero };
            entries.Upsert(Guest("two"));

            Assert.True(entries.ProtectionFailed);
            Assert.Equal("two", Assert.Single(entries.Entries).Login);
            setAside = entries.SetAsidePath;
            Assert.True(File.Exists(setAside));
        }

        Assert.Equal(before, File.ReadAllBytes(setAside));
        Assert.Equal("two", Assert.Single(new ExchangeGuestEntries(_directory).Entries).Login);
    }

    [Fact]
    public void AReadThatSucceedsAgainEndsTheRunOfFailures()
    {
        new ExchangeGuestEntries(_directory).Upsert(Guest("one"));
        ExchangeGuestEntries entries;
        using (LockAgainstReaders(FilePath))
        {
            entries = new ExchangeGuestEntries(_directory) { UnreadableGrace = TimeSpan.FromHours(1) };
            Assert.Throws<IOException>(() => entries.Upsert(Guest("two")));
        }

        entries.Upsert(Guest("three")); // the lock is gone: read, then written, nothing set aside

        Assert.False(entries.ProtectionFailed);
        Assert.False(File.Exists(entries.SetAsidePath));
        Assert.Equal(["one", "three"], entries.Entries.Select(entry => entry.Login).Order().ToArray());
    }
}
