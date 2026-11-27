using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0303: the target's own refusals of <c>REMOTE-CONTROL</c> rule 13, beside the <c>APP-COMMANDS</c> reasons of
/// <see cref="AppCommandReason"/>. Each carries the status and exit code rule 13 gives it.
/// </summary>
public static class RemoteCommandReason
{
    /// <summary>The switch is off, the controller is not ticked, or the class is not granted to it. Refused, exit 12.</summary>
    public const string NotPermitted = "not-permitted";

    /// <summary>The person declined on the target, or did not answer in time. Refused, exit 16.</summary>
    public const string Declined = "declined";

    /// <summary>The request is outside the time window of rule 12. Refused, exit 2.</summary>
    public const string Expired = "expired";
}

/// <summary>STRWIN-0303: the English sentences of a remote answer. A controller shows its own words, never these (rule 5).</summary>
public static class RemoteCommandMessages
{
    public const string NotPermitted = "This program does not accept this command from this device.";
    public const string Declined = "The person on the other device did not accept this command.";
    public const string Expired = "The command arrived outside its time window and was not run.";
    public const string Malformed = "The command request is not one this program reads.";
    public const string Newer = "The command request is newer than this program reads.";
    public const string RingFull = "Too many commands arrived in a short time; send it again later.";
}

/// <summary>
/// STRWIN-0303: the clear payload of a <c>command-request</c> box (<c>REMOTE-CONTROL</c> rule 5): <c>v</c>, <c>verb</c>,
/// <c>args</c>, <c>issuedAt</c> and <c>ttlSeconds</c>. An unknown member is never ignored (section 3).
/// </summary>
public sealed record RemoteCommandRequest(string Verb, JsonElement Args, DateTimeOffset IssuedAt, int TtlSeconds)
{
    public const int CurrentVersion = 1;
    public const int DefaultTtlSeconds = 120;
    public const int MinimumTtlSeconds = 10;
    public const int MaximumTtlSeconds = 600;

    private static readonly HashSet<string> Members = new(StringComparer.Ordinal) { "v", "verb", "args", "issuedAt", "ttlSeconds" };

    /// <summary>
    /// Reads the opened payload. On failure <paramref name="refusal"/> is the answer: <c>schema</c> for a <c>v</c> above 1,
    /// <c>bad-argument</c> for anything else that is not a request. Never throws.
    /// </summary>
    public static RemoteCommandRequest? TryParse(ReadOnlySpan<byte> payload, string requestId, out AppCommandResponse? refusal)
    {
        refusal = AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage, RemoteCommandMessages.Malformed);
        if (payload.Length > RemoteCommandAnswer.MaximumPayloadBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v < 1)
            {
                return null;
            }

            if (v > CurrentVersion)
            {
                refusal = AppCommandResponse.Refused(requestId, AppCommandReason.Schema, AppCommandExit.NotProven, RemoteCommandMessages.Newer);
                return null;
            }

            if (root.EnumerateObject().Any(member => !Members.Contains(member.Name))
                || !root.TryGetProperty("verb", out var verb) || verb.ValueKind != JsonValueKind.String
                || verb.GetString() is not { Length: > 0 } verbName || !AppCommandLine.IsVerb(verbName)
                || !root.TryGetProperty("issuedAt", out var issued) || issued.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(issued.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var issuedAt))
            {
                return null;
            }

            var ttl = DefaultTtlSeconds;
            if (root.TryGetProperty("ttlSeconds", out var ttlElement)
                && (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out ttl)
                    || ttl < MinimumTtlSeconds || ttl > MaximumTtlSeconds))
            {
                return null;
            }

            var args = root.TryGetProperty("args", out var argsElement) ? argsElement : default;
            if (args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            {
                return null;
            }

            refusal = null;
            return new RemoteCommandRequest(verbName, args.ValueKind == JsonValueKind.Object ? args.Clone() : default,
                issuedAt, ttl);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException
                                              or DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>STRWIN-0303: the clear payload of a <c>command-result</c> box (<c>REMOTE-CONTROL</c> rule 5).</summary>
public static class RemoteCommandAnswer
{
    /// <summary>Item GD and rule 5: the clear payload of a box, request or answer, is at most 65536 bytes of UTF-8.</summary>
    public const int MaximumPayloadBytes = 65536;

    /// <summary>
    /// The UTF-8 bytes of the answer to <paramref name="response"/>. <c>data</c> members are dropped from the end until it
    /// fits, and <c>truncated</c> then says so.
    /// </summary>
    public static byte[] Serialize(AppCommandResponse response)
    {
        var data = response.Data?.DeepClone().AsObject();
        var truncated = false;
        while (true)
        {
            var bytes = Write(response, data, truncated);
            if (bytes.Length <= MaximumPayloadBytes || data is null || data.Count == 0)
            {
                return bytes;
            }

            data.Remove(data.Last().Key);
            truncated = true;
        }
    }

    private static byte[] Write(AppCommandResponse response, JsonObject? data, bool truncated)
    {
        var answer = new JsonObject
        {
            ["v"] = RemoteCommandRequest.CurrentVersion,
            ["status"] = response.Status,
            ["exit"] = response.ExitCode,
            ["message"] = AppCommandText.Scrub(response.Message),
            ["truncated"] = truncated
        };
        if (response.Reason is { } reason)
        {
            answer["reason"] = reason;
        }

        if (data is not null)
        {
            answer["data"] = data.DeepClone();
        }

        return Encoding.UTF8.GetBytes(answer.ToJsonString());
    }

    public static AppCommandResponse NotPermitted(string requestId) =>
        AppCommandResponse.Refused(requestId, RemoteCommandReason.NotPermitted, AppCommandExit.Permission, RemoteCommandMessages.NotPermitted);

    public static AppCommandResponse Declined(string requestId) =>
        AppCommandResponse.Refused(requestId, RemoteCommandReason.Declined, AppCommandExit.Stopped, RemoteCommandMessages.Declined);

    public static AppCommandResponse Expired(string requestId) =>
        AppCommandResponse.Refused(requestId, RemoteCommandReason.Expired, AppCommandExit.NotProven, RemoteCommandMessages.Expired);
}
