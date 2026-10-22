using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0228 definition of done item 3, the records corpus of the FMS_Win server: DEVICE-EXCHANGE item BS (a record is
/// bounded as delivered, and the corpus is adopted), section 6 (records), item AM (the bounds of an `enroll`) and item E
/// (the receiver pairs). It replays `vectors/server-records-v1.json`, vendored byte for byte under `Vectors/server/`
/// (sha256 2b0edd5dca31e91fc5b828fc5707d518c3b796bdf02f0bd28738e72108113e00, contract 0.17, rung 1). The reader side is
/// replayed: each accepted `listed` record, as an enrolled device reads it from `list`, is wrapped in a `directory` frame and
/// read by <see cref="ExchangeDirectoryState"/>. Devices are read; resources are not this product's to read; broadcast
/// records are stand-ins (a descriptor without an address) that the reader skips, never as an error and never at the
/// cost of the devices beside them. The eight refused and closed cases are the server's alone and are listed, with the
/// contract sentence, in <see cref="TheRefusedAndClosedCases_AreTheServersAlone_AndTheirNamesArePinned"/>. The `drive` member
/// of the accepted cases (section 8.1) belongs to a Drive path this product does not have (SP-0202).
/// </summary>
public sealed class ExchangeServerRecordsReplayTests
{
    private const string RecordsSha256 = "2b0edd5dca31e91fc5b828fc5707d518c3b796bdf02f0bd28738e72108113e00";

    private sealed record Case(string Name, string Kind, string Verdict, string? Reason, string Input, string? Listed, bool HasDrive);

    private static readonly IReadOnlyList<Case> Cases = ReadCases();

    private static IReadOnlyList<Case> ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(ExchangeFramesCorpus.ServerRecords)));
        return
        [
            .. document.RootElement.GetProperty("cases").EnumerateArray().Select(item => new Case(
                item.GetProperty("name").GetString()!, item.GetProperty("kind").GetString()!, item.GetProperty("verdict").GetString()!,
                ExchangeProtocol.String(item, "reason"), item.GetProperty("input").GetString()!, ExchangeProtocol.String(item, "listed"),
                item.TryGetProperty("drive", out _)))
        ];
    }

    private static IEnumerable<Case> Accepted(string kind) => Cases.Where(item => item.Kind == kind && item.Verdict == "accepted");

    private static Case Named(string name) => Cases.Single(item => item.Name == name);

    /// <summary>A `directory` frame of the given records, verbatim, read through the product's frame reader as an authenticated stream.</summary>
    private static async Task<JsonDocument> DirectoryOf(string[] devices, string[] resources, string[] broadcasts, long revision = 1)
    {
        var body = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":2,\"type\":\"directory\",\"revision\":{revision}," +
            $"\"devices\":[{string.Join(",", devices)}],\"resources\":[{string.Join(",", resources)}],\"broadcasts\":[{string.Join(",", broadcasts)}]}}");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return await ExchangeProtocol.ReadAsync(new MemoryStream(frame), authenticated: true, CancellationToken.None);
    }

    private static string NeighbourDevice => Named("device-minimal").Listed!;

    // ---------------------------------------------------------------- the file

    [Fact]
    public void VendoredFile_IsTheCatalogsBytes_AndItsProvenanceNamesTheSameHash()
    {
        Assert.Equal(RecordsSha256, ExchangeFramesCorpus.Sha256Of(ExchangeFramesCorpus.ServerRecords));
        Assert.Contains(RecordsSha256, File.ReadAllText(ExchangeFramesCorpus.PathOf("server/PROVENANCE.txt")));
    }

    [Fact]
    public void Corpus_ShapeIsPinned_SoARegenerationIsNoticed()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(ExchangeFramesCorpus.ServerRecords)));
        Assert.Equal("0.17", document.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("rung").GetInt32());
        Assert.Equal(24, Cases.Count);
        Assert.Equal([("device", 6), ("resource", 10), ("broadcast", 8)],
            Cases.GroupBy(item => item.Kind).Select(group => (group.Key, group.Count())));
        Assert.Equal(16, Cases.Count(item => item.Verdict == "accepted"));
        Assert.Equal(5, Cases.Count(item => item.Verdict == "refused"));
        Assert.Equal(3, Cases.Count(item => item.Verdict == "closed"));
        // Section 8.1's wrapper rides on every accepted case and on no other; this product reads none of it.
        Assert.All(Cases, item => Assert.Equal(item.Verdict == "accepted", item.HasDrive));
    }

    // ---------------------------------------------------------------- devices: read

    private static IReadOnlyList<(string Mode, string Transport)>? ExpectedPlays(JsonElement listed)
    {
        if (!listed.TryGetProperty("receiver", out var receiver) || receiver.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Item E: `plays` when present, else the product of `modes` and `transports`; a block that names neither says nothing.
        if (receiver.TryGetProperty("plays", out var plays) && plays.ValueKind == JsonValueKind.Array)
        {
            return [.. plays.EnumerateArray().Select(pair => (ExchangeProtocol.String(pair, "mode")!, ExchangeProtocol.String(pair, "transport")!))];
        }

        string[] Strings(string member) => receiver.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(item => item.GetString()!)]
            : [];
        var product = Strings("modes").SelectMany(mode => Strings("transports").Select(transport => (mode, transport))).ToArray();
        return product.Length > 0 ? product : null;
    }

    /// <summary>A device record read from <paramref name="wrapped"/> against what the case's own `listed` says.</summary>
    private static async Task<string?> DeviceReadFailure(Case item, string wrapped)
    {
        using var expected = JsonDocument.Parse(item.Listed!);
        using var frame = await DirectoryOf([wrapped], [], []);
        var state = new ExchangeDirectoryState();
        var read = state.ApplyFull(frame.RootElement).Snapshot.Devices?.SingleOrDefault();
        if (read is null)
        {
            return item.Name + ": the device was not read";
        }

        var root = expected.RootElement;
        var plays = ExpectedPlays(root);
        var readPlays = read.Plays?.Select(pair => (pair.Mode, pair.Transport)).ToArray();
        var lastSeen = DateTimeOffset.Parse(ExchangeProtocol.String(root, "lastSeen")!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return read.DeviceId == ExchangeProtocol.String(root, "deviceId")
            && read.DeviceName == ExchangeProtocol.String(root, "deviceName")
            && read.IsOnline == (ExchangeProtocol.String(root, "presence") == "online")
            && read.LastSeen == lastSeen
            && (plays is null ? readPlays is null : readPlays is not null && plays.SequenceEqual(readPlays))
            && read.E2eKey is null && !read.HasE2eCapability
            ? null
            : item.Name + ": the device was not read as its listed record says";
    }

    [Fact]
    public async Task EveryAcceptedDeviceRecord_IsReadAsItsListedRecordSays()
    {
        var devices = Accepted("device").ToArray();
        Assert.Equal(5, devices.Length);
        foreach (var item in devices)
        {
            Assert.Null(await DeviceReadFailure(item, item.Listed!));
        }
    }

    [Fact]
    public async Task TheReceiverBlock_IsReadByItemE_SoThePairsAreWhatTheFileSays()
    {
        async Task<string> PlaysOf(string name)
        {
            using var frame = await DirectoryOf([Named(name).Listed!], [], []);
            var plays = new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot.Devices!.Single().Plays;
            return plays is null ? "none" : string.Join(" ", plays.Select(pair => pair.Mode + ":" + pair.Transport));
        }

        // `plays` wins over modes x transports, and a member nested inside the block that nothing knows is ignored.
        Assert.Equal("AUDIO_ONLY:RELAY VIDEO_AUDIO:RTSP", await PlaysOf("device-full"));
        Assert.Equal("AUDIO_ONLY:RELAY", await PlaysOf("device-unknown-role"));
        // No block, an unknown product, or `"receiver":null` (absent, not malformed): a device that declared nothing.
        Assert.Equal("none", await PlaysOf("device-minimal"));
        Assert.Equal("none", await PlaysOf("device-unknown-product"));
        Assert.Equal("none", await PlaysOf("device-receiver-null"));
    }

    [Fact]
    public async Task AllTheAcceptedRecordsInOneFrame_LeaveTheFiveDevices_AndNothingElse()
    {
        using var frame = await DirectoryOf(
            [.. Accepted("device").Select(item => item.Listed!)],
            [.. Accepted("resource").Select(item => item.Listed!)],
            [.. Accepted("broadcast").Select(item => item.Listed!)], revision: 9);
        var snapshot = new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot;
        Assert.Equal(9, snapshot.Revision);
        Assert.Equal(Accepted("device").Select(ReadId).Order(StringComparer.Ordinal),
            snapshot.Devices!.Select(device => device.DeviceId).Order(StringComparer.Ordinal));
        Assert.Empty(snapshot.Groups);
        Assert.Empty(snapshot.LiveBroadcastIds);
    }

    private static string ReadId(Case item)
    {
        using var listed = JsonDocument.Parse(item.Listed!);
        return ExchangeProtocol.String(listed.RootElement, "deviceId")!;
    }

    // ---------------------------------------------------------------- resources and broadcasts: skipped

    [Fact]
    public async Task EveryAcceptedResource_IsIgnored_NeverAnError_AndTheDeviceBesideItIsIntact()
    {
        var resources = Accepted("resource").ToArray();
        Assert.Equal(6, resources.Length);
        foreach (var item in resources)
        {
            using var frame = await DirectoryOf([NeighbourDevice], [item.Listed!], []);
            var snapshot = new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot;
            Assert.Equal(1, snapshot.Revision);
            Assert.Equal(ReadId(Named("device-minimal")), snapshot.Devices!.Single().DeviceId);
            Assert.Empty(snapshot.Groups);
        }
    }

    [Fact]
    public async Task TheStaleCase_ResourceSixteenKiBExact_IsReadByItemBS_AsARecordOverTheBound()
    {
        // The file calls it accepted and its listed record is 16475 bytes; item BS refuses a record above 16384 bytes AS
        // DELIVERED with `capacity`, so a reader replaying the file treats that one case by the item: skipped by every reader.
        var item = Named("resource-16KiB-exact");
        Assert.True(Encoding.UTF8.GetByteCount(item.Listed!) > ExchangeDirectoryState.MaximumRecordBytes);
        Assert.Equal(ExchangeDirectoryState.MaximumRecordBytes, Encoding.UTF8.GetByteCount(item.Input));
        using var frame = await DirectoryOf([NeighbourDevice], [item.Listed!], []);
        Assert.True(frame.RootElement.GetRawText().Length < ExchangeProtocol.AuthenticatedLimit);
        Assert.Single(new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot.Devices!);
    }

    [Fact]
    public async Task EveryAcceptedBroadcastRecord_IsASkippedStandIn_NeverAnError_AndTheDeviceBesideItIsIntact()
    {
        // A server is content-agnostic: these descriptors are `{}` or endpoints only, with no top-level `url` and `mode` (the
        // reader's whole-record rule), so the directory drops them. Nothing here is field equality.
        var broadcasts = Accepted("broadcast").ToArray();
        Assert.Equal(5, broadcasts.Length);
        foreach (var item in broadcasts)
        {
            using var frame = await DirectoryOf([NeighbourDevice], [], [item.Listed!]);
            var snapshot = new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot;
            Assert.Empty(snapshot.LiveBroadcastIds);
            Assert.Empty(snapshot.Groups.SelectMany(group => group.Broadcasts));
            Assert.Equal(ReadId(Named("device-minimal")), snapshot.Devices!.Single().DeviceId);
        }
    }

    [Fact]
    public async Task ARecordAtTheBound_IsReadAndOneByteOverIsSkipped_ForTheDevicesTheProductReads()
    {
        // Item BS and section 6: 16384 bytes of JSON as delivered. The padding member is the corpus' own (`pad`, which its
        // 16 KiB cases use); it is added to a listed device to land on the bound exactly.
        var bare = NeighbourDevice;
        var overhead = Encoding.UTF8.GetByteCount(bare[..^1] + ",\"pad\":\"\"}");
        async Task<int> DevicesRead(int bytes)
        {
            var padded = bare[..^1] + ",\"pad\":\"" + new string('a', bytes - overhead) + "\"}";
            Assert.Equal(bytes, Encoding.UTF8.GetByteCount(padded));
            using var frame = await DirectoryOf([padded], [], []);
            return new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot.Devices!.Count;
        }

        Assert.Equal(1, await DevicesRead(ExchangeDirectoryState.MaximumRecordBytes));
        Assert.Equal(0, await DevicesRead(ExchangeDirectoryState.MaximumRecordBytes + 1));
    }

    // ---------------------------------------------------------------- the server's alone

    [Fact]
    public void TheRefusedAndClosedCases_AreTheServersAlone_AndTheirNamesArePinned()
    {
        // Server-only. refused `capacity` (item BS: "Admission bounds that result before it stores anything ... answered
        // `refused {reason:"capacity"}` and stores nothing"); refused `id-collision` (item G: a resourceId held by another
        // device, a shareId held by another resource, a broadcastId in use by another device); closed (item AM and section 6:
        // a malformed enroll or record closes the stream with no answer). This product publishes no resource, so no
        // resource request is its to send; it can send a broadcast record, whose bound is in the next test.
        var serverOnly = Cases.Where(item => item.Verdict != "accepted").Select(item => (item.Name, item.Verdict, item.Reason))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(
        [
            ("broadcast-16KiB-plus-1", "refused", "capacity"),
            ("broadcast-id-held-by-another-device", "refused", "id-collision"),
            ("broadcast-without-descriptor", "closed", null),
            ("device-receiver-too-many-modes", "closed", null),
            ("resource-16KiB-plus-1", "refused", "capacity"),
            ("resource-held-by-another-device", "refused", "id-collision"),
            ("resource-not-an-id", "closed", null),
            ("resource-share-id-held-by-another", "refused", "id-collision")
        ], serverOnly);

        // What a refusal of these shows, when one reaches this product: the rows of the table.
        Assert.Equal("ExchangeCapacity", ExchangeProtocol.RefusalKey("capacity"));
        Assert.Equal("ExchangeIdCollision", ExchangeProtocol.RefusalKey("id-collision"));
    }

    [Fact]
    public void TheOversizeCases_AreOneByteOverTheBoundTheReaderEnforces()
    {
        foreach (var name in new[] { "resource-16KiB-plus-1", "broadcast-16KiB-plus-1" })
        {
            Assert.Equal(ExchangeDirectoryState.MaximumRecordBytes + 1, Encoding.UTF8.GetByteCount(Named(name).Input));
        }
    }

    [Fact]
    public void TheBoundsChecker_AgreesWithTheServer_OnEveryDeviceCase()
    {
        // The `input` of a device case is the members of the enroll envelope. The checker must accept the five the server
        // accepted and reject the nine-modes case it closed the stream over, before it is trusted on this product's enroll.
        foreach (var item in Cases.Where(candidate => candidate.Kind == "device"))
        {
            using var input = JsonDocument.Parse(item.Input);
            var violations = ExchangeEnrollmentBounds.Violations(input.RootElement);
            Assert.Equal(item.Verdict == "closed", violations.Count > 0);
        }

        using var nine = JsonDocument.Parse(Named("device-receiver-too-many-modes").Input);
        Assert.Contains("receiver.modes has 9 entries", ExchangeEnrollmentBounds.Violations(nine.RootElement));
    }

    [Fact]
    public async Task TheProductsEnroll_DeclaresFewerEntriesThanTheCaseTheServerClosed_AndNoPlaysOverEight()
    {
        using var enroll = await ExchangeFramesReplay.ProductEnrollAsync(pairingCode: false);
        using var nine = JsonDocument.Parse(Named("device-receiver-too-many-modes").Input);
        var closedModes = nine.RootElement.GetProperty("receiver").GetProperty("modes").GetArrayLength();
        var receiver = enroll.RootElement.GetProperty("receiver");
        Assert.True(receiver.GetProperty("modes").GetArrayLength() < closedModes);
        Assert.True(receiver.GetProperty("transports").GetArrayLength() < closedModes);
        Assert.False(receiver.TryGetProperty("plays", out _));
        Assert.Empty(ExchangeEnrollmentBounds.Violations(enroll.RootElement));
    }

    // ---------------------------------------------------------------- negative proof

    [Fact]
    public async Task NegativeProof_OneChangedByteOfAListedRecord_IsNotTheDeviceTheFileDescribes()
    {
        var item = Named("device-full");
        Assert.Null(await DeviceReadFailure(item, item.Listed!));

        // "online" read as "onlinf": the device is no longer online, so the read differs from the file's.
        var presence = item.Listed!.IndexOf("\"presence\":\"online\"", StringComparison.Ordinal);
        Assert.True(presence > 0);
        var mutated = item.Listed.Remove(presence + 17, 1).Insert(presence + 17, "f");
        Assert.NotEqual(item.Listed, mutated);
        Assert.NotNull(await DeviceReadFailure(item, mutated));

        // A pair of `plays` changed in one letter: the pairs read are not the file's.
        var transport = item.Listed.IndexOf("RELAY", item.Listed.IndexOf("\"plays\"", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(transport > 0);
        Assert.NotNull(await DeviceReadFailure(item, item.Listed.Remove(transport, 1).Insert(transport, "P")));
    }
}
