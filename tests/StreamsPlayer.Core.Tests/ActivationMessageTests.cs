using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0118: the forwarded-request wire format (APP-ACTIVATION rule 4).
public sealed class ActivationMessageTests
{
    public static TheoryData<string[]> ValidLaunches => new()
    {
        Array.Empty<string>(),
        new[] { "--url", "https://example.test/live.m3u8" },
        new[] { "--id", "0f8fad5b-d9cb-469f-a165-70867728950e" },
        new[] { "--url", "rtsp://camera.local:554/stream?user=a&b=\"c\"\\d" },
        new[] { "--bogus" },
    };

    [Theory]
    [MemberData(nameof(ValidLaunches))]
    public void Serialize_ThenParse_RoundTripsTheArguments(string[] arguments)
    {
        var line = ActivationMessage.Serialize(arguments);

        Assert.Equal((byte)'\n', line[^1]);
        Assert.True(ActivationMessage.TryParse(line.AsSpan(0, line.Length - 1), out var parsed));
        Assert.Equal(arguments, parsed);
    }

    // SP-0184 (S13-2): an unpaired surrogate in a file name must not make the sender throw.
    [Fact]
    public void TrySerialize_ReplacesALoneSurrogateInsteadOfThrowing()
    {
        Assert.True(ActivationMessage.TrySerialize(new[] { "--url", "x\uD800y" }, out var payload));

        Assert.True(ActivationMessage.TryParse(payload!.AsSpan(0, payload.Length - 1), out var parsed));
        Assert.Equal(new[] { "--url", "x\uFFFDy" }, parsed);
    }

    [Fact]
    public void TryParse_RefusesAnEscapedLoneSurrogateWithoutThrowing()
    {
        const string line = "{\"schemaVersion\":1,\"command\":\"open\",\"args\":[\"\ud800\"]}";

        Assert.False(ActivationMessage.TryParse(line, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void RoundTrip_ProducesTheSameLaunchRequestAsTheCommandLine()
    {
        var id = Guid.NewGuid();
        string[] arguments = ["--id", id.ToString()];

        Assert.True(ActivationMessage.TryParse(Encoding.UTF8.GetString(ActivationMessage.Serialize(arguments)), out var parsed));
        Assert.Equal(StreamLaunchRequest.Parse(arguments), StreamLaunchRequest.Parse(parsed));
    }

    [Fact]
    public void Serialize_WritesTheContractFields()
    {
        var text = Encoding.UTF8.GetString(ActivationMessage.Serialize(["--url", "https://example.test/a"]));

        Assert.Equal(
            "{\"schemaVersion\":1,\"command\":\"open\",\"args\":[\"--url\",\"https://example.test/a\"],\"origin\":\"second-launch\"}\n",
            text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"open\"")]
    [InlineData("{}")]
    [InlineData("{\"command\":\"open\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"command\":\"open\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":\"1\",\"command\":\"open\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":1.5,\"command\":\"open\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"args\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"exit\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"OPEN\",\"args\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"open\",\"args\":\"--url\"}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"open\",\"args\":[1,2]}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"open\",\"args\":[null]}")]
    [InlineData("/launcher-run:0f8fad5b-d9cb-469f-a165-70867728950e")]
    public void TryParse_RejectsMalformedPayloads(string line)
    {
        Assert.False(ActivationMessage.TryParse(line, out var arguments));
        Assert.Null(arguments);
    }

    [Fact]
    public void TryParse_RejectsEmptyBytes()
    {
        Assert.False(ActivationMessage.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void TryParse_RejectsInvalidUtf8WithoutThrowing()
    {
        Assert.False(ActivationMessage.TryParse(new byte[] { 0xFF, 0xFE, 0x7B }, out _));
    }

    [Fact]
    public void TryParse_RejectsOversizedPayloads()
    {
        var url = "https://example.test/" + new string('a', ActivationMessage.MaximumPayloadBytes);
        var line = ActivationMessage.Serialize(["--url", url]);

        Assert.False(ActivationMessage.TryParse(line, out _));
        Assert.False(ActivationMessage.TryParse(Encoding.UTF8.GetString(line), out _));
    }

    [Fact]
    public void TryParse_RejectsTooManyArguments()
    {
        var line = ActivationMessage.Serialize(Enumerable.Repeat("x", ActivationMessage.MaximumArgumentCount + 1).ToArray());

        Assert.False(ActivationMessage.TryParse(line, out _));
    }

    // SP-0170: the sender checks the receiver's limits itself, so a launch the receiver would drop is never sent.
    [Fact]
    public void TrySerialize_RefusesALaunchWithMoreArgumentsThanTheReceiverAccepts()
    {
        var arguments = Enumerable.Repeat("x", 40).ToArray();

        Assert.False(ActivationMessage.TrySerialize(arguments, out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void TrySerialize_CountsNonAsciiTextAsTheReceiverSeesIt()
    {
        // 12 000 Cyrillic letters are 24 000 bytes as UTF-8 but 72 000 once escaped for the wire.
        var arguments = new[] { "--url", "https://example.test/" + new string('ж', 12_000) };

        Assert.False(ActivationMessage.TrySerialize(arguments, out _));
    }

    [Theory]
    [MemberData(nameof(ValidLaunches))]
    public void TrySerialize_AcceptsWhatSerializeProducesForARealLaunch(string[] arguments)
    {
        Assert.True(ActivationMessage.TrySerialize(arguments, out var payload));
        Assert.Equal(ActivationMessage.Serialize(arguments), payload);
        Assert.True(ActivationMessage.TryParse(payload.AsSpan(0, payload.Length - 1), out _));
    }

    [Fact]
    public void TryParse_SkipsUnknownFieldsAndAcceptsAMissingArgsArray()
    {
        Assert.True(ActivationMessage.TryParse(
            "{\"schemaVersion\":1,\"command\":\"open\",\"workingDir\":\"C:\\\\Media\",\"extra\":{\"a\":1}}\r\n",
            out var arguments));
        Assert.Empty(arguments);
    }
}
