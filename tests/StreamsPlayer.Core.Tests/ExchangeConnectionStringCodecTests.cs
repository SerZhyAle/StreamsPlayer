using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeConnectionStringCodecTests
{
    [Theory]
    [InlineData("FMSEXCH1:x=192.0.2.1:1", "192.0.2.1", 1)]
    [InlineData(" \r\nFMSEXCH1:x=HOST.test%3a65535\r\n", "host.test", 65535)]
    [InlineData("FMSEXCH1:z=%GG&x=%5B2001:0DB8::7%5D:80", "2001:db8::7", 80)]
    public void AddressForms_AreNormalized(string text, string host, int port)
    {
        var result = ExchangeConnectionStringCodec.Parse(text);
        Assert.True(result.Success, result.Reason);
        Assert.Null(result.Reason);
        Assert.Equal(host, result.Data!.Host);
        Assert.Equal(port, result.Data.Port);
        Assert.Null(result.Data.Fingerprint);
        Assert.Null(result.Data.Name);
        Assert.Null(result.Data.Login);
    }

    [Fact]
    public void OptionalParts_AreDecodedByNameAndWrittenInCanonicalOrder()
    {
        var fingerprint = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:u=ALIce&n=%D0%94%D0%BE%D0%BC+~*&fp="
            + Uri.EscapeDataString(fingerprint) + "&x=HOST.test:80");
        Assert.True(result.Success, result.Reason);
        Assert.Equal("Дом ~*", result.Data!.Name);
        Assert.Equal("alice", result.Data.Login);
        Assert.Equal(fingerprint, result.Data.Fingerprint);
        Assert.Equal("FMSEXCH1:x=host.test%3A80&fp=SHA256%3A" + new string('A', 43)
            + "&n=%D0%94%D0%BE%D0%BC+%7E*&u=alice", ExchangeConnectionStringCodec.Encode(result.Data));
    }

    [Theory]
    [InlineData("FMSEXCH1:n=hello", "missing-x")]
    [InlineData("FMSEXCH1:x=private-host.test:0", "bad-x")]
    [InlineData("FMSEXCH1:x=private-host.test:65536", "bad-x")]
    [InlineData("FMSEXCH1:x=private-host.test/path:80", "bad-x")]
    [InlineData("FMSEXCH1:x=%5B%5B::1%5D%5D:80", "bad-x")]
    [InlineData("FMSEXCH1:x=private-host.test:80&x=other.test:80", "repeated-key")]
    [InlineData("FMSEXCH1:x=private-host.test:80&fp=SHA256:short", "bad-fp")]
    [InlineData("FMSEXCH2:x=private-host.test:80", "newer-major")]
    [InlineData("FMSEXCH1:x=private-host.test:80 &n=a", "whitespace")]
    [InlineData("FMSEXCH1:x=private-host.test:80&n=a%00b", "bad-n")]
    [InlineData("FMSEXCH1:x=private-host.test:80&u=ab", "bad-u")]
    [InlineData("FMSEXCH1:x=private-host.test:80&n=%C0%AF", "bad-utf8")]
    [InlineData("FMSEXCH1:x=private-host.test:80&n=%Q0", "bad-escape")]
    public void Refusal_IsAtomicAndNeverEchoesInput(string text, string reason)
    {
        var result = ExchangeConnectionStringCodec.Parse(text);
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(reason, result.Reason);
        Assert.DoesNotContain("private-host", result.Reason);
        Assert.DoesNotContain("private-host", result.ToString()!);
    }

    [Fact]
    public void Limits_AreCheckedAfterTrimmingAndOnDecodedScalars()
    {
        var prefix = "FMSEXCH1:x=a.test:80&z=";
        var text = prefix + new string('a', 512 - prefix.Length);
        Assert.True(ExchangeConnectionStringCodec.Parse(" \n" + text + "\r\n").Success);
        Assert.Equal("too-long", ExchangeConnectionStringCodec.Parse(text + "a").Reason);
        Assert.True(ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test:80&n=" + string.Concat(Enumerable.Repeat("%F0%9F%98%80", 40))).Success);
        Assert.Equal("bad-n", ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test:80&n=" + new string('a', 65)).Reason);
        Assert.Equal("bad-n", ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test:80&n=%C2%85").Reason);
    }

    [Fact]
    public void Writer_RejectsInvalidDataWithoutEchoOrFallbackReplacement()
    {
        var exception = Assert.Throws<ArgumentException>(() => ExchangeConnectionStringCodec.Encode(new("private-host.test/path", 80)));
        Assert.DoesNotContain("private-host", exception.ToString());
        Assert.Throws<ArgumentException>(() => ExchangeConnectionStringCodec.Encode(new("a.test", 80, name: "\uD800")));
    }

    [Fact]
    public async Task Reader_StopsAt4097BytesOnNonSeekableStream()
    {
        using var stream = new CountingStream(100000);
        var result = await ExchangeConnectionStringCodec.ReadAsync(stream);
        Assert.Equal("file-too-long", result.Reason);
        Assert.Null(result.Data);
        Assert.Equal(4097, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Reader_PropagatesCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("FMSEXCH1:x=a.test:80"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExchangeConnectionStringCodec.ReadAsync(stream, cancellation.Token));
    }

    [Fact]
    public async Task Reader_ToleratesOnlyOneLeadingBom()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFF\uFEFFFMSEXCH1:x=a.test:80"));
        Assert.Equal("not-a-string", (await ExchangeConnectionStringCodec.ReadAsync(stream)).Reason);
    }

    [Fact]
    public async Task Reader_DoesNotExposeIoExceptionPaths()
    {
        using var stream = new FailedStream();
        var result = await ExchangeConnectionStringCodec.ReadAsync(stream);
        Assert.Null(result.Data);
        Assert.Equal("not-a-string", result.Reason);
    }

    private sealed class FailedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private-host.test C:\\Private\\Connection.fmsexch"));
    }

    private sealed class CountingStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(7, Math.Min(buffer.Length, length - BytesRead));
            buffer.Span[..count].Fill((byte)'a');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
