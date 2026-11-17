using System.Text;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0271, <c>APP-COMMANDS</c> rule 9, moved to Core by SP-0275: what a second process says about an answer and the
/// <c>--result</c> file it writes. Both executables - the player's own and its console companion - say it the same way; they
/// differ only in where a line goes, which each passes in.
/// </summary>
public static class AppCommandOutput
{
    /// <summary>The complaint when the <c>--result</c> file could not be written (exit <see cref="AppCommandExit.Io"/>).</summary>
    public const string ResultNotWritten = "The --result file could not be written; nothing was written there.";

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Rule 9, for every process that answers a command: the message, or the response line with <c>--json</c>, through
    /// <paramref name="line"/>; <c>--result</c> holds the data of a finished command, whole or not at all. Returns the exit
    /// code the caller gets - the command's own, or <see cref="AppCommandExit.Io"/> when the result file could not be written.
    /// </summary>
    public static int Report(
        AppCommandResponse response, bool json, string? resultPath, Action<string> line, Action<string> complaint)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(complaint);

        line(json ? response.ToJsonLine() : response.Message);
        if (resultPath is not null
            && (response.Status is AppCommandStatus.Done or AppCommandStatus.Accepted)
            && !TryWriteResult(resultPath, response.Data))
        {
            complaint(ResultNotWritten);
            return AppCommandExit.Io;
        }

        return response.ExitCode;
    }

    /// <summary>
    /// Writes <paramref name="data"/> as one UTF-8 JSON file at <paramref name="path"/>, whole or not at all: a temporary
    /// file beside the target, renamed over it. A relative path is the caller's, resolved against its working directory.
    /// </summary>
    public static bool TryWriteResult(string path, JsonObject? data)
    {
        string? temporary = null;
        try
        {
            var target = Path.GetFullPath(path, Environment.CurrentDirectory);
            temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temporary, (data ?? new JsonObject()).ToJsonString(), Utf8);
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // The temporary file is left behind; the target was never touched.
                }
            }

            return false;
        }
    }
}
