using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0118: the frozen lock and pipe names (APP-ACTIVATION rules 2 and 3).
public sealed class SingleInstanceIdentityTests
{
    private static readonly string DefaultDirectory = Path.Combine(Path.GetTempPath(), "sp-identity", "StreamsPlayer");

    [Fact]
    public void DefaultProfile_UsesTheFrozenContractNames()
    {
        var identity = SingleInstanceIdentity.For(DefaultDirectory, DefaultDirectory, sessionId: 3);

        // These literals are frozen for the product's life: a changed name lets two copies run at once.
        Assert.Equal(@"Local\StreamsPlayerSingleInstance", identity.MutexName);
        Assert.Equal("sza-streamsplayer-single-instance-s3", identity.PipeName);
    }

    [Fact]
    public void DefaultProfile_IgnoresCaseAndTrailingSeparators()
    {
        var spelledDifferently = DefaultDirectory.ToLowerInvariant() + Path.DirectorySeparatorChar;

        Assert.Equal(
            SingleInstanceIdentity.For(DefaultDirectory, DefaultDirectory, 1),
            SingleInstanceIdentity.For(spelledDifferently, DefaultDirectory, 1));
    }

    [Fact]
    public void SessionsNeverShareAPipe()
    {
        Assert.NotEqual(
            SingleInstanceIdentity.For(DefaultDirectory, DefaultDirectory, 1).PipeName,
            SingleInstanceIdentity.For(DefaultDirectory, DefaultDirectory, 2).PipeName);
    }

    [Fact]
    public void RelocatedProfile_IsAnInstanceOfItsOwn()
    {
        var relocated = Path.Combine(Path.GetTempPath(), "sp-identity", "smoke-profile");

        var identity = SingleInstanceIdentity.For(relocated, DefaultDirectory, 1);
        var standard = SingleInstanceIdentity.For(DefaultDirectory, DefaultDirectory, 1);

        Assert.NotEqual(standard.MutexName, identity.MutexName);
        Assert.NotEqual(standard.PipeName, identity.PipeName);
        Assert.StartsWith(SingleInstanceIdentity.ProductMutexName + "-p", identity.MutexName);
        Assert.Equal(identity, SingleInstanceIdentity.For(relocated, DefaultDirectory, 1));
    }
}
