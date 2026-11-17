using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0271: APP-COMMANDS rule 10 and cases C2, C8, C10 - what happens with nothing running, and the rule-13 gate.
public sealed class AppCommandColdStartTests
{
    private static AppCommandEntry Entry(string name) => AppCommandManifest.Embedded.Find(name)!;

    [Theory]
    [InlineData("show", AppCommandColdStart.Start)]
    [InlineData("commands", AppCommandColdStart.Oneshot)]
    [InlineData("version", AppCommandColdStart.Oneshot)]
    [InlineData("quit", AppCommandColdStart.Oneshot)]
    [InlineData("status", AppCommandColdStart.Refuse)]
    public void EveryBaseCommandHasItsDeclaredColdStart(string verb, AppCommandColdStart expected)
    {
        Assert.Equal(expected, AppCommandPolicy.ColdStartOf(Entry(verb)));
    }

    [Fact]
    public void StatusWithNothingRunningIsNotRunningExit11()
    {
        var response = AppCommandPolicy.NotRunning("id");

        Assert.Equal(AppCommandExit.NotReady, response.ExitCode);
        Assert.Equal(AppCommandReason.NotRunning, response.Reason);
        Assert.Equal(AppCommandStatus.Refused, response.Status);
    }

    [Fact]
    public void ASafeVerbIsAcceptedWhileTheSettingIsOff()
    {
        Assert.All(AppCommandManifest.Embedded.Commands.Where(command => command.Risk == "safe"),
            command => Assert.Null(AppCommandPolicy.CheckRisk("id", command, restrictedAllowed: false, "The setting")));
    }

    // SP-0272: play stays safe so a shortcut or a double-clicked .spres works with the setting off; only the status
    // listing, which puts a channel's title in a response, is above safe.
    [Fact]
    public void PlayIsAcceptedAndPlaybackStatusIsSwitchOffExit12WhileTheSettingIsOff()
    {
        Assert.Null(AppCommandPolicy.CheckRisk("id", Entry("play"), restrictedAllowed: false, "The setting"));

        var refusal = AppCommandPolicy.CheckRisk("id", Entry("playback status"), restrictedAllowed: false, "The setting");

        Assert.NotNull(refusal);
        Assert.Equal(AppCommandExit.Permission, refusal.ExitCode);
        Assert.Equal(AppCommandReason.SwitchOff, refusal.Reason);
        Assert.Contains("The setting", refusal.Message, StringComparison.Ordinal);
        Assert.Null(AppCommandPolicy.CheckRisk("id", Entry("playback status"), restrictedAllowed: true, "The setting"));
    }

    [Theory]
    [InlineData("play", AppCommandColdStart.Start)]
    [InlineData("playback stop", AppCommandColdStart.Oneshot)]
    [InlineData("playback pause", AppCommandColdStart.Refuse)]
    [InlineData("playback resume", AppCommandColdStart.Refuse)]
    [InlineData("playback status", AppCommandColdStart.Refuse)]
    [InlineData("volume set", AppCommandColdStart.Refuse)]
    public void EveryPlaybackCommandHasItsDeclaredColdStart(string verb, AppCommandColdStart expected)
    {
        Assert.Equal(expected, AppCommandPolicy.ColdStartOf(Entry(verb)));
    }

    // SP-0273: a recording needs a session, and it writes the user's files - so no cold start and no recording while the
    // setting of rule 13 is off.
    [Theory]
    [InlineData("record start")]
    [InlineData("record stop")]
    public void ARecordVerbHasNoColdStartAndIsSwitchOffExit12WhileTheSettingIsOff(string verb)
    {
        Assert.Equal(AppCommandColdStart.Refuse, AppCommandPolicy.ColdStartOf(Entry(verb)));

        var refusal = AppCommandPolicy.CheckRisk("id", Entry(verb), restrictedAllowed: false, "The setting");

        Assert.NotNull(refusal);
        Assert.Equal(AppCommandExit.Permission, refusal.ExitCode);
        Assert.Equal(AppCommandReason.SwitchOff, refusal.Reason);
        Assert.Contains("The setting", refusal.Message, StringComparison.Ordinal);
        Assert.Null(AppCommandPolicy.CheckRisk("id", Entry(verb), restrictedAllowed: true, "The setting"));
    }

    [Theory]
    [InlineData("reads-user-data")]
    [InlineData("writes-user-data")]
    [InlineData("leaves-machine")]
    public void AVerbAboveSafeIsSwitchOffExit12NamingTheSetting(string risk)
    {
        var entry = Entry("status") with { Name = "probe", Risk = risk };

        var refused = AppCommandPolicy.CheckRisk("id", entry, restrictedAllowed: false, "Let other programs ask");

        Assert.NotNull(refused);
        Assert.Equal(AppCommandExit.Permission, refused.ExitCode);
        Assert.Equal(AppCommandReason.SwitchOff, refused.Reason);
        Assert.Contains("Let other programs ask", refused.Message, StringComparison.Ordinal);
        Assert.Null(AppCommandPolicy.CheckRisk("id", entry, restrictedAllowed: true, "Let other programs ask"));
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndNeverExchanged()
    {
        Assert.False(new CatalogState().AcceptsRestrictedCommands);
        Assert.Contains("acceptsRestrictedCommands", StandardSettings.LocalOnlyKeys);
        Assert.DoesNotContain(StandardSettings.Declarations,
            declaration => string.Equals(declaration.Key, "acceptsRestrictedCommands", StringComparison.OrdinalIgnoreCase));
    }
}
