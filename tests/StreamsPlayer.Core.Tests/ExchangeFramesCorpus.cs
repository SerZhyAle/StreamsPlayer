using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>The constants block of a frames corpus: the bounds the generating server enforced.</summary>
internal sealed record FrameConstants(int MaxFrame, int MaxFrameAuth, int MaxChunkLength, int MaxRecord, int SchemaVersion);

/// <summary>A relay upload chunk step: the flag byte, the payload, and the whole frame as it crossed the stream.</summary>
internal sealed record ChunkStep(byte Flags, byte[] Payload, byte[] Frame);

/// <summary>One step of a transcript. `Hex` is the whole frame (4-byte big-endian length, then the body); `Json` is the body.</summary>
internal sealed record Step(
    string Transcript, int Number, string Stream, string Direction, string? Hex, string? Json, bool Closed, ChunkStep? Chunk, bool IsHttp)
{
    internal bool ClientToServer => Direction == "client->server";

    internal bool ServerToClient => Direction == "server->client";

    internal bool IsFrame => Hex is not null;

    /// <summary>`transcript#step(stream)`, the name a failing assertion gives.</summary>
    internal string Label => $"{Transcript}#{Number}({Stream})";

    internal byte[] Frame => Convert.FromHexString(Hex ?? throw new InvalidOperationException(Label + " has no hex."));

    internal string? Type
    {
        get
        {
            if (Json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(Json);
            return ExchangeProtocol.String(document.RootElement, "type");
        }
    }

    internal JsonDocument Parse() => JsonDocument.Parse(Json ?? throw new InvalidOperationException(Label + " has no json."));
}

internal sealed record Transcript(string Name, IReadOnlyList<Step> Steps, IReadOnlyDictionary<string, string> StreamTransports);

/// <summary>One entry of the top-level `chunks` list. `Flags` and `Payload` are absent where the file omits them.</summary>
internal sealed record ChunkCase(string Name, byte[] Frame, byte Flags, byte[] Payload, string Verdict, string? Why);

/// <summary>
/// A frames corpus of the FMS_Win exchange server (`vectors/server-frames-v2.json`, `vectors/fmsw-224/frames-v2.json`),
/// read once. The files are vendored byte for byte and pinned by hash; a test that reads a field out of them reads
/// it from here, so no expected value is typed twice.
/// </summary>
internal sealed record FramesCorpus(
    string File, string Sha256, string ContractVersion, int Rung, FrameConstants Constants,
    IReadOnlyList<Transcript> Transcripts, IReadOnlyList<ChunkCase> Chunks)
{
    internal IEnumerable<Step> Steps => Transcripts.SelectMany(transcript => transcript.Steps);

    internal Transcript Named(string name) => Transcripts.Single(transcript => transcript.Name == name);

    internal IEnumerable<Step> Frames => Steps.Where(step => step.IsFrame);
}

internal static class ExchangeFramesCorpus
{
    internal const string ServerFrames = "server/server-frames-v2.json";
    internal const string ServerRecords = "server/server-records-v1.json";
    internal const string AccountFrames = "fmsw-224/frames-v2.json";

    internal static string PathOf(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Vectors", relative.Replace('/', Path.DirectorySeparatorChar));

    internal static string Sha256Of(string relative) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(PathOf(relative))));

    internal static FramesCorpus Load(string relative)
    {
        var bytes = File.ReadAllBytes(PathOf(relative));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var constants = root.GetProperty("constants");
        return new FramesCorpus(
            relative,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            root.GetProperty("contractVersion").GetString()!,
            root.GetProperty("rung").GetInt32(),
            new FrameConstants(
                constants.GetProperty("maxFrame").GetInt32(), constants.GetProperty("maxFrameAuth").GetInt32(),
                constants.GetProperty("maxChunkLength").GetInt32(), constants.GetProperty("maxRecord").GetInt32(),
                constants.GetProperty("schemaVersion").GetInt32()),
            [.. root.GetProperty("transcripts").EnumerateArray().Select(ReadTranscript)],
            [.. root.GetProperty("chunks").EnumerateArray().Select(ReadChunkCase)]);
    }

    private static Transcript ReadTranscript(JsonElement element)
    {
        var name = element.GetProperty("name").GetString()!;
        var transports = element.GetProperty("streams").EnumerateArray()
            .ToDictionary(stream => stream.GetProperty("id").GetString()!, stream => stream.GetProperty("transport").GetString()!);
        var steps = new List<Step>();
        foreach (var step in element.GetProperty("steps").EnumerateArray())
        {
            steps.Add(new Step(
                name, steps.Count + 1, step.GetProperty("stream").GetString()!, step.GetProperty("direction").GetString()!,
                Text(step, "hex"), Text(step, "json"), step.TryGetProperty("closed", out var closed) && closed.GetBoolean(),
                step.TryGetProperty("chunk", out var chunk)
                    ? new ChunkStep((byte)(chunk.TryGetProperty("flags", out var flags) ? flags.GetInt32() : 0),
                        Convert.FromHexString(chunk.GetProperty("payloadHex").GetString()!),
                        Convert.FromHexString(chunk.GetProperty("hex").GetString()!))
                    : null,
                step.TryGetProperty("http", out _)));
        }

        return new Transcript(name, steps, transports);
    }

    private static ChunkCase ReadChunkCase(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        Convert.FromHexString(element.GetProperty("hex").GetString()!),
        (byte)(element.TryGetProperty("flags", out var flags) ? flags.GetInt32() : 0),
        Convert.FromHexString(Text(element, "payloadHex") ?? ""),
        element.GetProperty("verdict").GetString()!,
        Text(element, "why"));

    private static string? Text(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
