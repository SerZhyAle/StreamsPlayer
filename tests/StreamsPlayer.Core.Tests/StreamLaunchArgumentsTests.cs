using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0127: a shortcut keeps starting its channel across a refresh that deletes the row and re-adds the
/// same address under a new id.
/// </summary>
public sealed class StreamLaunchArgumentsTests
{
    private const string Address = "https://example.test/live.mp3";
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void For_CarriesTheAddressBesideTheId()
    {
        var channel = Channel(Address);

        Assert.Equal($"--id \"{channel.Id:D}\" --url \"{Address}\"", StreamLaunchArguments.For(channel));
        Assert.True(StreamLaunchArguments.CarriesAddress(channel));
    }

    [Theory]
    [InlineData("rtsp://user:pass@camera.example/x")]
    [InlineData("https://host.example/x?token=abc")]
    [InlineData("http://panel.example/live/user/pass/123.ts")]
    [InlineData("https://host.example/x?a=1&amp;token=abc")]
    public void For_LeavesCredentialAddressesOutAndStillResolvesById(string url)
    {
        var channel = Channel(url);

        var arguments = StreamLaunchArguments.For(channel);
        var request = StreamLaunchRequest.Parse(SplitCommandLine(arguments));

        Assert.Equal($"--id \"{channel.Id:D}\"", arguments);
        Assert.False(StreamLaunchArguments.CarriesAddress(channel));
        Assert.Equal(StreamLaunchTargetKind.ChannelId, request.Kind);
        Assert.Null(request.Url);
        Assert.Same(channel, StreamLaunchArguments.Resolve([channel], request));
    }

    [Theory]
    [InlineData("https://example.test/a b.mp3")]
    [InlineData("https://example.test/a\"b.mp3")]
    [InlineData("https://example.test/a'b.mp3?token=abc")]
    [InlineData("https://example.test/dir\\")]
    [InlineData("file:///c:/music.mp3")]
    public void For_LeavesOutAnAddressTheCommandLineCannotCarry(string url)
    {
        var channel = Channel(url);

        Assert.Equal($"--id \"{channel.Id:D}\"", StreamLaunchArguments.For(channel));
    }

    [Fact]
    public void For_LeavesOutAnAddressThatWouldOverflowAShortcut()
    {
        var channel = Channel("https://example.test/" + new string('a', StreamLaunchArguments.MaximumLength));

        Assert.Equal($"--id \"{channel.Id:D}\"", StreamLaunchArguments.For(channel));
    }

    [Fact]
    public void For_RoundTripsThroughTheParser()
    {
        var channel = Channel(Address);

        var request = StreamLaunchRequest.Parse(SplitCommandLine(StreamLaunchArguments.For(channel)));

        Assert.Equal(StreamLaunchTargetKind.ChannelId, request.Kind);
        Assert.Equal(channel.Id, request.ChannelId);
        Assert.Equal(Address, request.Url);
    }

    [Fact]
    public void Resolve_PrefersTheRowWithTheId()
    {
        var target = Channel(Address);
        var sameAddress = Channel(Address);

        var resolved = StreamLaunchArguments.Resolve([sameAddress, target], new StreamLaunchRequest(StreamLaunchTargetKind.ChannelId, target.Url, target.Id));

        Assert.Same(target, resolved);
    }

    /// <summary>The acceptance case: the bank drops the row, a refresh deletes it, a later one re-adds it.</summary>
    [Fact]
    public void Resolve_FindsTheChannelByAddressAfterARefreshReplacedItsRow()
    {
        var entry = new CatalogEntry("Live", Address, MediaKind.Audio, "News", "World", "english", "MT", null, 2);
        var original = Assert.Single(CatalogMerger.Merge([], [entry], Now).Channels);
        var request = StreamLaunchRequest.Parse(SplitCommandLine(StreamLaunchArguments.For(original)));

        var dropped = CatalogMerger.Merge([original], [], Now.AddDays(1)).Channels;
        Assert.Empty(dropped);
        var readded = Assert.Single(CatalogMerger.Merge(dropped, [entry], Now.AddDays(2)).Channels);
        Assert.NotEqual(original.Id, readded.Id);

        Assert.Same(readded, StreamLaunchArguments.Resolve([readded], request));
    }

