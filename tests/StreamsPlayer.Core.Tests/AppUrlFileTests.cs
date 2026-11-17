using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0279, APP-COMMANDS item H: an address that may carry a credential is also taken from a file the caller names. These
// decide what the file means, what every problem with it answers, and that no answer carries the address or the path.
public sealed class AppUrlFileTests : IDisposable
{
    private const string Secret = "hunter2";
    private const string Radio = "https://listener:" + Secret + "@example.org/live.mp3";
    private const string NotLaunchable = "that is not a stream address";
    private const string ChannelId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sp0279-urlfile-" + Guid.NewGuid().ToString("N"));

    public AppUrlFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder that cannot be removed is the temp folder's to clean.
        }
    }

    private string Write(string name, string content, bool byteOrderMark = false)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(byteOrderMark));
        return path;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Options(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => (IReadOnlyList<string>)new[] { pair.Value }, StringComparer.OrdinalIgnoreCase);

    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    // ---- what the file means ------------------------------------------------------------------------------------------

    [Fact]
    public void NoUrlFileLeavesTheOptionsAsTheyWere()
    {
        var options = Options(("url", Radio));

        var result = AppUrlFile.Resolve("r", options, null, NotLaunchable);

        Assert.Same(options, result.Options);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void TheFirstLineBecomesTheUrlAndTheFileOptionIsDropped()
    {
        var path = Write("address.txt", Radio + "\r\nthe second line is ignored\r\n", byteOrderMark: true);

        var result = AppUrlFile.Resolve("r", Options(("url-file", path), ("background", "true")), null, NotLaunchable);

        Assert.Null(result.Refusal);
        Assert.Equal(Radio, AppPlaybackCommands.Last(result.Options, "url"));
        Assert.False(result.Options.ContainsKey("url-file"));
        Assert.Equal("true", AppPlaybackCommands.Last(result.Options, "background"));
        Assert.Equal(["--url", Radio], AppPlaybackCommands.LaunchArguments(result.Options, null));
    }

    [Fact]
    public void AChannelIdKeepsTheAddressFromTheFileAsItsFallback()
    {
        var path = Write("fallback.txt", Radio);

        var result = AppUrlFile.Resolve("r", Options(("id", ChannelId), ("url-file", path)), null, NotLaunchable);

        Assert.Equal(["--id", ChannelId, "--url", Radio], AppPlaybackCommands.LaunchArguments(result.Options, null));
    }

    [Fact]
    public void ARelativePathIsTakenFromTheCallersWorkingFolder()
    {
        Write("relative.txt", Radio);

        var result = AppUrlFile.Resolve("r", Options(("url-file", "relative.txt")), _folder, NotLaunchable);

        Assert.Null(result.Refusal);
        Assert.Equal(Radio, AppPlaybackCommands.Last(result.Options, "url"));
    }

    [Fact]
    public async Task TheFirstInstanceReadsItOffTheCallingThreadAndAnAbsentOptionCompletesAtOnce()
    {
        var path = Write("async.txt", Radio);

        var withFile = await AppUrlFile.ResolveAsync("r", Options(("url-file", path)), null, NotLaunchable);
        var without = AppUrlFile.ResolveAsync("r", Options(("url", Radio)), null, NotLaunchable);

        Assert.Equal(Radio, AppPlaybackCommands.Last(withFile.Options, "url"));
        Assert.True(without.IsCompletedSuccessfully);
    }

    // ---- what every problem answers, and what no answer carries -------------------------------------------------------

    [Fact]
    public void AMissingFileIsFailedTargetMissingExit11AndTheMessageNamesNoPath()
    {
        var path = Path.Combine(_folder, "nowhere-to-be-found.txt");

        var refusal = AppUrlFile.Resolve("r", Options(("url-file", path)), null, NotLaunchable).Refusal!;

        Assert.Equal(AppCommandStatus.Failed, refusal.Status);
        Assert.Equal(AppCommandReason.TargetMissing, refusal.Reason);
        Assert.Equal(AppCommandExit.NotReady, refusal.ExitCode);
        Assert.Equal(AppUrlFileMessages.Missing, refusal.Message);
        Assert.DoesNotContain("nowhere-to-be-found", refusal.ToJsonLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeReadIsFailedIoErrorExit14()
    {
        // A folder is not a file: opening it for reading is refused by the file system.
        var refusal = AppUrlFile.Resolve("r", Options(("url-file", _folder)), null, NotLaunchable).Refusal!;

        Assert.Equal(AppCommandStatus.Failed, refusal.Status);
        Assert.Equal(AppCommandReason.IoError, refusal.Reason);
        Assert.Equal(AppCommandExit.Io, refusal.ExitCode);
        Assert.Equal(AppUrlFileMessages.Unreadable, refusal.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n" + Radio)]
    [InlineData("   \r\n")]
    public void AnEmptyFileOrABlankFirstLineIsRefusedBadArgumentExit10(string content)
    {
        var path = Write("empty-" + Guid.NewGuid().ToString("N") + ".txt", content);

        var refusal = AppUrlFile.Resolve("r", Options(("url-file", path)), null, NotLaunchable).Refusal!;

        Assert.Equal(AppCommandStatus.Refused, refusal.Status);
        Assert.Equal(AppCommandReason.BadArgument, refusal.Reason);
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
        Assert.Equal(AppUrlFileMessages.Empty, refusal.Message);
    }

    [Fact]
    public void AFileOverTheBoundOfOneArgumentIsNotAnAddress()
    {
        var path = Write("huge.txt", "https://example.org/" + new string('a', AppCommandLimits.MaximumArgumentBytes));

        var refusal = AppUrlFile.Resolve("r", Options(("url-file", path)), null, NotLaunchable).Refusal!;

        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
    }

    [Fact]
    public void AnAddressThatIsNotAStreamAddressIsRefusedAndNoAnswerEchoesIt()
    {
        var path = Write("ftp.txt", "ftp://user:" + Secret + "@example.org/file.mp3");

        var refusal = AppUrlFile.Resolve("r", Options(("url-file", path)), null, NotLaunchable).Refusal!;

        Assert.Equal(AppCommandReason.BadArgument, refusal.Reason);
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
        Assert.Equal(NotLaunchable, refusal.Message);
        Assert.DoesNotContain(Secret, refusal.ToJsonLine(), StringComparison.Ordinal);
    }

    // ---- the parser: the option is an address target, never a second one ----------------------------------------------

    [Fact]
    public void PlayTakesAUrlFileAsItsAddressTargetOrAsTheFallbackOfAChannelId()
    {
        Assert.Equal(AppCommandParseKind.Verb, Parse("play", "--url-file", "address.txt").Kind);
        Assert.Equal(AppCommandParseKind.Verb, Parse("play", "--id", ChannelId, "--url-file", "address.txt").Kind);
        Assert.Equal("address.txt", Parse("play", "--url-file", "address.txt").Value("url-file"));
    }

    [Theory]
    [InlineData("play", "--url", Radio, "--url-file", "address.txt")]
    [InlineData("play", "--url-file", "a.txt", "--url-file", "b.txt")]
    [InlineData("play", "--file", "x.spres", "--url-file", "address.txt")]
    [InlineData("play", "--url-file")]
    public void PlayRefusesAnAddressGivenTwiceOrMixedWithAFileTarget(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
    }

    [Fact]
    public void ChannelAddTakesExactlyOneOfUrlAndUrlFile()
    {
        Assert.Equal(AppCommandParseKind.Verb, Parse("channel", "add", "--url-file", "address.txt").Kind);
        Assert.Equal(AppCommandParseKind.Verb, Parse("channel", "add", "--url", Radio).Kind);
        Assert.Equal(AppCommandParseKind.Refused, Parse("channel", "add").Kind);
        Assert.Equal(AppCommandParseKind.Refused, Parse("channel", "add", "--url", Radio, "--url-file", "address.txt").Kind);
        Assert.Equal(AppCommandParseKind.Refused, Parse("channel", "add", "--url-file", "a.txt", "--url-file", "b.txt").Kind);
    }
}
