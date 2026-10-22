namespace StreamsPlayer.Core;

public enum ExchangeQrOutcomeKind
{
    /// <summary>Exactly one QR code was found; <see cref="ExchangeQrOutcome.Text"/> is its payload.</summary>
    Found,

    /// <summary>The picture holds no QR code this reader can decode.</summary>
    None,

    /// <summary>The picture holds more than one QR code, so none of them is chosen for the person.</summary>
    Several,

    /// <summary>The picture is larger than the reader will decode, or is not an image at all.</summary>
    Unreadable
}

public sealed record ExchangeQrOutcome(ExchangeQrOutcomeKind Kind, string? Text = null);

/// <summary>
/// SP-0221 requirement 2: a QR is another way to carry the connection string and goes through the same codec
/// (DEVICE-EXCHANGE 5.5, "a QR of a connection string carries the text above, byte for byte"). This is the part
/// of reading a picture that needs no pixels: what the decoder found becomes one outcome, never a guess.
/// </summary>
public static class ExchangeQrImport
{
    public static ExchangeQrOutcome Interpret(IReadOnlyList<string> payloads) => payloads.Count switch
    {
        0 => new ExchangeQrOutcome(ExchangeQrOutcomeKind.None),
        1 => new ExchangeQrOutcome(ExchangeQrOutcomeKind.Found, payloads[0]),
        _ => new ExchangeQrOutcome(ExchangeQrOutcomeKind.Several)
    };
}
