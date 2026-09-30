using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FastMediaSorterBroadcastDescriptorTests
{
    private const string PhoneJson = """
        {"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Galaxy S25 FE","mode":"AUDIO_ONLY","sourceId":"3f6c1f0e-8d2b-4f6e-9a57-2b1f0c9d4e11"}
        """;

    [Fact]
    public void PlainContractDescriptorIsAccepted()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(PhoneJson);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
        Assert.NotNull(read.Broadcast);
        Assert.Equal("Galaxy S25 FE", read.Broadcast.Title);
        Assert.Equal("3f6c1f0e-8d2b-4f6e-9a57-2b1f0c9d4e11", read.Broadcast.SourceId);
        Assert.True(read.Broadcast.IsLive);
        Assert.Equal("http://192.168.1.97:8768/live-audio.aac", read.Broadcast.SelectAudioEndpoint().Url);
    }

    [Fact]
    public void BarcodeBuiltFromContractJsonIsAccepted()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(Compress(PhoneJson));

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
    }

    [Fact]
    public void CurrentAndroidIntentLinkIsAccepted()
    {
        var link = $"intent://import?payload={Uri.EscapeDataString(Compress(PhoneJson))}#Intent;scheme=fmsbcast;package=com.sza.fastmediasorter;end";

        var read = FastMediaSorterBroadcastDescriptor.Read(link);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
    }

    [Fact]
    public void UnknownMemberIsIgnored()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(
            """{"schemaVersion":1,"url":"http://192.168.1.166:33559/listen","mode":"AUDIO_ONLY","futureField":true}""");

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
    }

    [Fact]
    public void V2FieldsStayWithinSchemaOneAndSelectOnlyCompatibleAudioEndpoint()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(
            """
            {"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Galaxy S25 FE","mode":"AUDIO_ONLY","sourceId":"source","isLive":true,"targetLatencyMs":1000,"endpoints":[{"url":"rtsp://192.168.1.97:8554/live","transport":"RTSP","mode":"VIDEO_AUDIO","videoCodec":"H264","audioCodec":"AAC","isLive":true,"targetLatencyMs":1000},{"url":"http://192.168.1.97:8768/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"bitrate":128000,"isLive":true,"targetLatencyMs":1000}]}
            """);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
        Assert.NotNull(read.Broadcast);
        Assert.Equal(2, read.Broadcast.Endpoints.Count);
        Assert.Equal(1000, read.Broadcast.TargetLatencyMs);
        Assert.Equal("http://192.168.1.97:8768/live-audio.aac", read.Broadcast.SelectAudioEndpoint().Url);
    }

    [Fact]
    public void HigherSchemaAsksForAnUpdate()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(
            """{"schemaVersion":2,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY"}""");

        Assert.Equal(FastMediaSorterBroadcastReadStatus.UnsupportedSchema, read.Status);
    }

    [Theory]
    [InlineData("VIDEO_AUDIO")]
    [InlineData("VIDEO_ONLY")]
    [InlineData("FUTURE_MODE")]
    public void UnsupportedModesAreNeverAcceptedAsAudio(string mode)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(
            $$"""{"schemaVersion":1,"url":"rtsp://192.168.1.97:8554/live","mode":"{{mode}}"}""");

        Assert.Equal(FastMediaSorterBroadcastReadStatus.UnsupportedMode, read.Status);
    }

    [Theory]
    [InlineData("FMSBCAST1:not-base64")]
    [InlineData("FMSBCAST1:bm90IGd6aXA=")]
    [InlineData("{not json}")]
    [InlineData("{\"schemaVersion\":1,\"url\":\"\",\"mode\":\"AUDIO_ONLY\"}")]
    [InlineData("{\"schemaVersion\":1,\"url\":\"http://example.test/live\",\"mode\":\"\"}")]
    public void MalformedPayloadsAreRefused(string payload)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(payload);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
    }

    [Fact]
    public void InvalidUtf8IsRefused()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(new byte[] { 0xc3, 0x28 });

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidEncoding, read.Status);
    }

    [Fact]
    public void InputAndInflatedPayloadsAreLimitedBeforeParsing()
    {
        var oversizedText = new string('x', FastMediaSorterBroadcastDescriptor.MaximumPayloadBytes + 1);
        var oversizedJson = "{\"schemaVersion\":1,\"url\":\"http://example.test/live\",\"mode\":\"AUDIO_ONLY\",\"ignored\":\"" +
                            new string('x', FastMediaSorterBroadcastDescriptor.MaximumPayloadBytes) + "\"}";

        Assert.Equal(FastMediaSorterBroadcastReadStatus.TooLarge, FastMediaSorterBroadcastDescriptor.Read(oversizedText).Status);
        Assert.Equal(FastMediaSorterBroadcastReadStatus.TooLarge, FastMediaSorterBroadcastDescriptor.Read(Compress(oversizedJson)).Status);
    }

    [Fact]
    public void NonBroadcastTextIsLeftForOtherPasteFlows()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read("SPCH1 https://example.test/live");

        Assert.Equal(FastMediaSorterBroadcastReadStatus.NotBroadcast, read.Status);
    }

    // SP-0158: nesting helpers. A link level costs "fmsbcast://?payload=" (20 characters), so a link
    // chain that fits the 64 KiB entry cap tops out near 3,276 levels - the old recursive reader
    // overflowed the stack inside that. The compressed form needs the test-side gzip writer.

    private static string WrapInLink(string inner) => "fmsbcast://?payload=" + Uri.EscapeDataString(inner);

    private static string WrapInLinkUnescaped(string inner) => "fmsbcast://?payload=" + inner;

    private static string NestLinks(int levels, string innermost)
    {
        var text = innermost;
        for (var level = 0; level < levels; level++)
        {
            // The payload is plain letters and link punctuation, so the escape is the identity and the
            // chain keeps its true per-level cost of 20 characters.
            text = WrapInLinkUnescaped(text);
        }

        return text;
    }

    private static string NestCompressed(int layers, string innermost)
    {
        var text = innermost;
        for (var layer = 0; layer < layers; layer++)
        {
            text = Compress(text);
        }

        return text;
    }

    /// <summary>
    /// SP-0158: a link chain deeper than the wrappings limit is the invalid payload it always was -
    /// refused after the limit, not followed one stack frame per level.
    /// </summary>
    [Fact]
    public void LinkNestedBeyondTheWrappingsLimitIsInvalid()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(NestLinks(FastMediaSorterBroadcastDescriptor.MaximumWrappings + 1, PhoneJson));

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
    }

    [Fact]
    public void CompressedNestedBeyondTheWrappingsLimitIsInvalid()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(
            NestCompressed(FastMediaSorterBroadcastDescriptor.MaximumWrappings + 1, PhoneJson));

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
    }

    [Fact]
    public void MixedLinkAndCompressionNestingBeyondTheLimitIsInvalid()
    {
        var text = PhoneJson;
        for (var level = 0; level <= FastMediaSorterBroadcastDescriptor.MaximumWrappings; level++)
        {
            text = level % 2 == 0 ? WrapInLink(text) : Compress(text);
        }

        var read = FastMediaSorterBroadcastDescriptor.Read(text);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
    }

    /// <summary>
    /// SP-0158: the size ceiling is the whole read's. Two layers that each inflate to ~34 KiB - inside
    /// the old per-layer cap - used to reset the budget at every level; their inflated total (~68 KiB)
    /// now refuses the input. High-entropy text keeps each outer layer near its inner size, so both
    /// layers stay small enough to be individually legal.
    /// </summary>
    [Fact]
    public void SmallCompressedLayersWhoseInflatedTotalExceedsTheCeilingAreTooLarge()
    {
        var inner = HighEntropyText(34_000);
        var chain = Compress(Compress(inner));

        var read = FastMediaSorterBroadcastDescriptor.Read(chain);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.TooLarge, read.Status);
    }

    /// <summary>A deterministic byte stream that gzip cannot shrink below its 6-bits-per-character entropy.</summary>
    private static string HighEntropyText(int length)
    {
        var bytes = new byte[length];
        uint state = 0x12345678;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            bytes[i] = (byte)(state >> 24);
        }

        return Convert.ToBase64String(bytes)[..length];
    }

    /// <summary>
    /// SP-0158 acceptance: a 100,000-level nesting completes with a bounded refusal. No 100,000-level
    /// input can exist under the 64 KiB entry cap (every link level costs at least 20 characters), so
    /// this one is refused by that entry check - the point is that it is refused, instantly, where the
    /// old reader would have started unwrapping it without end.
    /// </summary>
    [Fact]
    public void AHundredThousandLevelNestingCompletesWithABoundedRefusal()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(NestLinks(100_000, PhoneJson));

        Assert.Equal(FastMediaSorterBroadcastReadStatus.TooLarge, read.Status);
    }

    [Fact]
    public void InLimitDoubleWrappingIsStillAccepted()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(WrapInLink(Compress(PhoneJson)));

        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
        Assert.Equal("Galaxy S25 FE", read.Broadcast!.Title);
    }

    public static TheoryData<string, string> WronglyTypedNumericFields()
    {
        const string root = """"{"schemaVersion":1,"url":"http://192.168.1.97:8768/a.aac","mode":"AUDIO_ONLY"""";
        var data = new TheoryData<string, string>();
        foreach (var value in new[] { "\"2.2\"", "null", "true", "false", "1.5", "{}", "[]" })
        {
            data.Add("schemaVersion", $$"""{"schemaVersion":{{value}},"url":"http://h/a","mode":"AUDIO_ONLY"}""");
            data.Add("targetLatencyMs", $$"""{{root}},"targetLatencyMs":{{value}}}""");
            foreach (var field in new[] { "sampleRate", "bitrate", "targetLatencyMs" })
            {
                data.Add($"endpoints[].{field}", $$"""{{root}},"endpoints":[{"url":"http://h/b","{{field}}":{{value}}}]}""");
            }
        }

        return data;
    }

    /// <summary>
    /// SP-0119: a string, null or boolean in a numeric field used to throw out of the reader and end the
    /// process from the paste handler. It is a broken payload like any other.
    /// </summary>
    [Theory]
    [MemberData(nameof(WronglyTypedNumericFields))]
    public void WronglyTypedNumericFieldIsAnInvalidPayload(string field, string json)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(json);

        Assert.True(read.Status == FastMediaSorterBroadcastReadStatus.InvalidPayload, $"{field}: {read.Status} for {json}");
        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, FastMediaSorterBroadcastDescriptor.Read(Compress(json)).Status);
    }

    [Fact]
    public void ClipboardJsonWithAStringSchemaVersionIsNotABroadcast() =>
        Assert.Equal(
            FastMediaSorterBroadcastReadStatus.InvalidPayload,
            FastMediaSorterBroadcastDescriptor.Read("""{"schemaVersion":"2.2"}""").Status);

    // A test used to read a copy of the LIVE-BROADCAST document out of this repository and assert it
    // carried the wire shapes above. The copy is gone: the contract has one home, the shared store that
    // CLAUDE.md names and docs/contracts/LIVE-BROADCAST.md points at, and a repository that keeps its own
    // copy is how two readings of one contract start to disagree. Everything the copy was asked to prove
    // - the FMSBCAST1 prefix, the schemaVersion refusal, one connection per listener - is asserted here
    // against the code that implements it instead of against a document nobody executes.

    private static string Compress(string json)
    {
        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }

        return FastMediaSorterBroadcastDescriptor.CompressedPrefix + Convert.ToBase64String(target.ToArray());
    }
}
