using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// The one replay of a connection-string conformance case (DEVICE-EXCHANGE 5.5), shared by the artifact of
/// SP-0213 and the account-lifecycle corpus of FMS_W 224: input, verdict, reason, fields, canonical bytes.
/// </summary>
internal static class ConnectionStringVectorReplay
{
    internal static IEnumerable<object[]> Cases(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    internal static async Task ReplayAsync(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var input = item.TryGetProperty("inputHex", out var hex) ? Convert.FromHexString(hex.GetString()!)
            : Encoding.UTF8.GetBytes(Optional(item, "input") ?? "");
        ExchangeConnectionStringResult result;
        if (item.GetProperty("kind").GetString() == "file")
        {
            using var stream = new MemoryStream(input);
            result = await ExchangeConnectionStringCodec.ReadAsync(stream);
        }
        else
        {
            result = ExchangeConnectionStringCodec.Parse(new UTF8Encoding(false, true).GetString(input));
        }

        var valid = item.GetProperty("verdict").GetString() != "refused";
        Assert.True(valid == result.Success, $"{name}: {result.Reason}");
        if (!valid)
        {
            Assert.Null(result.Data);
            Assert.Equal(item.GetProperty("reason").GetString(), result.Reason);
            return;
        }

        Assert.Null(result.Reason);
        var data = result.Data!;
        var fields = item.GetProperty("fields");
        Assert.Equal(fields.GetProperty("host").GetString(), data.Host);
        Assert.Equal(fields.GetProperty("port").GetInt32(), data.Port);
        Assert.Equal(Optional(fields, "fp"), data.Fingerprint);
        Assert.Equal(Optional(fields, "n"), data.Name);
        Assert.Equal(Optional(fields, "u"), data.Login);
        // The invitation code is validated and dropped (this product never registers); only its presence is kept.
        Assert.Equal(Optional(fields, "i") is not null, data.HasInvitation);
        var canonical = ExchangeConnectionStringCodec.Encode(data);
        var expected = item.GetProperty("canonical").GetString()!;
        if (data.HasInvitation)
        {
            // Encode never writes the invitation: the canonical text of the corpus carries it, ours must not.
            Assert.DoesNotContain("&i=", canonical);
            Assert.StartsWith(expected[..expected.IndexOf("&i=", StringComparison.Ordinal)], canonical, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(canonical));
        }

        var endpoint = data.Host.Contains(':') ? $"[{data.Host}]:{data.Port}" : $"{data.Host}:{data.Port}";
        Assert.Equal(fields.GetProperty("x").GetString(), endpoint);
    }

    private static string? Optional(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.GetString() : null;
}
