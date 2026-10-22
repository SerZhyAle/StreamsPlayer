using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>What a corpus' `directory` and `changed` frames did to the product's directory state.</summary>
internal sealed record DirectoryTally(int Fulls, int ChangesApplied, int Relists, int BroadcastRecords);

internal static partial class ExchangeFramesReplay
{
    // ---------------------------------------------------------------- the directory (section 7.4)

    private sealed class Mirror
    {
        internal ExchangeDirectoryState State { get; } = new();

        internal List<string> Devices { get; set; } = [];

        internal long Revision { get; set; }
    }

    /// <summary>
    /// Feeds every `directory` and `changed` frame of each stream, in order, to a fresh <see cref="ExchangeDirectoryState"/> and
    /// compares it with a mirror built from the frames' own members: a full list replaces the devices; a push exactly one
    /// revision ahead merges them; any other revision leaves the state alone and asks for a new `list`. Broadcast records
    /// are stand-ins in these corpora (a descriptor without an address), which the reader skips - they are counted, never compared.
    /// </summary>
    internal static (IReadOnlyList<string> Failures, DirectoryTally Tally) DirectoryReplay(FramesCorpus corpus)
    {
        var failures = new List<string>();
        int fulls = 0, applied = 0, relists = 0, broadcastRecords = 0;
        foreach (var transcript in corpus.Transcripts)
        {
            var mirrors = new Dictionary<string, Mirror>();
            foreach (var step in transcript.Steps.Where(candidate => candidate.ServerToClient && candidate.Type is "directory" or "changed"))
            {
                var mirror = mirrors.TryGetValue(step.Stream, out var held) ? held : mirrors[step.Stream] = new Mirror();
                using var document = step.Parse();
                var root = document.RootElement;
                var revision = root.GetProperty("revision").GetInt64();
                broadcastRecords += CountBroadcastRecords(root);
                if (step.Type == "directory")
                {
                    var transition = mirror.State.ApplyFull(root);
                    mirror.Devices = DeviceIds(root);
                    mirror.Revision = revision;
                    fulls++;
                    if (transition.RelistNeeded || !SameState(mirror, transition.Snapshot, root))
                    {
                        failures.Add(step.Label + ": the full list did not become the state the frame describes");
                    }
                }
                else if (revision == mirror.Revision + 1)
                {
                    var transition = mirror.State.ApplyChange(root);
                    List<string> upserted = root.TryGetProperty("upserts", out var upserts) ? DeviceIds(upserts) : [];
                    List<string> removed = root.TryGetProperty("removals", out var removals)
                        && removals.TryGetProperty("deviceIds", out var removedIds)
                        ? [.. removedIds.EnumerateArray().Select(id => id.GetString()!)]
                        : [];
                    mirror.Devices = [.. mirror.Devices.Union(upserted).Except(removed)];
                    mirror.Revision = revision;
                    applied++;
                    if (transition.RelistNeeded || !SameState(mirror, transition.Snapshot, null))
                    {
                        failures.Add(step.Label + ": the push one revision ahead was not merged as the frame says");
                    }
                }
                else
                {
                    var transition = mirror.State.ApplyChange(root);
                    relists++;
                    if (!transition.RelistNeeded || !SameState(mirror, transition.Snapshot, null))
                    {
                        failures.Add($"{step.Label}: revision {revision} after {mirror.Revision} must ask for a new list and change nothing");
                    }
                }
            }
        }

        return (failures, new DirectoryTally(fulls, applied, relists, broadcastRecords));
    }

    private static List<string> DeviceIds(JsonElement container) =>
        container.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array
            ? [.. devices.EnumerateArray().Select(device => ExchangeProtocol.String(device, "deviceId")!)]
            : [];

    private static int CountBroadcastRecords(JsonElement root)
    {
        var count = root.TryGetProperty("broadcasts", out var full) && full.ValueKind == JsonValueKind.Array ? full.GetArrayLength() : 0;
        return count + (root.TryGetProperty("upserts", out var upserts) && upserts.TryGetProperty("broadcasts", out var pushed)
            && pushed.ValueKind == JsonValueKind.Array ? pushed.GetArrayLength() : 0);
    }

