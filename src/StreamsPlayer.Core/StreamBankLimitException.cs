namespace StreamsPlayer.Core;

/// <summary>
/// A stream-bank archive, or one of its parts, is larger than the ceiling that guards the read (SP-0184 S11-1).
/// <see cref="System.IO.InvalidDataException"/> is sealed, so this travels as the inner exception of the single
/// <see cref="System.IO.InvalidDataException"/> the reader promises: a caller maps "too big" to its message by
/// what was thrown, not by what the message says, and every existing catch of the outer type keeps working.
/// </summary>
public sealed class StreamBankLimitException(string message) : Exception(message)
{
    /// <summary>The single <see cref="System.IO.InvalidDataException"/> that carries a limit refusal.</summary>
    public static System.IO.InvalidDataException Wrap(string message) => new(message, new StreamBankLimitException(message));
}
