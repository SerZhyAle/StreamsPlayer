using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// The replay of a frames corpus against this product's own wire code (DEVICE-EXCHANGE sections 5 to 7, the rule of the
/// corpus header: "a client replays a transcript by encoding every client->server step to exactly its `hex` and decoding
/// every server->client step to the same values"). Every check returns the failures it found - a step label and what
/// differed - and the tests assert there are none, so a negative proof can hand the same check one tampered byte and see
/// it fail. Expected values come from the file; the only values typed here are the product's own mapping tables.
/// </summary>
internal static partial class ExchangeFramesReplay
{
    /// <summary>
    /// What `refused.reason` shows in this product: the message key of its row. The three reasons that are not the
    /// product's to raise (`protection-unsupported`, `invitation-invalid`, `login-taken`: it publishes no protected
    /// resource and never registers, SP-0221) are shown as the generic refusal, which is the contract's rule for a reason a
    /// build does not know (section 7.10).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RefusalRows = new Dictionary<string, string>
    {
        ["bad-credentials"] = "ExchangeBadCredentials",
        ["device-revoked"] = "ExchangeDeviceRevoked",
        ["signed-out"] = "ExchangeSignedOut",
        ["version-unsupported"] = "ExchangeVersionUnsupported",
        ["tls-required"] = "ExchangeTlsRequired",
        ["id-collision"] = "ExchangeIdCollision",
        ["capacity"] = "ExchangeCapacity",
        ["port-unavailable"] = "ExchangePortUnavailable",
        ["unavailable"] = "ExchangeUnavailable",
        ["rate-limited"] = "ExchangeRateLimited",
        ["password-policy"] = "ExchangePasswordPolicy",
        ["limit-reached"] = "ExchangeLimitReached",
        ["not-permitted"] = "ExchangeNotPermitted",
        ["protection-unsupported"] = "ExchangeRefused",
        ["invitation-invalid"] = "ExchangeRefused",
        ["login-taken"] = "ExchangeRefused"
    };

    internal static readonly IReadOnlyDictionary<string, string> CastResultRows = new Dictionary<string, string>
    {
        ["declined"] = "BroadcastCastDeclined",
        ["unsupported"] = "BroadcastCastUnsupported",
        ["unavailable"] = "BroadcastCastUnavailable",
        ["timeout"] = "BroadcastCastTimeout"
    };

    // ---------------------------------------------------------------- framing

    /// <summary>Each step with its stream's state: a stream is authenticated once `welcome` or `enrolled` has been sent on it.</summary>
    internal static IEnumerable<(Step Step, bool Authenticated)> InStreamOrder(Transcript transcript)
    {
        var authenticated = new HashSet<string>();
        foreach (var step in transcript.Steps)
        {
            yield return (step, authenticated.Contains(step.Stream));
            if (step.ServerToClient && step.Type is "enrolled" or "welcome")
            {
                authenticated.Add(step.Stream);
            }
        }
    }

