using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

public sealed record DriveTokens(string AccessToken, string? RefreshToken, int ExpiresIn)
{
    public override string ToString() => nameof(DriveTokens);
}

public sealed class DriveRequestException(int statusCode) : IOException($"Google Drive request failed ({statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The single-scope installed-app flow. Browser and protected storage belong to the host.</summary>
public sealed class DriveOAuth(HttpClient client, string clientId, string? clientSecret)
{
    public const string Scope = "https://www.googleapis.com/auth/drive.appdata";
    public static string NewVerifier() => ExchangeBase64Url.Encode(RandomNumberGenerator.GetBytes(48));
    public static string Challenge(string verifier) => ExchangeBase64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string AuthorizationUrl(string redirect, string state, string verifier) =>
        "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = Scope, ["state"] = state, ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent"
        }.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

    public Task<DriveTokens> ExchangeAsync(string code, string redirect, string verifier, CancellationToken cancellation) =>
        RequestAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, cancellation);

    public Task<DriveTokens> RefreshAsync(string refreshToken, CancellationToken cancellation) =>
        RequestAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellation);

    public async Task RevokeAsync(string token, CancellationToken cancellation)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var response = await client.PostAsync("https://oauth2.googleapis.com/revoke", body, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DriveRequestException((int)response.StatusCode);
    }

    private async Task<DriveTokens> RequestAsync(Dictionary<string, string> fields, CancellationToken cancellation)
    {
        fields["client_id"] = clientId;
        if (!string.IsNullOrEmpty(clientSecret))
            fields["client_secret"] = clientSecret;
        using var body = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", body, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DriveRequestException((int)response.StatusCode);
        var bytes = await DriveRestClient.ReadBoundedAsync(response.Content, 65536, cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException("Google token response exceeds the limit.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var access = ExchangeProtocol.String(root, "access_token");
        var refresh = ExchangeProtocol.String(root, "refresh_token");
        if (ExchangeProtocol.String(root, "scope") is { } scope
            && !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { Scope }))
        {
            // Incremental consent can return an older wider grant. Never retain it for this source.
            await RevokeAsync(refresh ?? access ?? throw new InvalidDataException("Missing Google token."), cancellation).ConfigureAwait(false);
            throw new InvalidDataException("Google granted a different scope.");
        }
        if (string.IsNullOrEmpty(access) || !root.TryGetProperty("expires_in", out var expiry)
            || !expiry.TryGetInt32(out var seconds) || seconds <= 0)
            throw new InvalidDataException("Google returned no usable access token.");
        return new DriveTokens(access, refresh, seconds);
    }
}
