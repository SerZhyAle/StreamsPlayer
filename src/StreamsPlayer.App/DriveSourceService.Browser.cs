using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class DriveSourceService
{
    private static async Task<DriveTokens> SignInBrowserAsync(DriveOAuth oauth, CancellationToken cancellation)
    {
        // Reserve a loopback port, then let http.sys own the exact callback prefix for this one operation.
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var redirect = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var verifier = DriveOAuth.NewVerifier();
        var state = DriveOAuth.NewVerifier();
        Process.Start(new ProcessStartInfo(oauth.AuthorizationUrl(redirect, state, verifier)) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var query = context.Request.QueryString;
        var valid = context.Request.Url?.AbsolutePath == "/" && query["state"] == state;
        context.Response.StatusCode = valid ? 200 : 400;
        context.Response.ContentType = "text/plain; charset=utf-8";
        var message = System.Text.Encoding.UTF8.GetBytes(LocalizationService.Get("DriveBrowserReturn"));
        await context.Response.OutputStream.WriteAsync(message, timeout.Token);
        context.Response.Close();
        if (!valid || query["error"] is not null || query["code"] is not { Length: > 0 } code)
            throw new InvalidOperationException("Google sign-in did not complete.");
        return await oauth.ExchangeAsync(code, redirect, verifier, timeout.Token);
    }
}
