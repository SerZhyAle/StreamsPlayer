using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

public sealed record DriveFile(string Id, string Name, DateTimeOffset ModifiedTime, long? Size);
public sealed record DriveListing(DateTimeOffset ServerTime, IReadOnlyList<DriveFile> Files);

/// <summary>Only the app-data records used by the broadcast source. No resource or packet access.</summary>
public sealed class DriveRestClient(HttpClient client, Func<CancellationToken, Task<string>> accessToken)
{
    private const string FilesUrl = "https://www.googleapis.com/drive/v3/files";
    public async Task<DriveListing> ListAsync(CancellationToken cancellation)
    {
        var query = Uri.EscapeDataString("trashed = false and (name contains 'fmsx-broadcast-' or name contains 'fmsx-device-')");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{FilesUrl}?spaces=appDataFolder&pageSize=1000&q={query}&fields=nextPageToken,files(id,name,size,modifiedTime)");
        using var response = await SendAsync(request, cancellation).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException("Drive listing exceeds the limit.");
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Drive returned an invalid listing.");
        // A partial list would wrongly end channels; a later poll must never silently replace the full view with it.
        if (json.RootElement.TryGetProperty("nextPageToken", out _))
            throw new InvalidDataException("Drive has more records than one listing can hold.");
        var files = new List<DriveFile>();
        foreach (var file in entries.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || ExchangeProtocol.String(file, "id") is not { Length: > 0 } id
                || ExchangeProtocol.String(file, "name") is not { } name || !DriveRecords.IsRecordName(name)
                || !DateTimeOffset.TryParse(ExchangeProtocol.String(file, "modifiedTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var modified))
                continue;
            long? size = long.TryParse(ExchangeProtocol.String(file, "size"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var length) ? length : null;
            files.Add(new DriveFile(id, name, modified, size));
        }
        return new DriveListing(response.Headers.Date ?? throw new InvalidDataException("Drive returned no server clock."), files);
    }

    public async Task<byte[]?> ReadAsync(DriveFile file, CancellationToken cancellation)
    {
        if (!DriveRecords.IsRecordName(file.Name) || file.Size > DriveRecords.MaximumBytes)
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FilesUrl}/{Uri.EscapeDataString(file.Id)}?alt=media");
        using var response = await SendAsync(request, cancellation).ConfigureAwait(false);
        return await ReadBoundedAsync(response.Content, DriveRecords.MaximumBytes, cancellation).ConfigureAwait(false);
    }

    public async Task<string> WriteAsync(string name, byte[] record, string? fileId, CancellationToken cancellation)
    {
        if (!DriveRecords.IsRecordName(name) || record.Length > DriveRecords.MaximumBytes)
            throw new ArgumentException("Invalid Drive record.");
        using var request = fileId is null
            ? new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id")
            : new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media&fields=id");
        if (fileId is null)
        {
            var multipart = new MultipartContent("related");
            multipart.Add(new StringContent(JsonSerializer.Serialize(new { name, mimeType = "application/json", parents = new[] { "appDataFolder" } }), Encoding.UTF8, "application/json"));
            multipart.Add(new ByteArrayContent(record) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } });
            request.Content = multipart;
        }
        else
            request.Content = new ByteArrayContent(record) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var response = await SendAsync(request, cancellation).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, 65536, cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException("Drive write response exceeds the limit.");
        using var json = JsonDocument.Parse(bytes);
        return ExchangeProtocol.String(json.RootElement, "id") ?? throw new InvalidDataException("Drive returned no file id.");
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{FilesUrl}/{Uri.EscapeDataString(fileId)}");
        using var response = await SendAsync(request, cancellation, allowMissing: true).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation, bool allowMissing = false)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(cancellation).ConfigureAwait(false));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || allowMissing && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new DriveRequestException(status);
    }

    public static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximumBytes)
            return null;
        await using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes + 1)];
        while (output.Length <= maximumBytes)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), cancellation).ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            output.Write(buffer, 0, count);
        }
        return null;
    }
}
