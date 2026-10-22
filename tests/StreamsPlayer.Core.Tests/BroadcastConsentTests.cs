using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0217's consent tests, as the acceptance criteria state them: with broadcasting not allowed no
/// start is permitted; "start when the application starts" off means nothing starts; nothing starts a
/// source the user did not choose earlier. The gate is the one place the rules live, so the UI and the
/// service cannot disagree.
/// </summary>
public sealed class BroadcastConsentTests
{
    private static BroadcastProducerSettings Settings(bool allowed = false, bool startWithApp = false,
        string[]? restart = null) => new()
    {
        Allowed = allowed,
        StartWithApplication = startWithApp,
        RestartSources = restart ?? []
    };

    [Fact]
    public void WhileNotAllowed_NothingCanBeStarted()
    {
        Assert.Equal(BroadcastStartRefusal.NotAllowed,
            BroadcastProducerGate.RefusalFor(Settings(), BroadcastSource.Microphone, []));
    }

    [Fact]
    public void AllowedAndExplicit_AStartIsPermitted()
    {
        Assert.Null(BroadcastProducerGate.RefusalFor(Settings(allowed: true), BroadcastSource.Microphone, []));
    }

    [Fact]
    public void EverySourceIsIndependentOfTheOthers()
    {
        // The bound of the tactical plan is the owner's four sources; with three live, the fourth
        // still starts - "independently of the others permitting it" (requirement 1).
        var three = new HashSet<BroadcastSource>
        {
            BroadcastSource.Microphone, BroadcastSource.SystemSound, BroadcastSource.Screen
        };
        Assert.Null(BroadcastProducerGate.RefusalFor(Settings(allowed: true), BroadcastSource.Camera, three));

        // With all four live, the honest refusal is the one about the source itself.
        var four = new HashSet<BroadcastSource>(three) { BroadcastSource.Camera };
        Assert.Equal(BroadcastStartRefusal.AlreadyRunning,
            BroadcastProducerGate.RefusalFor(Settings(allowed: true), BroadcastSource.Camera, four));
    }

    [Fact]
    public void ASourceAlreadyLiveIsRefusedAsSuch()
    {
        Assert.Equal(BroadcastStartRefusal.AlreadyRunning,
            BroadcastProducerGate.RefusalFor(Settings(allowed: true), BroadcastSource.SystemSound,
                [BroadcastSource.SystemSound]));
    }

    [Fact]
    public void WithTheRestartSettingOff_NothingStarts()
    {
        Assert.Empty(BroadcastProducerGate.RestartSources(Settings(startWithApp: false, restart: ["mic"])));
        Assert.Empty(BroadcastProducerGate.RestartSources(Settings(startWithApp: true, restart: [])));
    }

    [Fact]
    public void WhileNotAllowedEvenTheRestartSettingStartsNothing()
    {
        Assert.Empty(BroadcastProducerGate.RestartSources(Settings(allowed: false, startWithApp: true, restart: ["mic"])));
    }

    [Fact]
    public void TheRestartStartsOnlyTheSourcesTheUserChoseEarlier()
    {
        var restart = BroadcastProducerGate.RestartSources(Settings(allowed: true, startWithApp: true,
            restart: ["mic", "system-audio", "not-a-source", "mic"]));
        Assert.Equal([BroadcastSource.Microphone, BroadcastSource.SystemSound], restart);
    }

    [Fact]
    public void AStartChoosesItsSourceForTheNextRestart_AStopUnchoosesIt()
    {
        var settings = Settings(allowed: true);
        settings = settings with { RestartSources = BroadcastProducerGate.ChosenAfter(settings, BroadcastSource.Microphone, started: true) };
        settings = settings with { RestartSources = BroadcastProducerGate.ChosenAfter(settings, BroadcastSource.SystemSound, started: true) };
        Assert.Equal(["mic", "system-audio"], settings.RestartSources);
        settings = settings with { RestartSources = BroadcastProducerGate.ChosenAfter(settings, BroadcastSource.Microphone, started: false) };
        Assert.Equal(["system-audio"], settings.RestartSources);
    }
}

/// <summary>The settings' own bounds: a hand-edited file is foreign input, and each value reads as its default.</summary>
public sealed class BroadcastProducerSettingsTests
{
    private static string TempDirectory() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StreamsPlayer.Core.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DefaultsSatisfyTheContractShapes()
    {
        var store = new BroadcastProducerSettingsStore(TempDirectory());
        var settings = await store.LoadAsync();
        Assert.False(settings.Allowed);
        Assert.False(settings.StartWithApplication);
        Assert.False(settings.LanListening);
        Assert.Equal(8768, settings.LanPort);
        Assert.Equal(44100, settings.SampleRate);
        Assert.Equal(1, settings.MicrophoneChannels);
        Assert.Equal(2, settings.SystemSoundChannels);
        Assert.Equal(128, settings.BitrateKbps);
        Assert.Equal(0, settings.MaxListeners);
        Assert.Empty(settings.RestartSources);
    }

    [Fact]
    public async Task AValueOutsideItsRangeReadsAsItsDefault()
    {
        var directory = TempDirectory();
        var store = new BroadcastProducerSettingsStore(directory);
        var saved = await store.LoadAsync();
        Assert.True(await store.SaveAsync(saved with
        {
            LanPort = 99999,
            SampleRate = 9999,
            MicrophoneChannels = 5,
            SystemSoundChannels = 0,
            BitrateKbps = 9999,
            MaxListeners = -1,
            MicrophoneDeviceId = "  ",
            RestartSources = ["mic", "not-a-source", "mic", "system-audio", "screen", "camera", "extra"]
        }));

        var reloaded = await new BroadcastProducerSettingsStore(directory).LoadAsync();
        Assert.Equal(8768, reloaded.LanPort);
        Assert.Equal(44100, reloaded.SampleRate);
        Assert.Equal(1, reloaded.MicrophoneChannels);
        Assert.Equal(2, reloaded.SystemSoundChannels);
        Assert.Equal(128, reloaded.BitrateKbps);
        Assert.Equal(0, reloaded.MaxListeners);
        Assert.Null(reloaded.MicrophoneDeviceId);
        // The bound of the tactical plan holds even against a hand-picked file.
        Assert.True(reloaded.RestartSources.Count <= BroadcastSources.MaximumSimultaneous);
    }

    [Fact]
    public async Task TheChosenSourcesSurviveARestart()
    {
        var directory = TempDirectory();
        var store = new BroadcastProducerSettingsStore(directory);
        var settings = await store.LoadAsync();
        settings = settings with { Allowed = true, StartWithApplication = true };
        settings = settings with { RestartSources = BroadcastProducerGate.ChosenAfter(settings, BroadcastSource.Microphone, started: true) };
        Assert.True(await store.SaveAsync(settings));

        // The "restart": a fresh store, as the next launch reads it.
        var reloaded = await new BroadcastProducerSettingsStore(directory).LoadAsync();
        Assert.Equal([BroadcastSource.Microphone], BroadcastProducerGate.RestartSources(reloaded));
    }
}
