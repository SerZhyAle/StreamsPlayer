using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0118: the one line a later copy sends the running copy - the <c>APP-ACTIVATION</c> rule 4 JSON
/// Lines payload, carrying the later copy's raw command-line arguments.
/// </summary>
/// <remarks>
/// The arguments travel unparsed on purpose. The running copy feeds them to
/// <see cref="StreamLaunchRequest.Parse"/> exactly as its own command line was fed, so a forwarded launch
/// cannot be interpreted differently from a direct one - including an invalid one, which reports itself
/// on the status line the same way. Parsing here is strict and never throws: anything that is not a
/// well-formed version-1 <c>open</c> line is refused, and unknown fields are skipped (contract §3).
/// </remarks>
public static class ActivationMessage
{
    public const int SchemaVersion = 1;

    public const string OpenCommand = "open";

    /// <summary>The payload's <c>origin</c> field - informational only, never read back.</summary>
    public const string Origin = "second-launch";

    /// <summary>A line longer than this is refused without being decoded.</summary>
    public const int MaximumPayloadBytes = 64 * 1024;

    /// <summary>More arguments than this is refused; a real launch carries at most four (SP-0127).</summary>
    public const int MaximumArgumentCount = 32;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>The newline-terminated UTF-8 line for <paramref name="arguments"/>.</summary>
    public static byte[] Serialize(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("command", OpenCommand);
            writer.WriteStartArray("args");
            foreach (var argument in arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteString("origin", Origin);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>
    /// SP-0170: <see cref="Serialize"/> for a sender that has to honour the receiver's limits. <see langword="false"/>
    /// when <paramref name="arguments"/> are more than <see cref="MaximumArgumentCount"/> or the line, as it
    /// would travel (non-ASCII text is escaped six-fold), is longer than <see cref="MaximumPayloadBytes"/> -
    /// both of which <see cref="TryParse(string?, out IReadOnlyList{string}?)"/> refuses on the other end.
    /// </summary>
    public static bool TrySerialize(IReadOnlyList<string> arguments, [NotNullWhen(true)] out byte[]? payload)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        payload = null;
        if (arguments.Count > MaximumArgumentCount)
        {
            return false;
        }

        var line = Serialize(arguments);
        if (line.Length - 1 > MaximumPayloadBytes)
        {
            return false;
        }

        payload = line;
        return true;
    }

    /// <summary>
    /// Reads one payload line received from the pipe. <see langword="false"/> for anything empty,
    /// oversized, malformed, of another schema version, or carrying a command other than <c>open</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> line, [NotNullWhen(true)] out IReadOnlyList<string>? arguments)
    {
        arguments = null;
        if (line.IsEmpty || line.Length > MaximumPayloadBytes)
        {
            return false;
        }

        return TryParse(Utf8.GetString(line), out arguments);
    }

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, out IReadOnlyList{string}?)"/>
    public static bool TryParse(string? line, [NotNullWhen(true)] out IReadOnlyList<string>? arguments)
    {
        arguments = null;
        if (string.IsNullOrWhiteSpace(line) || Utf8.GetByteCount(line) > MaximumPayloadBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.TrimEnd('\r', '\n'));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != SchemaVersion ||
                !root.TryGetProperty("command", out var command) ||
                command.ValueKind != JsonValueKind.String ||
                command.GetString() != OpenCommand)
            {
                return false;
            }

            var values = new List<string>();
            if (root.TryGetProperty("args", out var args))
            {
                if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() > MaximumArgumentCount)
                {
                    return false;
                }

                foreach (var argument in args.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    values.Add(argument.GetString()!);
                }
            }

            arguments = values;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
