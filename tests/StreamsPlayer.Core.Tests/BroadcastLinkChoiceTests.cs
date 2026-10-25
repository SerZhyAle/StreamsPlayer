using System.Text.Json;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0223 requirements 4 and 6 in Core: a guest session is offered no source, the link's password is the person's
/// choice (LIVE-BROADCAST item R) and never part of the settings file, and a description carries the `auth` marker with a
/// user name and nothing a reader could test a guess against (item M).
/// </summary>
public sealed class BroadcastLinkChoiceTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "StreamsPlayer.Core.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(BroadcastSource.Microphone)]
    [InlineData(BroadcastSource.SystemSound)]
    [InlineData(BroadcastSource.Screen)]
    [InlineData(BroadcastSource.Camera)]
    public void ASignedInGuestIsOfferedNoSource(BroadcastSource source)
    {
        var settings = new BroadcastProducerSettings { Allowed = true };

        Assert.Equal(BroadcastStartRefusal.GuestAccount, BroadcastProducerGate.RefusalFor(settings, source, [], guestOnly: true));
        Assert.Null(BroadcastProducerGate.RefusalFor(settings, source, [], guestOnly: false));
    }

    [Fact]
    public void WithoutThePermissionTheConsentRefusalComesBeforeTheGuestOne()
    {
        Assert.Equal(BroadcastStartRefusal.NotAllowed,
            BroadcastProducerGate.RefusalFor(new BroadcastProducerSettings(), BroadcastSource.Screen, [], guestOnly: true));
    }

    [Fact]
    public void TheDefaultChoiceIsAPasswordMadeForEachBroadcast()
    {
        Assert.Equal(BroadcastLinkChoice.Generated, new BroadcastProducerSettings().Choice);
    }

    [Theory]
    [InlineData("custom", BroadcastLinkChoice.Custom)]
    [InlineData("none", BroadcastLinkChoice.None)]
    [InlineData("generated", BroadcastLinkChoice.Generated)]
    [InlineData("anything else", BroadcastLinkChoice.Generated)]
    [InlineData("", BroadcastLinkChoice.Generated)]
    public async Task TheChoiceSurvivesARestartAndAnUnknownValueReadsAsTheDefault(string stored, BroadcastLinkChoice expected)
    {
        var directory = TempDirectory();
        var store = new BroadcastProducerSettingsStore(directory);
        Assert.True(await store.SaveAsync(new BroadcastProducerSettings { LinkChoice = stored }));

        var reloaded = await new BroadcastProducerSettingsStore(directory).LoadAsync();

        Assert.Equal(expected, reloaded.Choice);
    }

    [Fact]
    public async Task TheSettingsFileNeverHoldsAPassword()
    {
        var directory = TempDirectory();
        var store = new BroadcastProducerSettingsStore(directory);
        Assert.True(await store.SaveAsync(new BroadcastProducerSettings
        {
            LinkChoice = BroadcastProducerSettings.LinkChoiceName(BroadcastLinkChoice.Custom)
        }));

        var text = await File.ReadAllTextAsync(store.FilePath);

        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"linkChoice\":\"custom\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyDeviceIdReadsAsTheFirstDevice()
    {
        var directory = TempDirectory();
        var store = new BroadcastProducerSettingsStore(directory);
        Assert.True(await store.SaveAsync(new BroadcastProducerSettings { MonitorDeviceId = " ", CameraDeviceId = "" }));

        var reloaded = await new BroadcastProducerSettingsStore(directory).LoadAsync();

        Assert.Null(reloaded.MonitorDeviceId);
        Assert.Null(reloaded.CameraDeviceId);
    }

    private static BroadcastWirePlan Plan(string? authUser) => new(
        "bcast-id", "device-id", "Test screen", BroadcastSource.Screen, "192.168.1.5", 8768, "opaque-token", "example.test:44022",
        Relay: true, Tunnel: true, "SHA256:abcdefghij", 44100, 1, 128, AuthUser: authUser);

    [Fact]
    public void AProtectedDescriptorCarriesAMarkerWithAUserAndNoSecret()
    {
        var json = BroadcastDescriptorWriter.Write(Plan(BroadcastLinkPassword.LinkUser));

        var read = FastMediaSorterBroadcastDescriptor.Read(json);
        Assert.True(read.IsAccepted, read.Status.ToString());
        Assert.Equal(new FastMediaSorterBroadcastAuth("password-v1", BroadcastLinkPassword.LinkUser), read.Broadcast!.Auth);

        using var document = JsonDocument.Parse(json);
        var members = document.RootElement.GetProperty("auth").EnumerateObject().Select(member => member.Name).Order().ToArray();
        Assert.Equal(["scheme", "user"], members);
    }

    [Fact]
    public void ADescriptorOfAnOpenLinkCarriesNoMarker()
    {
        var json = BroadcastDescriptorWriter.Write(Plan(authUser: null));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("auth", out _));
        Assert.Null(FastMediaSorterBroadcastDescriptor.Read(json).Broadcast!.Auth);
    }
}
