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

    [Fact]
    public void ForPowerShell_StartsWithTheCallOperatorAndSingleQuotesThePath()
    {
        var channel = Channel(Address);

        Assert.Equal(
            $"& 'C:\\Users\\O''Neil $x\\StreamsPlayer.exe' --id \"{channel.Id:D}\" --url \"{Address}\"",
            StreamLaunchArguments.ForPowerShell("C:\\Users\\O'Neil $x\\StreamsPlayer.exe", channel));
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
    [InlineData("https://example.test/$(calc)")]
    [InlineData("https://example.test/live?x=$HOME")]
    [InlineData("https://example.test/a`b.mp3")]
    [InlineData("https://example.test/%USERNAME%.mp3")]
    [InlineData("https://example.test/%DATE%/live.mp3")]
    [InlineData("https://example.test/%CD%")]
    [InlineData("https://example.test/%C3%A9/%USERNAME%.mp3")]
    public void For_LeavesOutAnAddressTheCommandLineCannotCarry(string url)
    {
        var channel = Channel(url);

        Assert.Equal($"--id \"{channel.Id:D}\"", StreamLaunchArguments.For(channel));
    }

    // SP-0184 (A16-2): non-ASCII text in a path is percent-encoded, and "%C3%A9t%C3%A9" is not a cmd variable.
    [Theory]
    [InlineData("https://example.test/caf%C3%A9.mp3")]
    [InlineData("https://example.test/%C3%A9t%C3%A9/live.mp3")]
    [InlineData("https://example.test/%E4%B8%AD%E6%96%87/stream")]
    [InlineData("https://example.test/a%20b%20c")]
    public void For_KeepsTheAddressOfAPercentEncodedPath(string url)
    {
        var channel = Channel(url);

        Assert.True(StreamLaunchArguments.CarriesAddress(channel));
        Assert.Equal($"--id \"{channel.Id:D}\" --url \"{url}\"", StreamLaunchArguments.For(channel));
    }
    /// <summary>
    /// Audit 26.1010.0106 A1: PowerShell reads U+2018..U+201B as single quotes and U+201C..U+201E as double
    /// quotes, so such a mark ends the quoted --url and "calc" would run when the command is pasted.
    /// </summary>
    [Theory]
    [InlineData("http://h/a\u2018;calc;\u2018")]
    [InlineData("http://h/a\u2019;calc;\u2019")]
    [InlineData("http://h/a\u201A;calc;\u201A")]
    [InlineData("http://h/a\u201B;calc;\u201B")]
    [InlineData("http://h/a\u201C;calc;\u201C")]
    [InlineData("http://h/a\u201D;calc;\u201D")]
    [InlineData("http://h/a\u201E;calc;\u201E")]
    [InlineData("http://h/a\u201F;calc;\u201F")]
    public void For_LeavesOutAnAddressWithATypographicQuote(string url)
    {
        var channel = Channel(url);

        Assert.False(StreamLaunchArguments.CarriesAddress(channel));
        var arguments = StreamLaunchArguments.For(channel);
        Assert.Equal($"--id \"{channel.Id:D}\"", arguments);
        Assert.DoesNotContain("calc", StreamLaunchArguments.ForPowerShell("C:\\app\\StreamsPlayer.exe", channel));
    }

    /// <summary>
    /// The audit asked which other characters are special inside a double-quoted native argument. In
    /// PowerShell only the quotes, <c>$</c> and the backtick are; cmd adds <c>%NAME%</c>. The rest stay literal.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/a&b;c(d){e},f@g?x=1#frag")]
    [InlineData("https://example.test/a^b!c~d")]
    [InlineData("https://xn--e1afmkfd.xn--p1ai/live/stream.mp3")]
    [InlineData("https://\u043F\u0440\u0438\u043C\u0435\u0440.\u0440\u0444/\u0440\u0430\u0434\u0438\u043E.mp3")]
    [InlineData("https://example.test/caf%C3%A9?name=%D0%B0%D0%B1")]
    public void For_KeepsTheAddressOfALiteralOrInternationalPath(string url)
    {
        var channel = Channel(url);

        Assert.True(StreamLaunchArguments.CarriesAddress(channel));
        Assert.Equal($"--id \"{channel.Id:D}\" --url \"{url}\"", StreamLaunchArguments.For(channel));
    }

    /// <summary>cmd also expands <c>%NAME:~0,5%</c> and <c>%NAME:a=b%</c>, and <c>%CD%</c> is a hex-pair name.</summary>
    [Theory]
    [InlineData("https://example.test/%PATH:~0,5%")]
    [InlineData("https://example.test/%PATH:a=b%/x")]
    [InlineData("https://example.test/%CD%AB")]
    public void For_LeavesOutAnAddressWithACmdSubstringOrDirectoryVariable(string url)
    {
        var channel = Channel(url);

        Assert.Equal($"--id \"{channel.Id:D}\"", StreamLaunchArguments.For(channel));
    }

    /// <summary>
    /// Audit A2: a relay or tunnel address is the right to listen; it never goes into a shortcut file or a
    /// copied command, and the channel still launches by id.
    /// </summary>
    [Theory]
    [InlineData("https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream")]
    [InlineData("fmsx://exchange.example.net:44022/b/ICEiIyQlJicoKSorLC0uLw/http")]
    public void For_LeavesOutABroadcastCapabilityAddress(string url)
    {
        var channel = Channel(url);

        var arguments = StreamLaunchArguments.For(channel);
        Assert.Equal($"--id \"{channel.Id:D}\"", arguments);
        Assert.False(StreamLaunchArguments.CarriesAddress(channel));
        Assert.DoesNotContain("ICEiIyQlJicoKSorLC0uLw", arguments);
    }

    /// <summary>Re-audit D1: a "/b/" segment of an ordinary address is not a capability; the address still rides along.</summary>
    [Theory]
    [InlineData("http://h/radio/b/live.mp3")]
    [InlineData("https://h/b/news")]
    [InlineData("https://exchange.example.net/b/ICEiIyQlJicoKSorLC0uLw/http")]
    [InlineData("http://h/x?next=/b/news")]
    public void For_CarriesAnOrdinaryAddressWithABSegment(string url)
    {
        var channel = Channel(url);

        Assert.True(StreamLaunchArguments.CarriesAddress(channel));
        Assert.Equal($"--id \"{channel.Id:D}\" --url \"{url}\"", StreamLaunchArguments.For(channel));
    }

    /// <summary>
    /// Re-audit D4: a token between the percents is an escape only when it is exactly two hex digits. A name
    /// that merely begins with a hex pair (DATE, BASE), is followed by one after the closing percent, or
    /// begins with "=" is still a cmd variable.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/%DATE%AB")]
    [InlineData("https://example.test/%BASE%AB")]
    [InlineData("https://example.test/%C3%DATE%AB")]
    [InlineData("https://example.test/%A9%USERNAME%.mp3")]
    [InlineData("https://example.test/%=ExitCode%")]
    [InlineData("https://example.test/%=C:%x")]
    [InlineData("https://example.test/%cd%AB")]
    public void For_LeavesOutAnAddressWithAHexNamedOrEqualsNamedCmdVariable(string url)
    {
        var channel = Channel(url);

        Assert.Equal($"--id \"{channel.Id:D}\"", StreamLaunchArguments.For(channel));
    }

    [Theory]
    [InlineData("https://example.test/%C3%A9t%C3%A9")]
    [InlineData("https://example.test/caf%C3%A9/%D0%B0%D0%B1.mp3")]
    [InlineData("https://example.test/%E4%B8%AD%E6%96%87%C3%A9t%C3%A9")]
    public void For_StillCarriesPercentEncodedNonAsciiPaths(string url)
    {
        var channel = Channel(url);

        Assert.True(StreamLaunchArguments.CarriesAddress(channel));
    }

    /// <summary>Audit S10: every PowerShell single-quote character in the executable path is doubled.</summary>
    [Fact]
    public void ForPowerShell_DoublesEveryTypographicSingleQuoteInThePath()
    {
        var channel = Channel(Address);

        var line = StreamLaunchArguments.ForPowerShell("C:\\Users\\a\u2018b\u2019c\u201Ad\u201Be'f\\StreamsPlayer.exe", channel);

        Assert.StartsWith(
            "& 'C:\\Users\\a\u2018\u2018b\u2019\u2019c\u201A\u201Ad\u201B\u201Be''f\\StreamsPlayer.exe' ",
            line,
            StringComparison.Ordinal);
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
