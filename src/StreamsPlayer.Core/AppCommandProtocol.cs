using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>SP-0271: the exit codes of <c>APP-COMMANDS</c> rule 11 - the portfolio's vocabulary, not a new one.</summary>
public static class AppCommandExit
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int NotProven = 2;
    public const int Usage = 10;
    public const int NotReady = 11;
    public const int Permission = 12;
    public const int Resources = 13;
    public const int Io = 14;
    public const int Network = 15;
    public const int Stopped = 16;
}

/// <summary>SP-0271: the <c>status</c> member of a response (<c>APP-COMMANDS</c> rule 7).</summary>
public static class AppCommandStatus
{
    public const string Done = "done";
    public const string Accepted = "accepted";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string Busy = "busy";
}

/// <summary>
/// SP-0271: the closed <c>reason</c> tokens of <c>APP-COMMANDS</c> rule 12, plus <see cref="Error"/>, the product's own
/// token for an exit 1 that has no more exact one (the spelling the research proposal of 2026-11-14, item 5, asks for).
/// </summary>
public static class AppCommandReason
{
    public const string UnknownVerb = "unknown-verb";
    public const string BadArgument = "bad-argument";
    public const string TooLarge = "too-large";
    public const string Schema = "schema";
    public const string SwitchOff = "switch-off";
    public const string Elevation = "elevation";
    public const string Busy = "busy";
    public const string NotRunning = "not-running";
    public const string TargetMissing = "target-missing";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string Error = "error";

    /// <summary>
    /// SP-0279, <c>APP-COMMANDS</c> item L: a verb's <c>data</c> is over the 64 KiB bound of a response line and the verb is not
    /// paged. Failed, exit 1.
    /// </summary>
    public const string ResponseTooLarge = "response-too-large";

    /// <summary>
    /// SP-0272, the product's own token: a stream that was asked for did not open or could not be played. Exit 1, or 15
    /// when the host or the network could not be reached (rule 11).
    /// </summary>
    public const string StreamFailed = "stream-failed";

    /// <summary>
    /// SP-0272, the product's own token (the research proposal of 2026-11-14, CyrFlip item 3 b): the verb would have to
    /// ask a person at the desktop - a link password - and rule 4 forbids it. Refused, exit 12.
    /// </summary>
    public const string NeedsPerson = "needs-person";

    /// <summary>
    /// SP-0273, the token the research proposal of 2026-11-14 (StreamsPlayer item 5) asks <c>APP-COMMANDS</c> to adopt: the
    /// volume the verb writes to has no room left. Failed, exit 13.
    /// </summary>
    public const string NoSpace = "no-space";

    /// <summary>
    /// SP-0273, from the same proposal: a file or folder the verb needed could not be written. Failed, exit 14.
    /// </summary>
    public const string IoError = "io-error";

    /// <summary>
    /// SP-0273, the product's own token: the stream cannot be recorded by design (a one-listener broadcast, a format a byte
    /// copy cannot record, a video engine with no recording). Failed, exit 1.
    /// </summary>
    public const string NotRecordable = "not-recordable";
}

/// <summary>How a line that arrived on the activation pipe reads (SP-0271).</summary>
public enum AppCommandLineKind
{
    /// <summary>No <c>requestId</c>: the frozen launch line of SP-0118, answered exactly as before.</summary>
    Legacy,

    /// <summary>A well-formed request of this profile.</summary>
    Request,

    /// <summary>It carries a <c>requestId</c> but is not a request this version can read.</summary>
    Malformed,

    /// <summary>A request of a newer <c>schemaVersion</c> than <see cref="AppCommandRequest.CurrentSchema"/>.</summary>
    NewerSchema
}

/// <summary>A pipe line read as a profile line, with the request id when one could be read.</summary>
public sealed record AppCommandLineRead(AppCommandLineKind Kind, AppCommandRequest? Request, string RequestId);

/// <summary>
/// SP-0271: the request of the <c>APP-COMMANDS</c> rule 7 profile - one JSON line, UTF-8, from the second process to
/// the first. Members are matched by name and unknown ones are ignored; <see cref="CallerPid"/> is this product's own
/// extra member, read for the rule-16 log line only.
/// </summary>
public sealed record AppCommandRequest(string Command, IReadOnlyList<string> Args, string RequestId)
{
    public const int CurrentSchema = 1;

