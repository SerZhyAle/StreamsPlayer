using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0303: a forwarded <c>command-request</c> (<c>DEVICE-EXCHANGE</c> 1.18 item GC). <see cref="FromDeviceId"/> is
/// routing only: the controller is the <c>from</c> of the box that opened (<c>REMOTE-CONTROL</c> rule 4).
/// </summary>
public sealed record ExchangeCommandRequestFrame(string FromDeviceId, string RequestId, ExchangeSealedBox Box);

/// <summary>STRWIN-0303: the two frames of item GC as a target reads and writes them.</summary>
public static class ExchangeCommandFrames
{
    public const string RequestType = "command-request";
    public const string ResultType = "command-result";

    private const int MaximumRequestIdLength = 64;

    /// <summary>Item GC: 1 to 64 characters of <c>[A-Za-z0-9_-]</c>.</summary>
    public static bool IsRequestId(string? value) =>
        value is { Length: > 0 and <= MaximumRequestIdLength }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    /// <summary>A forwarded request, or null when a member is missing or mistyped (dropped: there is nobody to answer).</summary>
    public static ExchangeCommandRequestFrame? ReadRequest(JsonElement root)
    {
        var from = ExchangeProtocol.String(root, "fromDeviceId");
        var requestId = ExchangeProtocol.String(root, "requestId");
        if (from is not { Length: > 0 } || !IsRequestId(requestId)
            || !root.TryGetProperty("box", out var boxElement) || ExchangeSealedBox.TryRead(boxElement) is not { } box)
        {
            return null;
        }

        return new ExchangeCommandRequestFrame(from, requestId!, box);
    }

    /// <summary>The answer, sealed to the controller alone, addressed to it (item GC, first hop).</summary>
    public static object Result(string toDeviceId, string requestId, ExchangeSealedBox box) => new
    {
        schemaVersion = 2,
        type = ResultType,
        toDeviceId,
        requestId,
        box
    };
}
