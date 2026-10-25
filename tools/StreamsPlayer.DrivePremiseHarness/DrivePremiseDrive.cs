using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace StreamsPlayer.DrivePremiseHarness;

/// <summary>One file of the account's Drive application data folder, as far as the listing reads it.</summary>
public sealed record DriveAppDataFile(string Id, string Name, string? MimeType, long? Size, DateTimeOffset? ModifiedTime);

/// <summary>
/// The Drive v3 calls of SP-0202's premise experiment and nothing more: create a file in <c>appDataFolder</c>,
/// list that space, read one file's bytes, delete one file. No other Drive surface is touched - the product's
/// poll-and-refresh behaviour is gated work this tool must not anticipate.
/// </summary>
public sealed class DrivePremiseDrive(HttpClient client)
{
    private const string FilesBaseUrl = "https://www.googleapis.com/drive/v3/files";
    private const string UploadBaseUrl = "https://www.googleapis.com/upload/drive/v3/files";

    /// <summary>Creates the probe file in the application data folder and returns the created Drive file.</summary>
    public async Task<DriveAppDataFile> UploadAsync(string accessToken, string fileName, string content, CancellationToken cancellation)
    {
        // Drive's multipart upload is the only create call that names the parent space in metadata; the boundary
        // is fixed-length so the body can be built as one buffer, not a streamed multipart of two parts.
        const string boundary = "sp0202premise";
        var metadata = $$"""{"name":"{{fileName}}","mimeType":"application/json","parents":["appDataFolder"]}""";
        var body = new StringBuilder()
            .Append($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{metadata}\r\n")
            .Append($"--{boundary}\r\nContent-Type: application/json\r\n\r\n{content}\r\n")
            .Append($"--{boundary}--")
            .ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadBaseUrl}?uploadType=multipart&fields=id,name,mimeType,size,modifiedTime");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        using var response = await client.SendAsync(request, cancellation);
        var responseText = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The Drive upload failed ({(int)response.StatusCode}): {responseText}");
        }
        using var document = System.Text.Json.JsonDocument.Parse(responseText);
        return ReadFile(document.RootElement);
    }

    /// <summary>Lists every file of the application data folder, following Drive's pages.</summary>
    public async Task<IReadOnlyList<DriveAppDataFile>> ListAppDataAsync(string accessToken, CancellationToken cancellation)
    {
        var files = new List<DriveAppDataFile>();
        string? pageToken = null;
        do
        {
            var url = FilesBaseUrl + "?spaces=appDataFolder&pageSize=100" +
                "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)" +
                (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellation);
            var responseText = await response.Content.ReadAsStringAsync(cancellation);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The Drive listing failed ({(int)response.StatusCode}): {responseText}");
            }
            using var document = System.Text.Json.JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (root.TryGetProperty("files", out var entries))
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    files.Add(ReadFile(entry));
                }
            }
            pageToken = root.TryGetProperty("nextPageToken", out var token) && token.ValueKind == System.Text.Json.JsonValueKind.String
                ? token.GetString()
                : null;
        }
        while (pageToken is not null);
        return files;
    }

    /// <summary>Reads one file's bytes, or <c>null</c> when the file is larger than the contract's record ceiling -
    /// a listing of the premise experiment never downloads a file a conforming reader would skip.</summary>
    public async Task<byte[]?> ReadMediaAsync(string accessToken, string fileId, int maxBytes, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FilesBaseUrl}/{Uri.EscapeDataString(fileId)}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Reading a Drive file failed ({(int)response.StatusCode}).");
        }
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
        {
            return null;
        }
        var content = await response.Content.ReadAsByteArrayAsync(cancellation);
        return content.Length > maxBytes ? null : content;
    }

    public async Task DeleteAsync(string accessToken, string fileId, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{FilesBaseUrl}/{Uri.EscapeDataString(fileId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Deleting a Drive file failed ({(int)response.StatusCode}).");
        }
    }

    private static DriveAppDataFile ReadFile(System.Text.Json.JsonElement entry) => new(
        entry.GetProperty("id").GetString()!,
        entry.GetProperty("name").GetString()!,
        entry.TryGetProperty("mimeType", out var mime) && mime.ValueKind == System.Text.Json.JsonValueKind.String ? mime.GetString() : null,
        entry.TryGetProperty("size", out var size) && size.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(size.GetString(), out var parsed) ? parsed : null,
        entry.TryGetProperty("modifiedTime", out var modified) && modified.ValueKind == System.Text.Json.JsonValueKind.String && DateTimeOffset.TryParse(modified.GetString(), out var stamp) ? stamp : null);
}