    public const string CliOrigin = "cli";

    public const string WaitDone = "done";

    public string? WorkingDir { get; init; }

    public string Origin { get; init; } = CliOrigin;

    public string Wait { get; init; } = WaitDone;

    public string? ResultPath { get; init; }

    public string? EventsPath { get; init; }

    public int? CallerPid { get; init; }

    /// <summary>A new request for <paramref name="command"/> with a fresh request id.</summary>
    public static AppCommandRequest Create(string command, IReadOnlyList<string> args) =>
        new(command, args, Guid.NewGuid().ToString("D"));

    /// <summary>The newline-terminated UTF-8 line.</summary>
    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchema);
            writer.WriteString("command", Command);
            writer.WriteStartArray("args");
            foreach (var argument in Args)
            {
                writer.WriteStringValue(AppCommandText.Scrub(argument));
            }

            writer.WriteEndArray();
            WriteOptional(writer, "workingDir", WorkingDir is null ? null : AppCommandText.Scrub(WorkingDir));
            writer.WriteString("origin", Origin);
            writer.WriteString("requestId", RequestId);
            writer.WriteString("wait", Wait);
            WriteOptional(writer, "resultPath", ResultPath is null ? null : AppCommandText.Scrub(ResultPath));
            WriteOptional(writer, "eventsPath", EventsPath is null ? null : AppCommandText.Scrub(EventsPath));
            if (CallerPid is { } pid)
            {
                writer.WriteNumber("callerPid", pid);
            }

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads one pipe line. Never throws: a line that is not a JSON object with a string <c>requestId</c> is
    /// <see cref="AppCommandLineKind.Legacy"/> and goes to the frozen reader unchanged.
    /// </summary>
    public static AppCommandLineRead Read(ReadOnlySpan<byte> line)
    {
        var legacy = new AppCommandLineRead(AppCommandLineKind.Legacy, null, string.Empty);
        if (line.IsEmpty || line.Length > AppCommandLimits.MaximumRequestBytes)
        {
            return legacy;
        }

        try
        {
            using var document = JsonDocument.Parse(AppCommandText.Utf8.GetString(line).TrimEnd('\r', '\n'));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String)
            {
                return legacy;
            }

            var requestId = id.GetString() ?? string.Empty;
            var malformed = new AppCommandLineRead(AppCommandLineKind.Malformed, null, requestId);
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var schema) || schema < 1)
            {
                return malformed;
            }

            if (schema > CurrentSchema)
            {
                return new AppCommandLineRead(AppCommandLineKind.NewerSchema, null, requestId);
            }

            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(command.GetString()))
            {
                return malformed;
            }

            var args = new List<string>();
            if (root.TryGetProperty("args", out var array))
            {
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > AppCommandLimits.MaximumArgumentCount)
                {
                    return malformed;
                }

                foreach (var argument in array.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String)
                    {
                        return malformed;
                    }

                    args.Add(argument.GetString()!);
                }
            }

            var request = new AppCommandRequest(command.GetString()!, args, requestId)
            {
                WorkingDir = OptionalString(root, "workingDir"),
                Origin = OptionalString(root, "origin") ?? CliOrigin,
                Wait = OptionalString(root, "wait") ?? WaitDone,
                ResultPath = OptionalString(root, "resultPath"),
                EventsPath = OptionalString(root, "eventsPath"),
                CallerPid = root.TryGetProperty("callerPid", out var pid) && pid.ValueKind == JsonValueKind.Number &&
                    pid.TryGetInt32(out var pidValue) ? pidValue : null
            };
            return new AppCommandLineRead(AppCommandLineKind.Request, request, requestId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return legacy;
        }
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>
/// SP-0271: the response of the <c>APP-COMMANDS</c> rule 7 profile - one JSON line from the first instance, or one the
/// second process makes itself when it answers locally (a cold start, a usage error, a timeout).
/// </summary>
public sealed record AppCommandResponse(string RequestId, string Status, int ExitCode, string Message)
{
    public string? Reason { get; init; }

