namespace StreamsPlayer.Core;

/// <summary>
/// The four states of `DEVICE-EXCHANGE` item DR that sealed content adds. None of them is a wire reason: each
/// is something this program knows locally and tells the person, in words, instead of failing silently.
/// </summary>
public enum ExchangeSealedState
{
    None,
    /// <summary>`e2e-missing` - a device with no usable key; nothing is sent to it through the server.</summary>
    E2eMissing,
    /// <summary>`key-changed` - a device whose key differs from the one first seen; nothing is sent to it.</summary>
    KeyChanged,
    /// <summary>`server-no-e2e` - the server does not advertise `sealed`; content does not go through it.</summary>
    ServerNoE2e,
    /// <summary>`sealed-failed` - something sealed could not be opened on this device.</summary>
    SealedFailed
}

public static class ExchangeSealedStates
{
    /// <summary>The contract's code (item DR), for tests and for a log line that names the state and nothing else.</summary>
    public static string Code(this ExchangeSealedState state) => state switch
    {
        ExchangeSealedState.E2eMissing => "e2e-missing",
        ExchangeSealedState.KeyChanged => "key-changed",
        ExchangeSealedState.ServerNoE2e => "server-no-e2e",
        ExchangeSealedState.SealedFailed => "sealed-failed",
        _ => ""
    };

    /// <summary>The dictionary key of the sentence that says it to the person.</summary>
    public static string MessageKey(this ExchangeSealedState state) => state switch
    {
        ExchangeSealedState.E2eMissing => "ExchangeStateE2eMissing",
        ExchangeSealedState.KeyChanged => "ExchangeStateKeyChanged",
        ExchangeSealedState.ServerNoE2e => "ExchangeStateServerNoE2e",
        ExchangeSealedState.SealedFailed => "ExchangeStateSealedFailed",
        _ => ""
    };

    public static ExchangeSealedState FromCode(string? code) => code switch
    {
        "e2e-missing" => ExchangeSealedState.E2eMissing,
        "key-changed" => ExchangeSealedState.KeyChanged,
        "server-no-e2e" => ExchangeSealedState.ServerNoE2e,
        "sealed-failed" => ExchangeSealedState.SealedFailed,
        _ => ExchangeSealedState.None
    };
}