    /// <summary>The length prefix counts the body, the body is the `json`, and the body fits the limit of the stream's state.</summary>
    internal static string? FramingFailure(Step step, byte[] frame, bool authenticated, FrameConstants limits)
    {
        if (frame.Length < 5)
        {
            return step.Label + ": shorter than a prefix and one byte of body";
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (declared != frame.Length - 4)
        {
            return $"{step.Label}: the prefix says {declared}, the body is {frame.Length - 4}";
        }

        if (Encoding.UTF8.GetString(frame.AsSpan(4)) != step.Json)
        {
            return step.Label + ": the body is not the json";
        }

        var limit = authenticated ? limits.MaxFrameAuth : limits.MaxFrame;
        return declared > limit ? $"{step.Label}: {declared} is over the {limit} limit of the stream's state" : null;
    }

    /// <summary>The product's reader returns the frame's own text for a frame the server wrote.</summary>
    internal static async Task<string?> DecodeFailureAsync(Step step, byte[] frame, bool authenticated)
    {
        try
        {
            using var document = await ExchangeProtocol.ReadAsync(new MemoryStream(frame), authenticated, CancellationToken.None);
            return document.RootElement.GetRawText() == step.Json ? null : step.Label + ": decoded to other text";
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or JsonException)
        {
            return $"{step.Label}: {exception.GetType().Name}";
        }
    }

    internal static async Task<IReadOnlyList<string>> FramingAndDecodeFailuresAsync(FramesCorpus corpus)
    {
        var failures = new List<string>();
        foreach (var transcript in corpus.Transcripts)
        {
            foreach (var (step, authenticated) in InStreamOrder(transcript).Where(item => item.Step.IsFrame))
            {
                var frame = step.Frame;
                if (FramingFailure(step, frame, authenticated, corpus.Constants) is { } framing)
                {
                    failures.Add(framing);
                }

                if (step.ServerToClient && await DecodeFailureAsync(step, frame, authenticated) is { } decode)
                {
                    failures.Add(decode);
                }
            }
        }

        return failures;
    }

    // ---------------------------------------------------------------- the handshake and the refusals

    /// <summary>The first thing the product reads on a stream it opened: the corpus' own bytes through `ExchangeHandshake`.</summary>
    internal static async Task<(IReadOnlyList<string> Failures, int Streams)> OpeningReplayAsync(FramesCorpus corpus)
    {
        var failures = new List<string>();
        var streams = 0;
        foreach (var transcript in corpus.Transcripts)
        {
            foreach (var stream in transcript.Steps.Select(step => step.Stream).Distinct()
                         .Where(id => transcript.StreamTransports[id] == "tls"))
            {
                var opening = transcript.Steps.Where(step => step.Stream == stream && step.IsFrame).Take(2).ToArray();
                if (opening is not [{ ClientToServer: true } first, { ServerToClient: true } answer]
                    || first.Type is not ("enroll" or "hello"))
                {
                    continue;
                }

                streams++;
                var expected = first.Type == "enroll" ? "enrolled" : "welcome";
                using var document = await ExchangeHandshake.ReadAsync(new MemoryStream(answer.Frame), expected, CancellationToken.None);
                var type = ExchangeProtocol.String(document.RootElement, "type");
                if (type != answer.Type || (type != expected && type != "refused"))
                {
                    failures.Add($"{answer.Label}: the handshake returned {type}");
                }
            }
        }

        return (failures, streams);
    }

    /// <summary>`enrolled` and `welcome` through the validators the product applies, with the account kind the file names.</summary>
    internal static string? AccountFrameFailure(Step step)
    {
        using var document = step.Parse();
        var root = document.RootElement;
        try
        {
            ExchangeHandshake.ValidateSchema(root);
            var interval = ExchangeHandshake.ReadInterval(root);
            if (interval != root.GetProperty("keepaliveSeconds").GetInt32())
            {
                return step.Label + ": the interval read is not the frame's";
            }
        }
        catch (InvalidDataException exception)
        {
            return $"{step.Label}: {exception.Message}";
        }

        // Item AO: a member that is absent is a 0.20 server's member account; a name this build does not know reads as guest.
        var named = ExchangeProtocol.String(root, "accountKind");
        var expectedKind = named is null or "member" ? ExchangeAccountKind.Member : ExchangeAccountKind.Guest;
        if (ExchangeAccountKinds.Parse(named) != expectedKind)
        {
            return step.Label + ": the account kind read is not the file's";
        }

        return step.Type == "enrolled" && !(ExchangeProtocol.String(root, "deviceToken") is { } token && IsDeviceToken(token))
            ? step.Label + ": the device token is not 43 characters of base64url"
            : null;
    }

    /// <summary>The token rule the product applies inline on `enrolled` and `password-changed` (43 characters of base64url).</summary>
    internal static bool IsDeviceToken(string token) =>
        token.Length == 43 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// A refusal carries `reason` and nothing else (it names no request), and the reason maps to the row the product
    /// shows; <paramref name="rows"/> is the table, a parameter so a proof can hand it one wrong entry.
    /// </summary>
    internal static string? RefusalFailure(Step step, IReadOnlyDictionary<string, string> rows)
    {
        using var document = step.Parse();
        var root = document.RootElement;
        var members = root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (!members.SequenceEqual(["reason", "schemaVersion", "type"]))
        {
            return $"{step.Label}: a refusal carries {string.Join(",", members)}";
        }

        var reason = ExchangeProtocol.String(root, "reason");
        if (reason is null || !rows.TryGetValue(reason, out var expected))
        {
            return $"{step.Label}: reason '{reason}' has no row in the table";
        }

        var key = ExchangeProtocol.RefusalKey(reason);
        if (key != expected)
        {
            return $"{step.Label}: '{reason}' shows {key}, not {expected}";
        }

        // A row that is not the generic one is named by the wire reason itself (the table's "Code" column).
        return expected != "ExchangeRefused" && ExchangeFailureRows.OfWire(reason).Code != reason
            ? $"{step.Label}: the row of '{reason}' has another code"
            : null;
    }

    // ---------------------------------------------------------------- what the product reads after the handshake

    /// <summary>Every server->client frame of a type the product reads, parsed by the code that reads it.</summary>
    internal static IReadOnlyList<string> ReadingFailures(FramesCorpus corpus)
    {
        var failures = new List<string>();
        // One ledger per stream: a cast belongs to the device that accepted it, and a stop on another device's stream is not its own.
        var ledgers = new Dictionary<string, ExchangeCastLedger>();
        foreach (var step in corpus.Frames.Where(candidate => candidate.ServerToClient))
        {
            using var document = step.Parse();
            var root = document.RootElement;
            var ledger = ledgers.TryGetValue(step.Transcript + "/" + step.Stream, out var held)
                ? held
                : ledgers[step.Transcript + "/" + step.Stream] = new ExchangeCastLedger();
            switch (step.Type)
            {
                case "enrolled" or "welcome":
                    Add(failures, AccountFrameFailure(step));
                    break;
                case "refused":
                    Add(failures, RefusalFailure(step, RefusalRows));
                    break;
                case "keepalive" or "pairing-code" or "published" or "broadcast-started" or "upload-accepted"
                    or "connected" or "password-changed":
                    if (!ExchangeProtocol.IsKnownType(step.Type))
                    {
                        failures.Add(step.Label + ": the type is not one the product knows");
                    }

                    break;
                case "account-deleted":
                    // Not a type this product knows, so its control loop ignores it: it never sends `account-delete`.
                    if (ExchangeProtocol.IsKnownType(step.Type))
                    {
                        failures.Add(step.Label + ": the product knows a type it never uses");
                    }

                    break;
                case "directory" or "changed":
                    break;
                case "open":
                    Add(failures, OpenFailure(step, root));
                    break;
                case "cast-offer":
                    Add(failures, CastOfferFailure(step, root, ledger));
                    break;
                case "cast-stop":
                    Add(failures, CastStopFailure(step, root, ledger));
                    break;
                case "cast-result":
                    Add(failures, CastResultFailure(step, root));
                    break;
                default:
                    failures.Add($"{step.Label}: a '{step.Type}' frame that nothing here accounts for");
                    break;
            }
        }

        return failures;
    }

    private static void Add(List<string> failures, string? failure)
    {
        if (failure is not null)
        {
            failures.Add(failure);
        }
    }

    private static string? OpenFailure(Step step, JsonElement root)
    {
        var parsed = ExchangeTunnelOpen.TryParse(root);
        if (!root.TryGetProperty("broadcastId", out _))
        {
            // An `open` for a resource or a share is a producer's duty; this product publishes neither and does not read it.
            return parsed is null ? null : step.Label + ": a resource open was read as a broadcast tunnel";
        }

        return parsed is null || parsed.TunnelId != ExchangeProtocol.String(root, "tunnelId")
            || parsed.BroadcastId != ExchangeProtocol.String(root, "broadcastId")
            || parsed.Scheme != (ExchangeProtocol.String(root, "scheme") ?? BroadcastDescriptorWriter.HttpTunnelScheme)
            ? step.Label + ": the tunnel open was not read as the frame says"
            : null;
    }

    /// <summary>
    /// The offer's broadcast record in this corpus is a stand-in (the server is content-agnostic: `descriptor` is
    /// `{"endpoints":[]}`), so the offer is answerable but not playable: read, never an error, and `Unsupported`.
    /// </summary>
    private static string? CastOfferFailure(Step step, JsonElement root, ExchangeCastLedger ledger)
    {
        if (!ExchangeCastOffer.TryParse(root, out var offer) || offer is null)
        {
            return step.Label + ": the cast offer was not read";
        }

        var record = root.GetProperty("broadcast");
        if (offer.BroadcastId != ExchangeProtocol.String(record, "broadcastId")
            || offer.DeviceId != ExchangeProtocol.String(root, "fromDeviceId")
            || offer.Title != ExchangeProtocol.String(record, "title")
            || offer.Descriptor is not null || offer.Support != ExchangeBroadcastSupport.Unsupported || offer.CastId is not null)
        {
            return step.Label + ": the cast offer was not read as the frame says";
        }

        ledger.Track(offer.BroadcastId, offer.CastId, _ => true);
        return null;
    }

    private static string? CastStopFailure(Step step, JsonElement root, ExchangeCastLedger ledger)
    {
        if (!ExchangeCastStop.TryParse(root, out var stop) || stop is null
            || stop.BroadcastId != ExchangeProtocol.String(root, "broadcastId") || stop.CastId is not null)
        {
            return step.Label + ": the cast stop was not read as the frame says";
        }

        // A stop ends only a cast this device started (the offer registered it on this stream); on any other
        // stream - the caster's copy of the same stop - it matches nothing and is ignored.
        var started = ledger.Casts.Any(cast => cast.BroadcastId == stop.BroadcastId);
        var ended = ledger.Stop(stop);
        return (ended is null ? !started : ended.StopRequested)
            ? null
            : step.Label + ": the stop did not end exactly the cast this stream's device had started";
    }

    private static string? CastResultFailure(Step step, JsonElement root)
    {
        var result = ExchangeCastResult.TryParse(root);
        if (result is null)
        {
            return step.Label + ": the cast result was not read";
        }

        var accepted = root.GetProperty("accepted").GetBoolean();
        var reason = ExchangeProtocol.String(root, "reason");
        var expectedKey = accepted ? "BroadcastCastAccepted" : CastResultRows.GetValueOrDefault(reason ?? "", "BroadcastCastUnavailable");
        return result.Accepted != accepted || result.BroadcastId != ExchangeProtocol.String(root, "broadcastId")
            || result.ReceiverDeviceId != ExchangeProtocol.String(root, "receiverDeviceId") || result.MessageKey != expectedKey
            ? step.Label + ": the cast result was not read as the frame says"
            : null;
    }
}
