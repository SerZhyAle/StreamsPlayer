using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0074: the reply parser that exists because .NET's HTTP stack refuses a Shoutcast v1 greeting.
/// Every case runs on a MemoryStream - the ticket requires the parsing rules to be verifiable without a
/// network, and the bounds below are exactly what an untrusted server would be attacking.
/// </summary>
public sealed class IcyResponseHeadTests
{
    private static Task<IcyResponseHead?> ReadAsync(string head) =>
        IcyResponseHead.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(head)), CancellationToken.None);

    [Fact]
    public async Task ReadsAShoutcastV1Greeting()
    {
        // The whole point of the ticket: this is the reply HttpClient throws on.
        var head = await ReadAsync("ICY 200 OK\r\nicy-name: Fake\r\nicy-metaint: 16000\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal(200, head!.StatusCode);
        Assert.Equal("16000", head["icy-metaint"]);
    }

    [Fact]
    public async Task ReadsAnOrdinaryHttpGreetingIdentically()
    {
        // Criterion 1: the stations that already worked must keep working, so both greetings have to
        // yield the same answer from the same code.
        var head = await ReadAsync("HTTP/1.1 200 OK\r\nicy-metaint: 16000\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal(200, head!.StatusCode);
        Assert.Equal("16000", head["icy-metaint"]);
    }

    [Fact]
    public async Task HeaderLookupIgnoresCase()
    {
        var head = await ReadAsync("ICY 200 OK\r\nIcy-MetaInt: 8192\r\n\r\n");

        Assert.Equal("8192", head!["icy-metaint"]);
    }

    [Fact]
    public async Task ToleratesBareLineFeeds()
    {
        // Shoutcast daemons are old and not uniformly CRLF.
        var head = await ReadAsync("ICY 200 OK\nicy-metaint: 4096\n\n");

        Assert.Equal("4096", head!["icy-metaint"]);
    }

    [Fact]
    public async Task ReportsANonSuccessStatus()
    {
        var head = await ReadAsync("HTTP/1.1 404 Not Found\r\n\r\n");

        Assert.Equal(404, head!.StatusCode);
        Assert.Null(head["icy-metaint"]);
    }

    [Fact]
    public async Task AbsentHeaderIsNull()
    {
        var head = await ReadAsync("ICY 200 OK\r\nicy-name: Fake\r\n\r\n");

        Assert.NotNull(head);
        Assert.Null(head!["icy-metaint"]);
    }

    [Fact]
    public async Task SkipsALineWithNoName()
    {
        // A junk line is not a reason to discard a reply that is otherwise usable.
        var head = await ReadAsync("ICY 200 OK\r\ngarbage-without-a-colon\r\nicy-metaint: 16\r\n\r\n");

        Assert.Equal("16", head!["icy-metaint"]);
    }

    [Fact]
    public async Task FirstValueWinsForADuplicatedHeader()
    {
        var head = await ReadAsync("ICY 200 OK\r\nicy-metaint: 16\r\nicy-metaint: 999999\r\n\r\n");

        Assert.Equal("16", head!["icy-metaint"]);
    }

    [Fact]
    public async Task RefusesAReplyThatIsNotHttpOrIcy()
    {
        Assert.Null(await ReadAsync("HELLO THERE\r\n\r\n"));
    }

    [Fact]
    public async Task RefusesAHeadThatNeverTerminates()
    {
        // The failure mode this bound exists for: a server that sends headers forever. It must end as a
        // null, not as an unbounded read.
        var endless = new StringBuilder("ICY 200 OK\r\n");
        while (endless.Length < IcyResponseHead.MaxHeadBytes * 2)
        {
            endless.Append("x-filler: 0123456789\r\n");
        }

        Assert.Null(await ReadAsync(endless.ToString()));
    }

    [Fact]
    public async Task RefusesAnOverlongHeaderLine()
    {
        var line = new string('a', IcyResponseHead.MaxLineLength + 10);

        Assert.Null(await ReadAsync($"ICY 200 OK\r\nx-huge: {line}\r\n\r\n"));
    }

    [Fact]
    public async Task RefusesAStreamThatEndsInsideTheHead()
    {
        Assert.Null(await ReadAsync("ICY 200 OK\r\nicy-metaint: 16\r\n"));
    }

    [Fact]
    public async Task LeavesTheStreamOnTheFirstBodyByte()
    {
        // The body's first byte matters - it is audio, and the frame layout is counted from it - so the
        // parser must not buffer past the blank line.
        var bytes = Encoding.ASCII.GetBytes("ICY 200 OK\r\nicy-metaint: 16\r\n\r\nBODY");
        var stream = new MemoryStream(bytes);

        var head = await IcyResponseHead.ReadAsync(stream, CancellationToken.None);
        Assert.NotNull(head);

        var rest = new byte[4];
        var read = await stream.ReadAsync(rest);
        Assert.Equal(4, read);
        Assert.Equal("BODY", Encoding.ASCII.GetString(rest));
    }
}
