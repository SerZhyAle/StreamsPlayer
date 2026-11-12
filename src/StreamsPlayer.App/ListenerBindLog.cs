using System.Globalization;

namespace StreamsPlayer.App;

/// <summary>
/// <c>APP-BEHAVIOUR</c> rule 4 (0.11): a listener the user invoked is inside the rule only while it logs every
/// bind, so the listening-port check can match each LISTEN socket it finds to a line here. Only the owner, the
/// scope and the port are written: the LAN address is this machine's private address and a relay proxy's
/// loopback URL carries its token, so neither belongs in a log that may be mailed.
/// </summary>
internal static class ListenerBindLog
{
    public static void Loopback(string owner, int port) => Record(owner, "loopback", port);

    public static void Lan(string owner, int port) => Record(owner, "lan", port);

    private static void Record(string owner, string scope, int port) =>
        HandlerBoundary.Record(
            $"LISTEN | owner={owner} | scope={scope} | port={port.ToString(CultureInfo.InvariantCulture)}");
}
