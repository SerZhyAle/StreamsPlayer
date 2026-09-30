namespace StreamsPlayer.Core;

/// <summary>
/// The log fields that describe a contained fault (SP-0166): its type, its own text and the frame it came from.
/// One place, so every containment point writes the same three fields and none of them builds a line by hand.
/// </summary>
/// <remarks>
/// The exception's own text is for the log only; nothing built here is shown to the user.
/// </remarks>
public static class FaultLogFields
{
    /// <summary>Longest message kept, so a runaway native error string cannot flood the log line.</summary>
    public const int MaximumMessageLength = 200;

    public static string[] Of(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return [$"type={exception.GetType().Name}", $"err={Clean(exception.Message)}", $"at={Origin(exception)}"];
    }

    private static string Origin(Exception exception)
    {
        var first = exception.StackTrace?.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(first) ? "none" : Clean(first);
    }

    // One event is one line: a line break inside a value would start a fake entry.
    private static string Clean(string text)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= MaximumMessageLength ? flat : flat[..MaximumMessageLength];
    }
}
