using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

namespace StreamsPlayer.DrivePremiseHarness;

/// <summary>
/// The installed-app sign-in SP-0202's premise experiment runs on the Windows side: the system browser, PKCE and a
/// loopback redirect (ticket requirement 2), the one scope <c>drive.appdata</c> and nothing else. The access token
/// lives in memory for the single command that asked for it and is never written anywhere: this is a rung-3
/// instrument, not the product's token storage, which is gated work.
/// </summary>
public static class DrivePremiseOAuth
{
    public const string AppDataScope = "https://www.googleapis.com/auth/drive.appdata";

    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private static readonly TimeSpan BrowserAnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Signs the run in with the given Desktop-app client and returns an access token for the one scope.</summary>
    /// <exception cref="InvalidOperationException">the user closed the browser, the redirect echoed the wrong
    /// <c>state</c>, or Google refused the token exchange. Response bodies are never reported.</exception>
    public static async Task<string> SignInAsync(HttpClient client, string clientId, CancellationToken cancellation, string? clientSecret = null)
    {
        var verifier = DrivePremisePkce.NewVerifier();
        var state = DrivePremisePkce.NewState();
        var (listener, redirectUri) = StartLoopbackListener();

        var authorizationUrl = BuildAuthorizationUrl(clientId, redirectUri, state, DrivePremisePkce.Challenge(verifier));

        Console.WriteLine();
        Console.WriteLine("Opening the system browser for the Google sign-in...");
        try
        {
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            listener.Close();
            throw new InvalidOperationException("Could not open the system browser for sign-in.");
        }

        string authorizationCode;
        try
        {
            authorizationCode = await ReceiveAuthorizationCodeAsync(listener, state, cancellation);
        }
        finally
        {
            listener.Close();
        }

        return await ExchangeTokenAsync(client, clientId, redirectUri, verifier, authorizationCode, cancellation, clientSecret);
    }

    private static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge)
    {
        static string Pair(string name, string value) => $"{name}={Uri.EscapeDataString(value)}";
        return AuthorizationEndpoint + "?" + string.Join("&", new[]
        {
            Pair("client_id", clientId),
            Pair("redirect_uri", redirectUri),
            Pair("response_type", "code"),
            Pair("scope", AppDataScope),
            Pair("state", state),
            Pair("code_challenge", codeChallenge),
            Pair("code_challenge_method", "S256"),
        });
    }

    private static (HttpListener Listener, string RedirectUri) StartLoopbackListener()
    {
        // A handful of attempts keeps two harness runs side by side possible; http.sys allows an interactive user
        // the exact localhost prefix without any URL ACL.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = Random.Shared.Next(49152, 65536);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://localhost:{port}/");
            }
            catch (HttpListenerException)
            {
            }
        }
        throw new InvalidOperationException("No loopback port could be bound for the redirect.");
    }

    private static async Task<string> ReceiveAuthorizationCodeAsync(HttpListener listener, string expectedState, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(BrowserAnswerTimeout);
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("The browser never answered the loopback redirect (5 minutes).");
            }

            var query = context.Request.QueryString;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html";
            var answer = "<html><body style=\"font-family:Segoe UI,sans-serif\"><p>The harness received the sign-in answer. Return to the console window.</p></body></html>"u8.ToArray();
            context.Response.ContentLength64 = answer.Length;
            await context.Response.OutputStream.WriteAsync(answer, timeout.Token);
            context.Response.Close();

            if (query["error"] is { Length: > 0 } error)
            {
                throw new InvalidOperationException("Google did not authorize the sign-in.");
            }
            if (query["state"] != expectedState)
            {
                throw new InvalidOperationException("The redirect echoed a state this run did not issue; the answer was refused.");
            }
            if (query["code"] is { Length: > 0 } code)
            {
                return code;
            }
        }
    }

    internal static async Task<string> ExchangeTokenAsync(HttpClient client, string clientId, string redirectUri, string verifier, string authorizationCode, CancellationToken cancellation, string? clientSecret = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = authorizationCode,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            fields["client_secret"] = clientSecret;
        }
        using var body = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(TokenEndpoint, body, cancellation);
        var responseText = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The token exchange failed ({(int)response.StatusCode}).");
        }
        using var document = System.Text.Json.JsonDocument.Parse(responseText);
        if (document.RootElement.TryGetProperty("access_token", out var token) && token.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return token.GetString()!;
        }
        throw new InvalidOperationException("The token answer carried no access_token.");
    }
}
