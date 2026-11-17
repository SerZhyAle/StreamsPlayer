namespace StreamsPlayer.Core;

/// <summary>
/// SP-0279: what <see cref="AppUrlFile"/> made of a command's options - the options to run with, or the answer that ends the
/// command. Exactly one of the two is meaningful: <see cref="Refusal"/> is <see langword="null"/> when the options can be used.
/// </summary>
public sealed record AppUrlFileResult(IReadOnlyDictionary<string, IReadOnlyList<string>> Options, AppCommandResponse? Refusal);

/// <summary>
/// SP-0279, <c>APP-COMMANDS</c> item H: the sentences of <c>--url-file</c>. English, never the path or the content of the file -
/// the address may carry a credential and the path is the caller's.
/// </summary>
public static class AppUrlFileMessages
{
    public const string Missing = "The file of --url-file does not exist.";

    public const string Unreadable = "The file of --url-file could not be read.";

    public const string Empty = "The file of --url-file holds no address; put the stream address on its first line.";
}

/// <summary>
/// SP-0279, <c>APP-COMMANDS</c> item H: an address that may carry a credential (<c>rtsp://user:password@host/stream</c>) is
/// marked <c>sensitive</c> in the manifest, and the verb also takes it from a file the caller names - <c>--url-file</c>, one
/// UTF-8 line - for the caller who does not want it in the command line and the shell history.
/// </summary>
/// <remarks>
/// The first instance reads the file (a relative path from the caller's working folder, rule 7), off the UI thread, and the
/// verb then runs with the address as if <c>--url</c> had carried it. A second process that becomes the first instance with
/// <c>play</c> reads it before it starts. The content never reaches a log line, an answer or a message; a problem is told in
/// words that name the option and not the file.
/// </remarks>
public static class AppUrlFile
{
    /// <summary>The name of the option, without its dashes.</summary>
    public const string OptionName = "url-file";

    /// <summary>
    /// The options to run <paramref name="requestId"/>'s command with: unchanged when no <c>--url-file</c> was given, else with
    /// the address of the file's first line as <c>url</c> and <c>url-file</c> removed. <paramref name="notLaunchableMessage"/>
    /// is the verb's own sentence for an address that is not http, https or rtsp. Never throws for a file problem.
    /// </summary>
    public static AppUrlFileResult Resolve(
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options,
        string? workingDirectory,
        string notLaunchableMessage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(notLaunchableMessage);

        if (AppPlaybackCommands.Last(options, OptionName) is not { } given)
        {
            return new AppUrlFileResult(options, null);
        }

        var path = given.Trim();
        if (path.Length == 0)
        {
            return Refused(options, requestId, AppUrlFileMessages.Empty);
        }

        string address;
        try
        {
            address = ReadFirstLine(AppPlaybackCommands.ResolvePath(path, workingDirectory));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed(options, AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady,
                AppUrlFileMessages.Missing));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Security.SecurityException)
        {
            return Failed(options, AppCommandResponse.Failed(requestId, AppCommandReason.IoError, AppCommandExit.Io,
                AppUrlFileMessages.Unreadable));
        }

        if (address.Length == 0)
        {
            return Refused(options, requestId, AppUrlFileMessages.Empty);
        }

        if (!StreamMediaKindClassifier.IsLaunchable(address))
        {
            return Refused(options, requestId, notLaunchableMessage);
        }

        var resolved = new Dictionary<string, IReadOnlyList<string>>(
            options.Where(pair => !string.Equals(pair.Key, OptionName, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase)
        {
            ["url"] = [address]
        };
        return new AppUrlFileResult(resolved, null);
    }

    /// <summary>
    /// <see cref="Resolve"/> for the first instance: the file is read on a pool thread, and a command with no
    /// <c>--url-file</c> completes at once, on the caller's own thread.
    /// </summary>
    public static Task<AppUrlFileResult> ResolveAsync(
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options,
        string? workingDirectory,
        string notLaunchableMessage) =>
        AppPlaybackCommands.Last(options, OptionName) is null
            ? Task.FromResult(new AppUrlFileResult(options, null))
            : Task.Run(() => Resolve(requestId, options, workingDirectory, notLaunchableMessage));

    /// <summary>The first line of the file, trimmed; empty for an empty file or one over the bound of one argument.</summary>
    private static string ReadFirstLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[AppCommandLimits.MaximumArgumentBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > AppCommandLimits.MaximumArgumentBytes)
        {
            return string.Empty;
        }

        var text = AppCommandText.Utf8.GetString(buffer, 0, length).TrimStart('\uFEFF');
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static AppUrlFileResult Refused(
        IReadOnlyDictionary<string, IReadOnlyList<string>> options, string requestId, string message) =>
        Failed(options, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage, message));

    private static AppUrlFileResult Failed(IReadOnlyDictionary<string, IReadOnlyList<string>> options, AppCommandResponse answer) =>
        new(options, answer);
}
