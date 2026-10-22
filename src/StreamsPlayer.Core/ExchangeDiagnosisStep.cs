namespace StreamsPlayer.Core;

/// <summary>
/// The fifteen steps of the "connection does not work" ladder, in the order of <c>DEVICE-EXCHANGE</c> item CY
/// (SP-0225). The numeric value is the step's number in the contract; the order runs from this machine outward:
/// the other end, the path to it, the protected link, then the account.
/// </summary>
public enum ExchangeDiagnosisStep
{
    PcAsleep = 1,
    AddressLan = 2,
    GuestNetwork = 3,
    Vpn = 4,
    WindowsPublicNetwork = 5,
    IpFamily = 6,
    ServerInternet = 7,
    PortForward = 8,
    DeviceClock = 9,
    Certificate = 10,
    Credentials = 11,
    SignInRate = 12,
    Invitation = 13,
    DeviceRemoved = 14,
    Operator = 15
}

/// <summary>Where a step makes sense (item CY, column "Applies"): the target's own network, the internet, or both.</summary>
public enum ExchangeDiagnosisApplies
{
    Any,
    Network,
    Internet
}

public static class ExchangeDiagnosisSteps
{
    public static IReadOnlyList<ExchangeDiagnosisStep> All { get; } = Array.AsReadOnly(
        Enum.GetValues<ExchangeDiagnosisStep>().OrderBy(step => (int)step).ToArray());

    /// <summary>The contract's id of a step. It appears in a log and in a details line, never as a headline.</summary>
    public static string IdOf(ExchangeDiagnosisStep step) => step switch
    {
        ExchangeDiagnosisStep.PcAsleep => "pc-asleep",
        ExchangeDiagnosisStep.AddressLan => "address-lan",
        ExchangeDiagnosisStep.GuestNetwork => "guest-network",
        ExchangeDiagnosisStep.Vpn => "vpn",
        ExchangeDiagnosisStep.WindowsPublicNetwork => "windows-public-network",
        ExchangeDiagnosisStep.IpFamily => "ip-family",
        ExchangeDiagnosisStep.ServerInternet => "server-internet",
        ExchangeDiagnosisStep.PortForward => "port-forward",
        ExchangeDiagnosisStep.DeviceClock => "device-clock",
        ExchangeDiagnosisStep.Certificate => "certificate",
        ExchangeDiagnosisStep.Credentials => "credentials",
        ExchangeDiagnosisStep.SignInRate => "sign-in-rate",
        ExchangeDiagnosisStep.Invitation => "invitation",
        ExchangeDiagnosisStep.DeviceRemoved => "device-removed",
        ExchangeDiagnosisStep.Operator => "operator",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
    };

    /// <summary>The interface key of a step's text - the whole text of "what to try" for it (item CY).</summary>
    public static string MessageKey(ExchangeDiagnosisStep step) => "ExchangeStep" + step;

    public static ExchangeDiagnosisApplies AppliesOf(ExchangeDiagnosisStep step) => step switch
    {
        ExchangeDiagnosisStep.AddressLan or ExchangeDiagnosisStep.GuestNetwork
            or ExchangeDiagnosisStep.WindowsPublicNetwork => ExchangeDiagnosisApplies.Network,
        ExchangeDiagnosisStep.ServerInternet or ExchangeDiagnosisStep.PortForward => ExchangeDiagnosisApplies.Internet,
        _ => ExchangeDiagnosisApplies.Any
    };
}
