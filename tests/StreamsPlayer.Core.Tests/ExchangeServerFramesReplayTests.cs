using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0228 definition of done item 3, the frames corpus of the FMS_Win server: DEVICE-EXCHANGE items BS (the corpus is
/// adopted), T (the frame limit follows the stream's state), AM (the bounds of an `enroll`), AO (an absent account kind is
/// a member) and sections 5 to 7. It replays `vectors/server-frames-v2.json`, vendored byte for byte under
/// `Vectors/server/` (sha256 afa1a68273670f98c5dd615261759d1853a4cafd9086254e23acbdc7d0aea107, contract 0.17, rung 2),
/// through the product's own wire code. The corpus header sets the rule: a client encodes every client->server step to
/// exactly its `hex` and decodes every server->client step to the same values; ids, tokens and codes are the scenario's.
/// What this product can produce or read is replayed here; what it cannot is listed in
/// <see cref="EveryClientFrameTypeIsAccountedFor"/>, with the reason. Evidence only: no joint run is claimed.
/// </summary>
public sealed class ExchangeServerFramesReplayTests
{
    private const string FramesSha256 = "afa1a68273670f98c5dd615261759d1853a4cafd9086254e23acbdc7d0aea107";

    private static readonly FramesCorpus Corpus = ExchangeFramesCorpus.Load(ExchangeFramesCorpus.ServerFrames);

    // ---------------------------------------------------------------- the file

    [Fact]
    public void VendoredFile_IsTheCatalogsBytes_AndItsProvenanceNamesTheSameHash()
    {
        Assert.Equal(FramesSha256, ExchangeFramesCorpus.Sha256Of(ExchangeFramesCorpus.ServerFrames));
        Assert.Equal(FramesSha256, Corpus.Sha256);
        Assert.Contains(FramesSha256, File.ReadAllText(ExchangeFramesCorpus.PathOf("server/PROVENANCE.txt")));
    }

    [Fact]
    public void Corpus_ShapeIsPinned_SoARegenerationIsNoticed()
    {
        Assert.Equal("0.17", Corpus.ContractVersion);
        Assert.Equal(2, Corpus.Rung);
        Assert.Equal(["enroll-and-hello", "pairing", "refusals", "directory", "tunnels", "relay", "cast"],
            Corpus.Transcripts.Select(transcript => transcript.Name));
        Assert.Equal(173, Corpus.Steps.Count());
        Assert.Equal(153, Corpus.Frames.Count());
        Assert.Equal(79, Corpus.Frames.Count(step => step.ClientToServer));
        Assert.Equal(74, Corpus.Frames.Count(step => step.ServerToClient));
        Assert.Equal(10, Corpus.Steps.Count(step => step.Closed));
        Assert.Equal(6, Corpus.Steps.Count(step => step.Chunk is not null));
        Assert.Equal(4, Corpus.Steps.Count(step => step.IsHttp));
        Assert.Equal(10, Corpus.Chunks.Count);
    }

    [Fact]
    public void ProductLimits_AreTheConstantsOfTheFile()
    {
        var constants = Corpus.Constants;
        // Expected is the product's constant here, actual the file's: either way a difference names both numbers.
        Assert.Equal(ExchangeProtocol.OpeningLimit, constants.MaxFrame);
        Assert.Equal(ExchangeProtocol.AuthenticatedLimit, constants.MaxFrameAuth);
        Assert.Equal(ExchangeUploadChunks.MaximumLengthField, constants.MaxChunkLength);
        Assert.Equal(ExchangeDirectoryState.MaximumRecordBytes, constants.MaxRecord);

        using var accepted = JsonDocument.Parse($$"""{"schemaVersion":{{constants.SchemaVersion}},"type":"welcome"}""");
        ExchangeHandshake.ValidateSchema(accepted.RootElement);
        foreach (var other in new[] { constants.SchemaVersion - 1, constants.SchemaVersion + 1 })
        {
            using var refused = JsonDocument.Parse($$"""{"schemaVersion":{{other}},"type":"welcome"}""");
            Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ValidateSchema(refused.RootElement));
        }
    }

    // ---------------------------------------------------------------- framing (item T)

    [Fact]
    public async Task EveryFrame_HasItsLengthPrefix_FitsItsStreamsLimit_AndIsDecodedByTheProductsReader()
    {
        Assert.Empty(await ExchangeFramesReplay.FramingAndDecodeFailuresAsync(Corpus));
    }

    [Fact]
    public async Task FrameLimit_IsTheOpeningOneUntilWelcomeOrEnrolled_AndTheAuthenticatedOneAfter()
    {
        var constants = Corpus.Constants;
        async Task<bool> Reads(int length, bool authenticated)
        {
            var body = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"type\":\"x\",\"pad\":\"" + new string('a', length - 39) + "\"}");
            Assert.Equal(length, body.Length);
            var frame = new byte[4 + length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)length);
            body.CopyTo(frame, 4);
            try
            {
                using var _ = await ExchangeProtocol.ReadAsync(new MemoryStream(frame), authenticated, CancellationToken.None);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        Assert.True(await Reads(constants.MaxFrame, authenticated: false));
        Assert.False(await Reads(constants.MaxFrame + 1, authenticated: false));
        Assert.True(await Reads(constants.MaxFrame + 1, authenticated: true));
        Assert.True(await Reads(constants.MaxFrameAuth, authenticated: true));
        Assert.False(await Reads(constants.MaxFrameAuth + 1, authenticated: true));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ExchangeProtocol.ReadAsync(new MemoryStream(new byte[4]), authenticated: true, CancellationToken.None));

        // The writer holds the same bounds, so the product cannot send what a conforming server would close the stream over.
        var oversize = new { schemaVersion = 2, type = "x", pad = new string('a', constants.MaxFrame) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExchangeProtocol.WriteAsync(new MemoryStream(), oversize, authenticated: false, CancellationToken.None));
        await ExchangeProtocol.WriteAsync(new MemoryStream(), oversize, authenticated: true, CancellationToken.None);
    }

    // ---------------------------------------------------------------- what the server sent

    [Fact]
    public async Task TheFirstFrameOfEveryOpenedStream_PassesTheProductsHandshake()
    {
        var (failures, streams) = await ExchangeFramesReplay.OpeningReplayAsync(Corpus);
        Assert.Empty(failures);
        // 34 streams, of which 26 open with this product's own first frame (enroll or hello) and an answer: the two plain
        // streams are the server's alone, the two `connect` streams of the tunnels open on a connection made earlier, and the
        // four HTTP listeners are not envelopes.
        Assert.Equal(26, streams);
        Assert.Equal(34, Corpus.Transcripts.Sum(transcript => transcript.StreamTransports.Count));
    }

    [Fact]
    public void PlainStreams_AreTheServersAlone_BecauseTheProductOpensNoneAndSendsNoSchemaVersionOne()
    {
        // Server-only: `tls-required` answers an enroll on a plain stream, `version-unsupported` a hello of schemaVersion 1
        // (the answer itself carries 2). This product connects through ExchangeTlsConnection only and writes schemaVersion 2
        // (ExchangeEnrollmentFields, the hello of the control loop), so neither request can be produced; both answers are
        // still read (Refusals_...).
        var plain = Corpus.Transcripts.SelectMany(transcript => transcript.StreamTransports
            .Where(stream => stream.Value == "plain").Select(stream => transcript.Name + "/" + stream.Key)).Order(StringComparer.Ordinal);
        Assert.Equal(["refusals/legacy", "refusals/plain-enroll"], plain);
        var legacy = Corpus.Named("refusals").Steps.First(step => step is { Stream: "legacy", ClientToServer: true });
        using var hello = legacy.Parse();
        Assert.Equal(1, hello.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ValidateSchema(hello.RootElement));
    }

    [Fact]
    public async Task AKnownFrameWhereTheHandshakeExpectsAnother_ClosesTheStream()
    {
        var keepalive = Corpus.Frames.First(step => step.ServerToClient && step.Type == "keepalive");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ExchangeHandshake.ReadAsync(new MemoryStream(keepalive.Frame), "welcome", CancellationToken.None));
    }

    [Fact]
    public void EveryServerFrame_IsReadAsTheFileSaysItIs()
    {
        Assert.Empty(ExchangeFramesReplay.ReadingFailures(Corpus));
        var types = Corpus.Frames.Where(step => step.ServerToClient).GroupBy(step => step.Type!)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["broadcast-started"] = 3, ["cast-offer"] = 4, ["cast-result"] = 5, ["cast-stop"] = 2, ["changed"] = 4,
            ["connected"] = 3, ["directory"] = 2, ["enrolled"] = 13, ["keepalive"] = 10, ["open"] = 3, ["pairing-code"] = 1,
            ["published"] = 4, ["refused"] = 11, ["upload-accepted"] = 1, ["welcome"] = 8
        }, types);
    }

    [Fact]
    public void EnrolledAndWelcome_CarryNoAccountKind_SoTheyReadAsAMemberAccount()
    {
        // The 0.17 server predates item AO: its frames are the real bytes for "an absent member is a member account".
        var frames = Corpus.Frames.Where(step => step.ServerToClient && step.Type is "enrolled" or "welcome").ToArray();
        Assert.Equal(21, frames.Length);
        foreach (var step in frames)
        {
            using var document = step.Parse();
            Assert.False(document.RootElement.TryGetProperty("accountKind", out _), step.Label);
            Assert.Equal(ExchangeAccountKind.Member,
                ExchangeAccountKinds.Parse(ExchangeProtocol.String(document.RootElement, "accountKind")));
        }
    }

    [Fact]
    public void Refusals_AreReasonOnly_AndEveryReasonOfTheFileHasARow()
    {
        var refusals = Corpus.Frames.Where(step => step.ServerToClient && step.Type == "refused").ToArray();
        var reasons = refusals.Select(step =>
        {
            using var document = step.Parse();
            return ExchangeProtocol.String(document.RootElement, "reason")!;
        }).Distinct().Order(StringComparer.Ordinal).ToArray();
        // The note of the file lists these nine as "produced in the refusals transcript".
        Assert.Equal(["bad-credentials", "capacity", "device-revoked", "id-collision", "port-unavailable", "rate-limited",
            "tls-required", "unavailable", "version-unsupported"], reasons);
        Assert.All(reasons, reason => Assert.True(ExchangeFramesReplay.RefusalRows.ContainsKey(reason), reason));
        var failures = refusals.Select(step => ExchangeFramesReplay.RefusalFailure(step, ExchangeFramesReplay.RefusalRows))
            .OfType<string>().ToArray();
        Assert.Empty(failures);
    }

    [Fact]
    public void ARefusalOfAOneTimeCode_ShowsTheCodesOwnRow_AndAnUnknownReasonTheGenericOne()
    {
        // pairing / again: the same code a second time (step 10) answers bad-credentials to an enroll that sent `pairingCode`.
        var again = Corpus.Named("pairing").Steps.Single(step => step is { Stream: "again", ServerToClient: true, IsFrame: true });
        using var document = again.Parse();
        var reason = ExchangeProtocol.String(document.RootElement, "reason");
        Assert.Equal("ExchangeBadCredentialsCode", ExchangeProtocol.RefusalKey(reason, oneTimeCode: true));
        Assert.Equal("ExchangeBadCredentials", ExchangeProtocol.RefusalKey(reason));
        Assert.Equal("ExchangeRefused", ExchangeProtocol.RefusalKey("a-reason-no-server-has-sent-yet"));
        Assert.Equal("ExchangeRefused", ExchangeProtocol.RefusalKey(null));
    }

    [Fact]
    public void Directory_FullListsReplaceTheState_APushOneAheadMerges_AndAGapAsksForANewList()
    {
        var (failures, tally) = ExchangeFramesReplay.DirectoryReplay(Corpus);
        Assert.Empty(failures);
        // `directory`: b-control (rev 5) and a-resync (rev 10); `changed`: a-control rev 4, 5, 6 on a state that never listed
        // (each asks for a new list and changes nothing) and a-resync rev 11 after the list of rev 10 (merged).
        Assert.Equal(new DirectoryTally(Fulls: 2, ChangesApplied: 1, Relists: 3, BroadcastRecords: 0), tally);
    }

    // ---------------------------------------------------------------- what the product sends

    [Fact]
    public async Task CastAnswers_AreByteForByteWhatTheProductAnswersTheOffer()
    {
        Assert.Empty(await ExchangeFramesReplay.CastAnswerFailuresAsync(Corpus));
        Assert.Equal(3, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "cast-answer"));
    }

    [Fact]
    public async Task UploadChunks_AreByteForByteWhatTheProductWrites_AndTheCleanEndIsAZeroLength()
    {
        Assert.Empty(await ExchangeFramesReplay.UploadChunkFailuresAsync(Corpus));
        var chunks = Corpus.Steps.Where(step => step.Chunk is not null).Select(step => step.Chunk!).ToArray();
        // header, joinable, ordinary, joinable, ordinary, then the zero length of the clean end.
        Assert.Equal([ExchangeUploadChunks.Header, ExchangeUploadChunks.Joinable, 0, ExchangeUploadChunks.Joinable, 0, 0],
            chunks.Select(chunk => (int)chunk.Flags));
        Assert.Equal(4, chunks[^1].Frame.Length);
    }

    [Fact]
    public async Task TheChunksList_ReplaysWhatAClientCanProduce_AndRecordsTheRestAsServerOnly()
    {
        var produced = new List<string>();
        var serverOnly = new List<string>();
        foreach (var item in Corpus.Chunks)
        {
            if (item.Verdict == "closes-the-stream")
            {
                // Server-only. DEVICE-EXCHANGE 7.6: "other bits zero" and "A chunk is at most 1 MiB, counted on the length
                // field"; item U: "A broadcaster never writes a larger value". The server closes the stream over an undefined
                // flag bit or a length above 1048576; a broadcaster is the party that must not write either, and the
                // product-side half of that duty is asserted in ChunkBounds_... below.
                serverOnly.Add(item.Name);
                continue;
            }

            var end = item.Verdict == "ends-the-broadcast";
            Assert.Null(ExchangeFramesReplay.ByteExactFailure(
                new Step("chunks", produced.Count + 1, item.Name, "client->server", null, null, false, null, false),
                await ExchangeFramesReplay.ChunkAsync(item.Flags, item.Payload, end), item.Frame));
            var read = await ExchangeUploadChunks.ReadAsync(new MemoryStream(item.Frame), CancellationToken.None);
            if (end)
            {
                Assert.Null(read);
            }
            else
            {
                Assert.Equal(item.Flags, read!.Value.Flags);
                Assert.Equal(item.Payload, read.Value.Payload);
            }

            produced.Add(item.Name);
        }

        Assert.Equal(["flags-ordinary", "flags-joinable", "flags-header", "flags-header-and-joinable-bits", "empty-payload", "zero-length"],
            produced);
        Assert.Equal(["undefined-flag-bit", "undefined-flag-bit-with-defined-ones", "length-1048577", "length-ffffffff"], serverOnly);
    }

    [Fact]
    public async Task ChunkBounds_TheProductNeitherWritesNorAcceptsALengthAboveOneMebibyte_AndTheFlagsItHasAreTheTwoDefined()
    {
        var limit = Corpus.Constants.MaxChunkLength;
        // The largest accepted chunk is exactly `limit` of length field (flag byte + payload) and is not listed because of its size.
        Assert.Equal(limit, (await ExchangeFramesReplay.ChunkAsync(ExchangeUploadChunks.Joinable, new byte[limit - 1], end: false)).Length - 4);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExchangeFramesReplay.ChunkAsync(0, new byte[limit], end: false));
        var closing = Corpus.Chunks.Where(chunk => chunk.Verdict == "closes-the-stream").ToArray();
        var tooLong = closing.Where(chunk => chunk.Why!.Contains("length above", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, tooLong.Length);
        foreach (var item in tooLong)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await ExchangeUploadChunks.ReadAsync(new MemoryStream(item.Frame), CancellationToken.None));
        }

        // The undefined bit of the other two server-only cases lies outside the two bits this product defines and sets.
        var undefined = closing.Where(chunk => chunk.Why!.Contains("undefined bit", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, undefined.Length);
        Assert.All(undefined, chunk =>
            Assert.NotEqual(0, chunk.Frame[4] & ~(ExchangeUploadChunks.Joinable | ExchangeUploadChunks.Header)));
    }

    [Fact]
    public async Task TunnelConnect_ByBroadcastId_IsTheFrameTheFileHas_AndTheFilesConnectedStartsTheSplice()
    {
        // The one tunnel frame the product writes from Core: a consumer's `connect` by broadcastId (ExchangeTunnelForwarder).
        // The other three `connect` frames of the file name a resourceId or a shareId, which this product never connects to.
        var tunnels = Corpus.Named("tunnels");
        var connect = tunnels.Steps.Single(step => step.ClientToServer && step.Type == "connect" && step.Json!.Contains("\"broadcastId\""));
        var connected = tunnels.Steps.Single(step => step.Stream == connect.Stream && step.ServerToClient && step.IsFrame);
        using var request = connect.Parse();
        var broadcastId = ExchangeProtocol.String(request.RootElement, "broadcastId");
        var scheme = ExchangeProtocol.String(request.RootElement, "scheme");
        var welcome = tunnels.Steps.First(step => step.ServerToClient && step.Type == "welcome");
        using var welcomeDocument = welcome.Parse();
        var endpoint = ExchangeProtocol.String(welcomeDocument.RootElement, "publicEndpoint");

        using var exchange = new ScriptedStream(connected.Frame);
        using var forwarder = ExchangeTunnelForwarder.Start($"fmsx://{endpoint}/b/{broadcastId}/{scheme}", "rtsp://192.0.2.1:8554/",
            certFingerprint: null, maxConnections: 1, streamConnector: (_, _, _, _) => Task.FromResult<Stream>(exchange));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (forwarder.ConnectedCount == 0)
        {
            await Task.Delay(10, deadline.Token);
        }

        Assert.Null(ExchangeFramesReplay.ByteExactFailure(connect, exchange.Written.ToArray()));
    }

    /// <summary>The exchange's side of a stream: what it was written is kept, what it says is the file's bytes and then the end.</summary>
    private sealed class ScriptedStream(byte[] reply) : Stream
    {
        private readonly MemoryStream _reply = new(reply);

        internal MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    [Fact]
    public async Task Enroll_CarriesEveryMemberTheCorpusEnrollsDo_AndItsReceiverBlockIsTheCorpusShape()
    {
        using var withPassword = await ExchangeFramesReplay.ProductEnrollAsync(pairingCode: false);
        using var withPairingCode = await ExchangeFramesReplay.ProductEnrollAsync(pairingCode: true);
        Assert.Empty(ExchangeFramesReplay.EnrollShapeFailures(Corpus, withPassword.RootElement, withPairingCode.RootElement));
        Assert.Equal(17, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "enroll"));
    }

    [Fact]
    public async Task Enroll_IsInsideTheBoundsOfItemAM_WhichTheRealServersCorpusDefines()
    {
        // The checker first has to agree with the real server on the corpus' own enrolls (EnrollShapeFailures asserts it for
        // each of the 17); then the product's enroll, with the longest role list it can declare, is held to it.
        foreach (var roles in new IReadOnlyList<string>[] { ["receiver"], ["receiver", "broadcaster"] })
        {
            foreach (var pairing in new[] { false, true })
            {
                using var enroll = await ExchangeFramesReplay.ProductEnrollAsync(pairing, roles);
                Assert.Empty(ExchangeEnrollmentBounds.Violations(enroll.RootElement));
                var receiver = enroll.RootElement.GetProperty("receiver");
                Assert.False(receiver.TryGetProperty("plays", out _), "modes x transports is over eight pairs, so item AM omits `plays`");
                Assert.True(receiver.GetProperty("modes").GetArrayLength() * receiver.GetProperty("transports").GetArrayLength()
                    > ExchangeCapabilities.MaximumDeclaredPairs);
            }
        }
    }

    [Fact]
    public void EveryClientFrameTypeIsAccountedFor()
    {
        // type -> how it is replayed, or why it is not. A type the file gains, or one this table names that the file lost, fails here.
        var accounted = new Dictionary<string, string>
        {
            ["cast-answer"] = "byte-exact: ExchangeCastAnswer (CastAnswers_...)",
            ["enroll"] = "member set: ExchangeEnrollmentFields + WriteEnrollmentAsync; the values are the device's (Enroll_...)",
            ["hello"] = "member names and order, ids, interval: the real ExchangeSourceService session (Exchange.Tests, ExchangeLoopRecoveryTests.ServerCorpusSession_...)",
            ["keepalive"] = "byte-exact: the real session (Exchange.Tests, ServerCorpusSession_TheControlLoop...)",
            ["bye"] = "byte-exact: the real session (Exchange.Tests, ServerCorpusSession_TheControlLoop...)",
            ["list"] = "byte-exact: the real session (Exchange.Tests, ServerCorpusSession_TheControlLoop...)",
            ["subscribe"] = "byte-exact: the real session (Exchange.Tests, ServerCorpusSession_TheControlLoop...)",
            ["connect"] = "by broadcastId: byte-exact through ExchangeTunnelForwarder (TunnelConnect_...); by resourceId or shareId: not produced",
            ["revoke"] = "byte-exact with the device's own id put in: the real leave path (Exchange.Tests, ServerCorpusSession_LeavingWritesTheFilesRevoke...)",
            ["broadcast-start"] = "seam: built inline in App/BroadcastProducerService.cs, which no test project compiles; not replayed",
            ["broadcast-update"] = "seam: built inline in App/BroadcastProducerService.cs; not replayed",
            ["broadcast-end"] = "seam: built inline in App/BroadcastProducerService.cs; not replayed",
            ["upload"] = "seam: built inline in App/BroadcastProducerService.cs (and its contentType differs by design); not replayed",
            ["cast"] = "seam: built inline in App/BroadcastProducerService.cs; not replayed",
            ["cast-stop"] = "seam: built inline in App/BroadcastProducerService.cs; not replayed",
            ["attach"] = "byte-exact: the real ExchangeTunnelBridge.RunAsync (Exchange.Tests, ServerCorpusSession_TheTunnelBridge...)",
            ["pairing-request"] = "not produced: this product mints no pairing code (it only enters one)",
            ["publish"] = "not produced: this product publishes no resource (roles receiver and broadcaster only)",
            ["unpublish"] = "not produced: this product publishes no resource"
        };
        var inFile = Corpus.Frames.Where(step => step.ClientToServer).Select(step => step.Type!).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(accounted.Keys.Order(StringComparer.Ordinal), inFile);
    }

    // ---------------------------------------------------------------- negative proofs: the checks above fail on one wrong byte

    [Fact]
    public async Task NegativeProof_OneChangedBodyByteOfAServerFrame_IsNotTheValueTheFileHas()
    {
        var step = Corpus.Frames.First(candidate => candidate.ServerToClient && candidate.Type == "refused");
        var frame = step.Frame;
        Assert.Null(await ExchangeFramesReplay.DecodeFailureAsync(step, frame, authenticated: false));
        Assert.Null(ExchangeFramesReplay.FramingFailure(step, frame, authenticated: false, Corpus.Constants));

        var changed = (byte[])frame.Clone();
        changed[^3] ^= 0x01; // the last letter of the reason: still valid JSON, no longer the file's reason
        Assert.NotNull(await ExchangeFramesReplay.DecodeFailureAsync(step, changed, authenticated: false));
        Assert.NotNull(ExchangeFramesReplay.FramingFailure(step, changed, authenticated: false, Corpus.Constants));

        var prefix = (byte[])frame.Clone();
        prefix[3] ^= 0x01; // the length prefix no longer counts the body
        Assert.NotNull(ExchangeFramesReplay.FramingFailure(step, prefix, authenticated: false, Corpus.Constants));
        Assert.NotNull(await ExchangeFramesReplay.DecodeFailureAsync(step, prefix, authenticated: false));
    }

    [Fact]
    public async Task NegativeProof_OneChangedByteOfAnAnswerTheProductWrites_IsNotTheFilesFrame()
    {
        var step = Corpus.Frames.First(candidate => candidate.ClientToServer && candidate.Type == "cast-answer");
        using var answer = step.Parse();
        var written = await ExchangeFramesReplay.WrittenAsync(
            ExchangeCastAnswer.Accept(ExchangeProtocol.String(answer.RootElement, "broadcastId")!));
        Assert.Null(ExchangeFramesReplay.ByteExactFailure(step, written));
        written[^3] ^= 0x01;
        Assert.NotNull(ExchangeFramesReplay.ByteExactFailure(step, written));
    }

    [Fact]
    public void NegativeProof_OneWrongEntryOfTheRefusalTable_FailsTheRefusalCheck()
    {
        var step = Corpus.Frames.First(candidate => candidate.ServerToClient && candidate.Type == "refused");
        Assert.Null(ExchangeFramesReplay.RefusalFailure(step, ExchangeFramesReplay.RefusalRows));
        var wrong = new Dictionary<string, string>(ExchangeFramesReplay.RefusalRows) { ["bad-credentials"] = "ExchangeRefused" };
        Assert.NotNull(ExchangeFramesReplay.RefusalFailure(step, wrong));
        var missing = ExchangeFramesReplay.RefusalRows.Where(entry => entry.Key != "bad-credentials")
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.NotNull(ExchangeFramesReplay.RefusalFailure(step, missing));
    }
}
