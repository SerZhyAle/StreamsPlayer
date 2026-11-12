namespace StreamsPlayer.Core;

/// <summary>The descriptor's shape only: no address, credential, source identifier, pin or seal key.</summary>
public static class BroadcastOpenDiagnostics
{
    public static string[] Fields(FastMediaSorterBroadcastInfo broadcast) =>
    [
        $"auth={(broadcast.Auth is null ? "absent" : "present")}",
        $"scheme={(broadcast.Auth is null ? "none" : broadcast.Auth.Scheme == ExchangeBroadcastAuth.SupportedScheme ? ExchangeBroadcastAuth.SupportedScheme : "unknown")}",
        "endpoints=" + string.Join(';', broadcast.Endpoints.Select((endpoint, index) =>
            $"{index}:{Transport(endpoint.Transport)}:sealed={(endpoint.Seal is null ? "false" : "true")}"))
    ];

    private static string Transport(string? value) => value?.ToUpperInvariant() switch
    {
        "HTTP" => "HTTP",
        "RTSP" => "RTSP",
        "RELAY" => "RELAY",
        "TUNNEL" => "TUNNEL",
        "P2P" => "P2P",
        null => "unspecified",
        _ => "unknown"
    };
}