    [Fact]
    public void Resolve_MatchesTheAddressByIdentityNotSpelling()
    {
        var row = Channel("HTTPS://Example.TEST/live.mp3");

        var resolved = StreamLaunchArguments.Resolve([row], new StreamLaunchRequest(StreamLaunchTargetKind.ChannelId, Address, Guid.NewGuid()));

        Assert.Same(row, resolved);
    }

    [Fact]
    public void Resolve_PrefersALiveRowOverARetiredOneAtTheSameAddress()
    {
        var retired = Channel(Address) with { RetiredAt = Now };
        var live = Channel(Address);

        var resolved = StreamLaunchArguments.Resolve([retired, live], new StreamLaunchRequest(StreamLaunchTargetKind.ChannelId, Address, Guid.NewGuid()));

        Assert.Same(live, resolved);
    }

    [Fact]
    public void Resolve_IdOnlyLaunchOfAMissingRow_ResolvesNothing() =>
        Assert.Null(StreamLaunchArguments.Resolve(
            [Channel(Address)],
            new StreamLaunchRequest(StreamLaunchTargetKind.ChannelId, ChannelId: Guid.NewGuid())));

    [Fact]
    public void Names_RecognisesTheChannelsOwnShortcutOnly()
    {
        var channel = Channel(Address);
        var arguments = StreamLaunchArguments.For(channel);

        Assert.True(StreamLaunchArguments.Names(arguments, channel.Id));
        Assert.False(StreamLaunchArguments.Names(arguments, Guid.NewGuid()));
        Assert.False(StreamLaunchArguments.Names(null, channel.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_IdAndUrl_ReturnsChannelTargetWithFallbackAddress(bool idFirst)
    {
        var id = Guid.NewGuid();
        string[] arguments = idFirst
            ? ["--id", id.ToString(), "--url", Address]
            : ["--url", Address, "--id", id.ToString()];

        var request = StreamLaunchRequest.Parse(arguments);

        Assert.Equal(StreamLaunchTargetKind.ChannelId, request.Kind);
        Assert.Equal(id, request.ChannelId);
        Assert.Equal(Address, request.Url);
    }

    [Fact]
    public void Parse_IdAlone_HasNoFallbackAddress() =>
        Assert.Null(StreamLaunchRequest.Parse(["--id", Guid.NewGuid().ToString()]).Url);

    [Fact]
    public void Parse_RepeatedId_ReturnsInvalid() =>
        Assert.Equal(
            StreamLaunchTargetKind.Invalid,
            StreamLaunchRequest.Parse(["--id", Guid.NewGuid().ToString(), "--id", Guid.NewGuid().ToString()]).Kind);

    [Fact]
    public void Parse_RepeatedUrl_ReturnsInvalid() =>
        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse(["--url", Address, "--url", Address]).Kind);

    [Fact]
    public void Parse_IdWithUnknownOption_ReturnsInvalid() =>
        Assert.Equal(
            StreamLaunchTargetKind.Invalid,
            StreamLaunchRequest.Parse(["--id", Guid.NewGuid().ToString(), "--other", "value"]).Kind);

    [Fact]
    public void Parse_IdWithUnlaunchableUrl_ReturnsInvalid() =>
        Assert.Equal(
            StreamLaunchTargetKind.Invalid,
            StreamLaunchRequest.Parse(["--id", Guid.NewGuid().ToString(), "--url", "file:///c:/x.mp3"]).Kind);

    [Fact]
    public void Parse_ThreeArguments_ReturnsInvalid() =>
        Assert.Equal(
            StreamLaunchTargetKind.Invalid,
            StreamLaunchRequest.Parse(["--id", Guid.NewGuid().ToString(), "--url"]).Kind);

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Live",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = Now
    };

    /// <summary>Enough of the Windows rules for the arguments this class writes: quoted runs, no escapes.</summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (character == ' ' && !quoted)
            {
                if (any)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(character);
                any = true;
            }
        }

        if (any)
        {
            arguments.Add(current.ToString());
        }

        return [.. arguments];
    }
}