    /// <param name="full">When the frame was a full list, its own devices are compared name by name and presence by presence.</param>
    private static bool SameState(Mirror mirror, ExchangeDirectorySnapshot snapshot, JsonElement? full)
    {
        // A state that was never listed has no device list at all (null), which is the empty list.
        var devices = snapshot.Devices ?? [];
        if (snapshot.Revision != mirror.Revision
            || !devices.Select(device => device.DeviceId).Order(StringComparer.Ordinal)
                .SequenceEqual(mirror.Devices.Order(StringComparer.Ordinal))
            || snapshot.LiveBroadcastIds.Count != 0 || snapshot.Groups.Count != 0)
        {
            return false;
        }

        if (full is not { } frame)
        {
            return true;
        }

        return frame.GetProperty("devices").EnumerateArray().All(device =>
        {
            var read = devices.Single(candidate => candidate.DeviceId == ExchangeProtocol.String(device, "deviceId"));
            return read.DeviceName == ExchangeProtocol.String(device, "deviceName")
                && read.IsOnline == (ExchangeProtocol.String(device, "presence") == "online");
        });
    }

    // ---------------------------------------------------------------- client frames the product can produce byte for byte

    internal static async Task<byte[]> WrittenAsync(object envelope)
    {
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteAsync(stream, envelope, authenticated: true, CancellationToken.None);
        return stream.ToArray();
    }

    internal static string? ByteExactFailure(Step step, byte[] produced, byte[]? expected = null) =>
        produced.AsSpan().SequenceEqual(expected ?? step.Frame)
            ? null
            : $"{step.Label}: produced {Convert.ToHexStringLower(produced)}, the file has {Convert.ToHexStringLower(expected ?? step.Frame)}";

    /// <summary>Each `cast-answer` is what the product answers the preceding `cast-offer` of its stream, by the decision the answer itself states.</summary>
    internal static async Task<IReadOnlyList<string>> CastAnswerFailuresAsync(FramesCorpus corpus)
    {
        var failures = new List<string>();
        foreach (var transcript in corpus.Transcripts)
        {
            var offers = new Dictionary<string, ExchangeCastOffer>();
            foreach (var step in transcript.Steps.Where(candidate => candidate.IsFrame))
            {
                if (step.ServerToClient && step.Type == "cast-offer")
                {
                    using var offered = step.Parse();
                    Assert.True(ExchangeCastOffer.TryParse(offered.RootElement, out var offer) && offer is not null, step.Label);
                    offers[step.Stream] = offer!;
                    continue;
                }

                if (!step.ClientToServer || step.Type != "cast-answer")
                {
                    continue;
                }

                using var document = step.Parse();
                var reason = ExchangeProtocol.String(document.RootElement, "reason");
                var offerOfStream = offers[step.Stream];
                object? answer = document.RootElement.GetProperty("accepted").GetBoolean()
                    ? ExchangeCastAnswer.For(offerOfStream, ExchangeCastDecision.Accepted)
                    : reason switch
                    {
                        "declined" => ExchangeCastAnswer.For(offerOfStream, ExchangeCastDecision.Declined),
                        "timeout" => ExchangeCastAnswer.For(offerOfStream, ExchangeCastDecision.TimedOut),
                        "unsupported" => ExchangeCastAnswer.Unsupported(offerOfStream.BroadcastId),
                        _ => null
                    };
                if (answer is null)
                {
                    failures.Add($"{step.Label}: the answer's reason '{reason}' is none the product gives");
                    continue;
                }

                if (ByteExactFailure(step, await WrittenAsync(answer)) is { } failure)
                {
                    failures.Add(failure);
                }
            }
        }

        return failures;
    }

    /// <summary>Item AT: each `password-change` is the frame the product writes for the two passwords the step itself carries.</summary>
    internal static async Task<IReadOnlyList<string>> PasswordChangeFailuresAsync(FramesCorpus corpus)
    {
        var failures = new List<string>();
        foreach (var step in corpus.Frames.Where(candidate => candidate.ClientToServer && candidate.Type == "password-change"))
        {
            using var document = step.Parse();
            var change = new ExchangePasswordChange(
                ExchangeProtocol.String(document.RootElement, "oldPassword")!.ToCharArray(),
                ExchangeProtocol.String(document.RootElement, "newPassword")!.ToCharArray());
            if (ByteExactFailure(step, await WrittenAsync(change)) is { } failure)
            {
                failures.Add(failure);
            }

            change.Clear();
        }

        return failures;
    }

    /// <summary>A relay upload chunk: the length counts the flag byte and the payload; a length of zero is the clean end (section 7.6).</summary>
    internal static async Task<byte[]> ChunkAsync(byte flags, byte[] payload, bool end)
    {
        using var stream = new MemoryStream();
        if (end)
        {
            await ExchangeUploadChunks.WriteEndAsync(stream, CancellationToken.None);
        }
        else
        {
            await ExchangeUploadChunks.WriteAsync(stream, flags, payload, CancellationToken.None);
        }

        return stream.ToArray();
    }

