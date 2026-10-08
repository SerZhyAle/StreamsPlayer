using System.Text.Json;

namespace StreamsPlayer.Core;

public static class ExchangeHandshake
{
    public static async Task<JsonDocument> ReadAsync(Stream stream, string expectedType, CancellationToken cancellationToken)
    {
        while (true)
        {
            var document = await ExchangeProtocol.ReadAsync(stream, false, cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateSchema(document.RootElement);
                var type = ExchangeProtocol.String(document.RootElement, "type");
                if (type == expectedType || type == "refused")
                {
                    return document;
                }

                if (ExchangeProtocol.IsKnownType(type))
                {
                    throw new InvalidDataException("Invalid exchange opening state.");
                }
            }
            catch
            {
                document.Dispose();
                stream.Dispose();
                throw;
            }

            document.Dispose();
        }
    }

    public static void ValidateSchema(JsonElement root)
    {
        if (ExchangeProtocol.String(root, "type") is null
            || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var version) || version != 2)
        {
            throw new InvalidDataException("Invalid exchange schema.");
        }
    }

    public static int ReadInterval(JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(ExchangeProtocol.String(root, "account"))
            || string.IsNullOrWhiteSpace(ExchangeProtocol.String(root, "publicEndpoint"))
            || ExchangeProtocol.String(root, "serverVersion") is null
            || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array
            || features.EnumerateArray().Any(feature => feature.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException("Invalid exchange welcome fields.");
        }

        if (!root.TryGetProperty("keepaliveSeconds", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var interval) || interval is < 10 or > 120)
        {
            throw new InvalidDataException("Invalid exchange keepalive interval.");
        }

        return interval;
    }

}
