using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed record ExchangeImportResult(ExchangeConnectionString? Data, string? MessageKey);

internal static class ExchangeConnectionImport
{
    internal static bool Recognizes(string text) => text.TrimStart('\uFEFF').TrimStart()
        .StartsWith("FMSEXCH", StringComparison.Ordinal);

    internal static ExchangeImportResult ReadText(string text) => Convert(ExchangeConnectionStringCodec.Parse(text));

    /// <summary>
    /// A QR carries the connection string byte for byte (DEVICE-EXCHANGE 5.5), so what it holds goes through the
    /// same codec as pasted text. A picture with no code, with several, or that cannot be read each has its one
    /// message; a code that holds anything else is refused with the codec's.
    /// </summary>
    internal static ExchangeImportResult ReadQr(ExchangeQrOutcome outcome) => outcome.Kind switch
    {
        ExchangeQrOutcomeKind.Found => ReadText(outcome.Text ?? ""),
        ExchangeQrOutcomeKind.None => new(null, "ExchangeQrNone"),
        ExchangeQrOutcomeKind.Several => new(null, "ExchangeQrSeveral"),
        _ => new(null, "ExchangeQrUnreadable")
    };

    internal static async Task<ExchangeImportResult> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length > ExchangeConnectionStringCodec.MaximumFileBytes)
                return new(null, "ExchangeStringInvalid");
            return Convert(await ExchangeConnectionStringCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // File paths and reader exceptions are private input, not diagnostic events.
            return new(null, "ExchangeStringInvalid");
        }
    }

    // A later FMSEXCH major gets the family's `format-unsupported` row (amendment of 2026-10-17): nothing was read,
    // and no update is demanded - release 46 has no minimum version (item CK).
    private static ExchangeImportResult Convert(ExchangeConnectionStringResult result) => new(result.Data,
        result.Success ? null : result.Reason == "newer-major"
            ? ExchangeFailureRows.FormatUnsupported.MessageKey : "ExchangeStringInvalid");
}
