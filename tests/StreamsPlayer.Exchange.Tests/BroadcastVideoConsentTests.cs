using System.IO;
using System.Runtime.InteropServices;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>SP-0223: the consent gates of the screen and the camera, and the way their failures read.</summary>
public sealed class BroadcastVideoConsentTests
{
    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));

        internal TestDirectory() => Directory.CreateDirectory(Path);

        internal void Save(ExchangeAccount account) => new ExchangeAccountStore(Path).Save(account);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Theory]
    [InlineData(BroadcastSource.Screen)]
    [InlineData(BroadcastSource.Camera)]
    public async Task WithBroadcastingNotAllowed_NoVideoSourceStartsAndNothingRuns(BroadcastSource source)
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);

        Assert.False(producer.Settings.Allowed);
        Assert.Equal(BroadcastStartRefusal.NotAllowed, producer.RefusalFor(source));
        Assert.False(await producer.StartAsync(source));
        Assert.Equal("BroadcastNotAllowed", producer.OutcomeKey);
        Assert.Empty(producer.Running);
    }

    [Theory]
    [InlineData(BroadcastSource.Screen)]
    [InlineData(BroadcastSource.Camera)]
    [InlineData(BroadcastSource.Microphone)]
    public async Task SignedInOnlyAsAGuest_NoSourceIsOfferedOrStarted(BroadcastSource source)
    {
        using var scope = new TestDirectory();
        var account = new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", 44022, "guest-login", new string('g', 43),
            "SHA256:" + new string('0', 64), Enabled: true) with { Guest = true };
        scope.Save(account);
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        await producer.UpdateSettingsAsync(settings => settings with { Allowed = true, LanListening = true });

        Assert.True(producer.IsGuestOnly);
        Assert.Equal(BroadcastStartRefusal.GuestAccount, producer.RefusalFor(source));
        Assert.False(await producer.StartAsync(source));
        Assert.Equal("BroadcastGuestRefused", producer.OutcomeKey);
        Assert.Empty(producer.Running);
    }

    [Fact]
    public void ARefusedWindowsCameraPermission_IsItsOwnMessageAndNeverAnUpload()
    {
        var denied = new COMException("denied", unchecked((int)0x80070005));
        var failure = WindowsVideoBroadcast.Failure(denied);

        Assert.Equal(BroadcastCaptureFailure.WindowsPermissionDenied, failure);
        Assert.Equal("BroadcastPermissionDenied", BroadcastProducerService.CaptureFailureKey(failure));
    }

    [Fact]
    public void ACameraAnotherProgramHolds_IsNamedAsSuchOnTheFirstRead()
    {
        var held = new COMException("held", unchecked((int)0xc00d3704));

        Assert.Equal(BroadcastCaptureFailure.DeviceInUse, WindowsVideoBroadcast.Failure(held));
        Assert.Equal("BroadcastDeviceInUse", BroadcastProducerService.CaptureFailureKey(BroadcastCaptureFailure.DeviceInUse));
    }
}
