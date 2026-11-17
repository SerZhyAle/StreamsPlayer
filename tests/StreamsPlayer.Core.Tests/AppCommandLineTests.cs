using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0271: APP-COMMANDS rules 1 to 3 and case C4 - one strict parser, bounded input, the frozen launch grammar untouched.
public sealed class AppCommandLineTests
{
    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    [Fact]
    public void NoArgumentIsTheEmptyAliasOfShow()
    {
        var parse = Parse();

        Assert.Equal(AppCommandParseKind.Alias, parse.Kind);
        Assert.Equal("show", parse.Entry!.Name);
    }

    // SP-0272: the frozen launch spellings are the aliases of play (rule 2) and mean exactly what the shipped parser
    // always made of them: the same Kind and the same value.
    [Theory]
    [InlineData("--url", "https://example.test/a")]
    [InlineData("--id", "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("--id", "0f8fad5b-d9cb-469f-a165-70867728950e", "--url", "https://example.test/a")]
    [InlineData("--url", "https://example.test/a", "--id", "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData(@"C:\Users\me\Radio.spres")]
    [InlineData("radio.spres")]
    public void TheLaunchSpellingsAreTheAliasesOfPlay(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.Equal("play", parse.Entry!.Name);
        Assert.Equal(StreamLaunchRequest.Parse(args), AppPlaybackCommands.ToLaunchRequest(parse.Options, workingDirectory: null));
    }

    [Fact]
    public void AnOversizedLaunchKeepsItsOwnPathAndLimits()
    {
        Assert.Equal(AppCommandParseKind.Launch, Parse("--url", new string('x', 70_000)).Kind);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("STATUS")]
    [InlineData("Status")]
    public void VerbsAreComparedWithoutRegardToCase(string verb)
    {
        var parse = Parse(verb);

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.Equal("status", parse.Entry!.Name);
    }

    [Fact]
    public void OptionsAreReadByTheManifestAndKeptAsTyped()
    {
        var parse = Parse("status", "--JSON", "--result", "Out File.json");

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.True(parse.Has("json"));
        Assert.Equal("Out File.json", parse.Value("result"));
        Assert.Equal(["--JSON", "--result", "Out File.json"], parse.ArgumentsAfterVerb);
    }

    [Fact]
    public void AnOptionValueMayFollowAnEqualsSign()
    {
        Assert.Equal("a.json", Parse("status", "--result=a.json").Value("result"));
    }

    [Theory]
    [InlineData(AppCommandReason.UnknownVerb, "frobnicate")]
    [InlineData(AppCommandReason.BadArgument, "status", "--bogus")]
    [InlineData(AppCommandReason.BadArgument, "status", "--result")]
    [InlineData(AppCommandReason.BadArgument, "status", "--json=maybe")]
    [InlineData(AppCommandReason.BadArgument, "show", "extra")]
    [InlineData(AppCommandReason.BadArgument, "show", "--json")]
    [InlineData(AppCommandReason.BadArgument, "quit", "--", "--json")]
    public void AnythingTheManifestDoesNotDeclareIsRefusedWithExit10(string reason, params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(reason, parse.Reason);
        var refusal = parse.ToRefusal("id");
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
        Assert.Equal(AppCommandStatus.Refused, refusal.Status);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
    }

    [Fact]
    public void AnArgumentOver32KiBIsRefusedNotTruncated()
    {
        var parse = Parse("status", "--result", new string('x', AppCommandLimits.MaximumArgumentBytes + 1));

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.TooLarge, parse.Reason);
    }

    [Fact]
    public void ARefusalMessageNeverEchoesALongValue()
    {
        var parse = Parse(new string('a', 500));

        Assert.True(parse.Message!.Length < 200, parse.Message);
    }

    [Fact]
    public void ARequestSurvivesTheWireUnchanged()
    {
        var request = new AppCommandRequest("status", ["--json", "--result", "ü.json"], Guid.NewGuid().ToString("D"))
        {
            WorkingDir = @"C:\work",
            CallerPid = 4242
        };

        var read = AppCommandRequest.Read(request.Serialize());

        Assert.Equal(AppCommandLineKind.Request, read.Kind);
        Assert.Equal(request.RequestId, read.RequestId);
        Assert.Equal("status", read.Request!.Command);
        Assert.Equal(request.Args, read.Request.Args);
        Assert.Equal(@"C:\work", read.Request.WorkingDir);
        Assert.Equal(4242, read.Request.CallerPid);
        Assert.Equal("cli", read.Request.Origin);
    }

    [Fact]
    public void TheFrozenLaunchLineIsNotAProfileLine()
    {
        Assert.Equal(AppCommandLineKind.Legacy, AppCommandRequest.Read(ActivationMessage.Serialize(["--url", "https://example.test/a"])).Kind);
    }
}