    public JsonObject? Data { get; init; }

    public static AppCommandResponse Done(string requestId, string message, JsonObject? data = null) =>
        new(requestId, AppCommandStatus.Done, AppCommandExit.Done, message) { Data = data };

    public static AppCommandResponse Refused(string requestId, string reason, int exitCode, string message) =>
        new(requestId, AppCommandStatus.Refused, exitCode, message) { Reason = reason };

    public static AppCommandResponse Failed(string requestId, string reason, int exitCode, string message) =>
        new(requestId, AppCommandStatus.Failed, exitCode, message) { Reason = reason };

    public static AppCommandResponse Busy(string requestId, string message) =>
        new(requestId, AppCommandStatus.Busy, AppCommandExit.NotReady, message) { Reason = AppCommandReason.Busy };

    /// <summary>The response line, without the newline.</summary>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", AppCommandRequest.CurrentSchema);
            writer.WriteString("requestId", RequestId);
            writer.WriteString("status", Status);
            writer.WriteNumber("exitCode", ExitCode);
            if (Reason is not null)
            {
                writer.WriteString("reason", Reason);
            }

            writer.WriteString("message", AppCommandText.Scrub(Message));
            if (Data is not null)
            {
                writer.WritePropertyName("data");
                Data.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return AppCommandText.Utf8.GetString(buffer.ToArray());
    }

    /// <summary>The newline-terminated UTF-8 line that travels on the pipe.</summary>
    public byte[] Serialize() => AppCommandText.Utf8.GetBytes(ToJsonLine() + "\n");

    /// <summary>
    /// Reads a response line. <see langword="false"/> for anything that is not a JSON object with an integer
    /// <c>schemaVersion</c> - which is how an older first instance's plain <c>ok</c> or <c>refused</c> reads.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out AppCommandResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out _) ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number ||
                !exit.TryGetInt32(out var exitCode))
            {
                return false;
            }

            response = new AppCommandResponse(
                root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : string.Empty,
                status.GetString()!,
                exitCode,
                root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()!
                    : string.Empty)
            {
                Reason = root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null,
                Data = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(data.GetRawText()) as JsonObject
                    : null
            };
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>SP-0271: the bounds of <c>APP-COMMANDS</c> rule 3.</summary>
public static class AppCommandLimits
{
    /// <summary>A request is at most this many bytes of UTF-8 - the same bound the frozen launch line has.</summary>
    public const int MaximumRequestBytes = ActivationMessage.MaximumPayloadBytes;

    /// <summary>One argument is at most this many bytes of UTF-8.</summary>
    public const int MaximumArgumentBytes = 32 * 1024;

    /// <summary>More arguments than this is refused, as on the launch line.</summary>
    public const int MaximumArgumentCount = ActivationMessage.MaximumArgumentCount;

    /// <summary>
    /// The longest response line (item L), written by the first instance and read by the second process; this product's answers
    /// are a few hundred bytes. A longer one is replaced by <see cref="AppCommandReason.ResponseTooLarge"/>.
    /// </summary>
    public const int MaximumResponseBytes = 64 * 1024;

    /// <summary>
    /// SP-0279, item J: how long the first instance lets its window thread take a command before it answers <c>busy</c> itself -
    /// shorter than the delivery bound (<c>AppCommandClient.DeliveryBound</c>, 1 s) that the second process counts from the
    /// accepted connection, so the busy answer arrives inside it. A command not taken in this time is never run later.
    /// </summary>
    public static readonly TimeSpan StartBound = TimeSpan.FromMilliseconds(800);

    /// <summary>The completion bound of rule 8 when a verb states none, and the most a verb may state.</summary>
    public const int DefaultTimeoutMs = 10_000;

    public const int MaximumTimeoutMs = 600_000;
}

internal static class AppCommandText
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>A lone surrogate cannot be written as JSON text; replaced with U+FFFD as the launch line does (SP-0184).</summary>
    internal static string Scrub(string value) =>
        value.Any(char.IsSurrogate) ? Utf8.GetString(Utf8.GetBytes(value)) : value;
}
