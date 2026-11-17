using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0275, <c>APP-COMMANDS</c> rule 9: the console companion's whole behaviour. A console-subsystem executable runs a
/// command, prints its answer on standard output and exits with the answer's code, so a caller that pipes (PowerShell, a CI
/// step, a development agent) can read both.
/// </summary>
/// <remarks>
/// <para>
/// The companion serves exactly the commands whose manifest entry names it in <c>exe</c> - the ones that need no window of
/// their own: a verb whose cold start is <c>start</c> must become the first instance, which a console process cannot, so the
/// manifest leaves it with the player's executable and the companion refuses it with a sentence that says so. Everything else
/// is the player's own second process: <c>commands</c> and <c>version</c> are answered from the embedded manifest with no
/// first instance at all, and any other command is carried to the running copy over the frozen pipe, or answered by its
/// cold start when nothing runs (<see cref="AppCommandCaller.Run"/>).
/// </para>
/// <para>
/// It lives in Core so the in-process listener tests of the profile run its whole path; the executable is only a
/// <c>Main</c> that passes the process's own identity, version and standard streams.
/// </para>
/// </remarks>
public static class AppCommandConsole
{
    /// <summary>The executable's file name, as the manifest's <c>exe</c> and the package's execution alias spell it.</summary>
    public const string ExecutableName = "StreamsPlayerCli.exe";

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Whether the manifest hands <paramref name="entry"/> to the companion.</summary>
    public static bool Serves(AppCommandManifest manifest, AppCommandEntry entry)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entry);
        return string.Equals(manifest.ExeOf(entry), ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs one command line and returns the process's exit code. <paramref name="identity"/> names the lock and the pipe of
    /// the copy to talk to, <paramref name="version"/> is this build's stamp, <paramref name="stdout"/> receives the answer
    /// and <paramref name="stderr"/> the complaints. Never throws for a command that cannot be run.
    /// </summary>
    public static int Run(
        IReadOnlyList<string> args, SingleInstanceIdentity identity, string version, Stream stdout, Stream stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        AppCommandManifest manifest;
        try
        {
            manifest = AppCommandManifest.Embedded;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            Write(stderr, AppCommandMessages.ManifestUnreadable + Environment.NewLine);
            return AppCommandExit.Failed;
        }

        var requestId = AppCommandCaller.NewRequestId();
        var parse = AppCommandLine.Parse(args, manifest);
        var json = parse.Kind == AppCommandParseKind.Verb
            ? parse.Has("json")
            : args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        string? resultPath = parse.Kind == AppCommandParseKind.Verb ? parse.Value("result") : null;

        int Report(AppCommandResponse response) => AppCommandOutput.Report(
            response, json, resultPath, text => Write(stdout, text + Environment.NewLine), text => Write(stderr, text + Environment.NewLine));

        switch (parse.Kind)
        {
            case AppCommandParseKind.Refused:
                return Report(parse.ToRefusal(requestId));
            case AppCommandParseKind.Alias:
            case AppCommandParseKind.Launch:
                // No command at all, or a launch spelling the profile cannot read: this program starts nothing.
                return Report(AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                    AppCommandMessages.CompanionUsage));
        }

        var entry = parse.Entry!;
        if (!Serves(manifest, entry))
        {
            return Report(AppCommandResponse.Refused(requestId, AppCommandReason.UnknownVerb, AppCommandExit.Usage,
                AppCommandMessages.NotServedHere(entry.Name)));
        }

        switch (entry.Name)
        {
            case "commands":
                if (json)
                {
                    Write(stdout, AppCommandManifest.EmbeddedBytes.Span);
                }
                else
                {
                    Write(stdout, AppCommandCaller.CommandList(manifest, ExecutableName) + Environment.NewLine);
                }

                return AppCommandExit.Done;
            case "version":
                return Report(AppCommandCaller.VersionResponse(requestId, manifest, version));
        }

        return Report(AppCommandCaller.Run(entry, parse, identity, requestId));
    }

    private static void Write(Stream stream, string text) => Write(stream, Utf8.GetBytes(text));

    private static void Write(Stream stream, ReadOnlySpan<byte> bytes)
    {
        try
        {
            stream.Write(bytes);
            stream.Flush();
        }
        catch (IOException)
        {
            // The caller closed its end of the pipe; the exit code still says what happened.
        }
    }
}
