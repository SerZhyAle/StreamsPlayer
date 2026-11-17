using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>What a second process does with a verb when no first instance runs (<c>APP-COMMANDS</c> rule 10).</summary>
public enum AppCommandColdStart
{
    /// <summary>It becomes the first instance and the command is what it starts with.</summary>
    Start,

    /// <summary>It does the work itself and exits, holding no single-instance lock.</summary>
    Oneshot,

    /// <summary>Exit 11, <c>not-running</c>.</summary>
    Refuse
}

/// <summary>
/// SP-0271: the decisions both processes take from a command's manifest entry - the cold start of rule 10 and the
/// risk gate of rule 13 - kept here so they are made once and tested without a window.
/// </summary>
public static class AppCommandPolicy
{
    public const string SafeRisk = "safe";

    /// <summary>The declared cold start; a manifest that declares none does not load (<see cref="AppCommandManifest.Parse"/>).</summary>
    public static AppCommandColdStart ColdStartOf(AppCommandEntry entry) => entry.ColdStart switch
    {
        "start" => AppCommandColdStart.Start,
        "oneshot" => AppCommandColdStart.Oneshot,
        "refuse" => AppCommandColdStart.Refuse,
        _ => throw new ArgumentException($"'{entry.Name}' declares no cold start.", nameof(entry))
    };

    /// <summary>The completion bound of rule 8 for <paramref name="entry"/>.</summary>
    public static TimeSpan CompletionBound(AppCommandEntry entry) =>
        TimeSpan.FromMilliseconds(Math.Clamp(entry.TimeoutMs, 1, AppCommandLimits.MaximumTimeoutMs));

    /// <summary>
    /// Rule 13: a <c>safe</c> verb is always accepted; any other class is refused while the person's one setting is off.
    /// <see langword="null"/> means the verb may run. <paramref name="settingName"/> is the setting's name as the
    /// person sees it, so the refusal names it.
    /// </summary>
    public static AppCommandResponse? CheckRisk(string requestId, AppCommandEntry entry, bool restrictedAllowed, string settingName) =>
        string.Equals(entry.Risk, SafeRisk, StringComparison.Ordinal) || restrictedAllowed
            ? null
            : AppCommandResponse.Refused(requestId, AppCommandReason.SwitchOff, AppCommandExit.Permission,
                AppCommandMessages.SwitchOff(settingName));

    /// <summary>The answer of a <c>refuse</c> verb with nothing running.</summary>
    public static AppCommandResponse NotRunning(string requestId) =>
        AppCommandResponse.Refused(requestId, AppCommandReason.NotRunning, AppCommandExit.NotReady, AppCommandMessages.NotRunning);
}

/// <summary>
/// SP-0271: the sentences of the command line. English, the same in both processes: a second process cannot learn the
/// interface language without loading the state rule 6 forbids it to load. Never a path, an address or a secret in one.
/// </summary>
public static class AppCommandMessages
{
    public const string Product = "STREAMS Player";

    private const int MaximumEchoLength = 40;

    public static string UnknownVerb(string verb) =>
        $"{Product} has no command \"{Echo(verb)}\"; run \"StreamsPlayer.exe commands\" for the list.";

    public static string UnknownOption(string option, string verb) =>
        $"The command \"{verb}\" has no option --{Echo(option)}; run \"StreamsPlayer.exe commands\" for its options.";

    public static string MissingValue(string option) => $"The option --{option} needs a value.";

    public static string MissingOption(string option, string verb) => $"The command \"{verb}\" needs the option --{option}.";

    public static string BadValue(string option, string kind) =>
        $"The value of --{option} is not a valid {kind}.";

    public static string UnexpectedValue(string verb) =>
        $"The command \"{verb}\" takes no value without an option name; run \"StreamsPlayer.exe commands\" for its options.";

    public const string BadArgument = "The command line could not be read; run \"StreamsPlayer.exe commands\" for the list.";

    public const string TooLarge = "The command line is too long: at most 64 KiB in all and 32 KiB in one argument.";

    public const string Schema = "The running " + Product + " is older than this command; close it and start it again.";

    public const string OlderInstance =
        "The running " + Product + " is older than this command line; close it and start it again to use commands.";

    public const string NotRunning = Product + " is not running.";

    public const string NothingToQuit = Product + " is not running; there was nothing to close.";

    public const string Busy = Product + " is busy - a dialog is open or it is closing; try again when it is free.";

    public const string NotResponding = Product + " did not take the command in time; it may be busy.";

    public const string NoAnswer = "The running " + Product + " did not answer.";

    public const string Elevated =
        "The running " + Product + " was started as administrator; a command from a program that is not cannot reach it.";

    public const string Timeout = "No answer came in time; the running " + Product + " may still be working on it.";

    public const string StillClosing = Product + " has not finished closing yet; it may still be closing.";

    /// <summary>SP-0279, item I: an answer whose request id is empty or is not this command's.</summary>
    public const string UnreadableAnswer =
        "The running " + Product + " sent an answer that does not belong to this command; it may still be working on it.";

    /// <summary>SP-0279, item L: the answer of a verb would have been longer than 64 KiB.</summary>
    public const string ResponseTooLarge = Product + " could not send its answer: it is longer than 64 KiB.";

    public const string Failed = "The command failed inside " + Product + "; the log has the details.";

    public const string Shown = "The " + Product + " window is in front.";

    public const string Closing = Product + " is closing.";

    public const string Closed = Product + " has closed.";

    /// <summary>SP-0275: a command the player's own executable serves, typed into the console companion.</summary>
    public static string NotServedHere(string verb) =>
        $"StreamsPlayerCli.exe does not run \"{verb}\": it starts {Product} when nothing is running. Run \"StreamsPlayer.exe {verb}\" with the same options instead.";

    /// <summary>SP-0275: the console companion started with no command.</summary>
    public const string CompanionUsage =
        "Usage: StreamsPlayerCli.exe <command> [options]. Run \"StreamsPlayerCli.exe commands\" for the list.";

    /// <summary>SP-0275: the command list built into the program could not be read.</summary>
    public const string ManifestUnreadable = "The list of commands built into this program could not be read.";

    public static string SwitchOff(string settingName) =>
        $"This command is off: turn on \"{settingName}\" in {Product} settings, General, to allow it.";

    public static string Ready(string version) => $"{Product} {version} is running and ready.";

    public static string Version(string version, string cliVersion) =>
        string.Create(CultureInfo.InvariantCulture, $"{Product} {version}, command set {cliVersion}.");

    private static string Echo(string value)
    {
        var printable = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return printable.Length <= MaximumEchoLength ? printable : printable[..MaximumEchoLength] + "..";
    }
}
