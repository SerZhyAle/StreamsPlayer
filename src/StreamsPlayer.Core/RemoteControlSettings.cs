namespace StreamsPlayer.Core;

/// <summary>STRWIN-0303: the four risk classes of <c>APP-COMMANDS</c> rule 13, as <c>REMOTE-CONTROL</c> rule 9 grants them.</summary>
public static class RemoteRiskClass
{
    public const string Safe = "safe";
    public const string ReadsUserData = "reads-user-data";
    public const string WritesUserData = "writes-user-data";
    public const string LeavesMachine = "leaves-machine";

    public static IReadOnlyList<string> All { get; } = [Safe, ReadsUserData, WritesUserData, LeavesMachine];
}

/// <summary>STRWIN-0303: the consent of <c>REMOTE-CONTROL</c> rule 10 for one granted class.</summary>
public static class RemoteConsent
{
    public const string Ask = "ask";
    public const string Always = "always";

    /// <summary>Rule 10 (0.3): <c>always</c> for <c>safe</c>, <c>ask</c> for every other class.</summary>
    public static string DefaultFor(string riskClass) =>
        string.Equals(riskClass, RemoteRiskClass.Safe, StringComparison.Ordinal) ? Always : Ask;
}

/// <summary>
/// STRWIN-0303: one controller the person ticked (rule 9) - the device, the name the person saw for it, and for each
/// granted class its consent (rule 10). A class that is not a key is not granted.
/// </summary>
public sealed record RemoteControllerGrant(string DeviceId, string DeviceName, IReadOnlyDictionary<string, string> Classes)
{
    /// <summary>Rule 9: a newly ticked controller gets <c>safe</c> and nothing else, with its default consent.</summary>
    public static RemoteControllerGrant Ticked(string deviceId, string deviceName) =>
        new(deviceId, deviceName, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RemoteRiskClass.Safe] = RemoteConsent.DefaultFor(RemoteRiskClass.Safe)
        });

    public string? ConsentFor(string riskClass) => Classes.TryGetValue(riskClass, out var consent) ? consent : null;

    /// <summary>The grant with <paramref name="riskClass"/> granted under <paramref name="consent"/>, or withdrawn when it is null.</summary>
    public RemoteControllerGrant With(string riskClass, string? consent)
    {
        var classes = new Dictionary<string, string>(Classes, StringComparer.Ordinal);
        if (consent is null)
        {
            classes.Remove(riskClass);
        }
        else
        {
            classes[riskClass] = consent;
        }

        return this with { Classes = classes };
    }
}

/// <summary>
/// STRWIN-0303: what the person decided on this device (<c>REMOTE-CONTROL</c> rules 8 to 10). It is kept in a document
/// of its own, never in the catalog state, the standard settings exchanged with the server or an imported file, so
/// nothing but the person's own act in this program changes it (rule 8). Absent or unreadable is <see cref="Off"/>.
/// </summary>
public sealed record RemoteControlSettings(bool Listening, IReadOnlyList<RemoteControllerGrant> Grants)
{
    public static RemoteControlSettings Off { get; } = new(false, []);

    public RemoteControllerGrant? GrantFor(string deviceId) =>
        Grants.FirstOrDefault(grant => string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal));

    /// <summary>Replaces or adds <paramref name="grant"/>, keeping the others in their order.</summary>
    public RemoteControlSettings WithGrant(RemoteControllerGrant grant) => this with
    {
        Grants = GrantFor(grant.DeviceId) is null
            ? [.. Grants, grant]
            : Grants.Select(existing => string.Equals(existing.DeviceId, grant.DeviceId, StringComparison.Ordinal) ? grant : existing).ToArray()
    };

    public RemoteControlSettings WithoutGrant(string deviceId) => this with
    {
        Grants = Grants.Where(grant => !string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal)).ToArray()
    };

    /// <summary>
    /// Rule 9: a controller revoked from the account or whose key changed drops out at once, with its grants.
    /// <paramref name="usableControllers"/> are the devices the account lists now with a key this device still trusts.
    /// </summary>
    public RemoteControlSettings Retaining(IReadOnlySet<string> usableControllers) => this with
    {
        Grants = Grants.Where(grant => usableControllers.Contains(grant.DeviceId)).ToArray()
    };

    /// <summary>A document read from disk with every unusable piece dropped: unknown classes and consents, blank ids, repeats.</summary>
    public RemoteControlSettings Sanitized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var grants = new List<RemoteControllerGrant>();
        foreach (var grant in Grants ?? [])
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.DeviceId) || !seen.Add(grant.DeviceId))
            {
                continue;
            }

            var classes = (grant.Classes ?? new Dictionary<string, string>())
                .Where(pair => RemoteRiskClass.All.Contains(pair.Key) && pair.Value is RemoteConsent.Ask or RemoteConsent.Always)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            grants.Add(new RemoteControllerGrant(grant.DeviceId, grant.DeviceName ?? string.Empty, classes));
        }

        return new RemoteControlSettings(Listening, grants);
    }
}

/// <summary>What the target does with one opened request once the time and the ring have let it through.</summary>
public enum RemoteDecisionKind
{
    /// <summary>Run the verb now.</summary>
    Run,

    /// <summary>Ask the person on this device first (rule 10), then run or answer <c>declined</c>.</summary>
    Ask,

    /// <summary>Answer <see cref="RemoteDecision.Refusal"/> and run nothing.</summary>
    Refuse
}

public sealed record RemoteDecision(RemoteDecisionKind Kind, AppCommandEntry? Entry, AppCommandResponse? Refusal);

/// <summary>
/// STRWIN-0303: rules 6 to 10 of <c>REMOTE-CONTROL</c> in the order that tells a controller nothing it may not know:
/// the switch, then the controller, then the verb, then its class, then consent. A controller that is not ticked
/// therefore cannot learn which verbs exist, and a verb that is not <c>remote</c> reads as one that does not exist
/// (<c>unknown-verb</c>, rule 13). The local setting of <c>APP-COMMANDS</c> rule 13 is never read here.
/// </summary>
public static class RemoteControlPolicy
{
    public static RemoteDecision Decide(
        string requestId, RemoteControlSettings settings, string controllerId, string verb, AppCommandManifest manifest)
    {
        if (!settings.Listening || settings.GrantFor(controllerId) is not { } grant)
        {
            return new(RemoteDecisionKind.Refuse, null, RemoteCommandAnswer.NotPermitted(requestId));
        }

        var entry = manifest.Commands.FirstOrDefault(command => string.Equals(command.Name, verb, StringComparison.Ordinal));
        if (entry?.Remote is null)
        {
            return new(RemoteDecisionKind.Refuse, null, AppCommandResponse.Refused(requestId, AppCommandReason.UnknownVerb,
                AppCommandExit.Usage, AppCommandMessages.UnknownVerb(verb)));
        }

        if (grant.ConsentFor(entry.Risk) is not { } consent)
        {
            return new(RemoteDecisionKind.Refuse, entry, RemoteCommandAnswer.NotPermitted(requestId));
        }

        // A confirm-device verb asks whatever the person chose for its class (rule 10).
        var ask = entry.Remote.ConfirmsOnDevice || string.Equals(consent, RemoteConsent.Ask, StringComparison.Ordinal);
        return new(ask ? RemoteDecisionKind.Ask : RemoteDecisionKind.Run, entry, null);
    }
}