    internal static async Task<IReadOnlyList<string>> UploadChunkFailuresAsync(FramesCorpus corpus)
    {
        var failures = new List<string>();
        foreach (var step in corpus.Steps.Where(candidate => candidate.Chunk is not null))
        {
            var chunk = step.Chunk!;
            var end = chunk.Frame.Length == 4;
            if (ByteExactFailure(step, await ChunkAsync(chunk.Flags, chunk.Payload, end), chunk.Frame) is { } failure)
            {
                failures.Add(failure);
            }

            var read = await ExchangeUploadChunks.ReadAsync(new MemoryStream(chunk.Frame), CancellationToken.None);
            var readBack = end
                ? read is null
                : read is { } item && item.Flags == chunk.Flags && item.Payload.SequenceEqual(chunk.Payload);
            if (!readBack)
            {
                failures.Add(step.Label + ": the product's reader returns another chunk");
            }
        }

        return failures;
    }

    // ---------------------------------------------------------------- the enroll: shape, not bytes

    /// <summary>
    /// The enroll this product sends, as its own code builds it (`ExchangeEnrollmentFields` and `WriteEnrollmentAsync`, the
    /// two calls of `EnrollAsync`), for the secret the person typed: a password, or a pairing code.
    /// </summary>
    internal static async Task<JsonDocument> ProductEnrollAsync(bool pairingCode, IReadOnlyList<string>? roles = null)
    {
        using var key = ExchangeE2eKeyPair.Generate();
        var fields = ExchangeEnrollmentFields.Build("alice", ExchangeProtocol.NewDeviceId(), "STREAMS Player", "streamsplayer-windows",
            "26.1021.1200", "windows", roles ?? ["receiver", "broadcaster"], ExchangeCapabilities.Receiver, 30, key);
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteEnrollmentAsync(stream, fields, pairingCode ? "pairingCode" : "password",
            "Passw0rdTest".ToCharArray(), CancellationToken.None);
        return JsonDocument.Parse(stream.ToArray().AsMemory(4));
    }

    /// <summary>
    /// The corpus fixes the members a conforming enroll has; this product sends all of them and, beyond them, only what
    /// it is owed to add (`capabilities` and `e2eKey` of item DF) - its `receiver` and its secret come last, which a
    /// server does not read by position. Values are the device's own and are not compared.
    /// </summary>
    internal static IReadOnlyList<string> EnrollShapeFailures(FramesCorpus corpus, JsonElement withPassword, JsonElement withPairingCode)
    {
        var failures = new List<string>();
        var fixedMembers = new[] { "schemaVersion", "type" };
        var corpusMembers = new HashSet<string>();
        foreach (var step in corpus.Steps.Where(candidate => candidate.ClientToServer && candidate.Type == "enroll"))
        {
            using var document = step.Parse();
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            corpusMembers.UnionWith(names);
            var product = root.TryGetProperty("pairingCode", out _) ? withPairingCode : withPassword;
            var productNames = product.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Except(productNames).ToArray() is { Length: > 0 } missing)
            {
                failures.Add($"{step.Label}: the product's enroll lacks {string.Join(",", missing)}");
            }

            if (fixedMembers.Any(member => root.GetProperty(member).GetRawText() != product.GetProperty(member).GetRawText()))
            {
                failures.Add(step.Label + ": schemaVersion or type differ");
            }

            if (root.TryGetProperty("receiver", out var receiver) && receiver.ValueKind == JsonValueKind.Object
                && receiver.EnumerateObject().Select(property => property.Name).Except(
                    product.GetProperty("receiver").EnumerateObject().Select(property => property.Name)).Any())
            {
                failures.Add(step.Label + ": the product's receiver block lacks a member of the file's");
            }

            if (ExchangeEnrollmentBounds.Violations(root) is { Count: > 0 } violations)
            {
                failures.Add($"{step.Label}: the bounds checker rejects an enroll the real server accepted: {string.Join("; ", violations)}");
            }
        }

        foreach (var product in new[] { withPassword, withPairingCode })
        {
            var extras = product.EnumerateObject().Select(property => property.Name)
                .Except(corpusMembers.Concat(["password", "pairingCode"])).Except(["capabilities", "e2eKey"]).ToArray();
            if (extras.Length > 0)
            {
                failures.Add("the product's enroll adds " + string.Join(",", extras) + " beyond the corpus' members and item DF's two");
            }
        }

        return failures;
    }
}
