using System.Globalization;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>SP-0272: the <c>state</c> of a stream in <c>playback status</c> and in the answer of <c>play</c>.</summary>
public static class AppPlaybackState
{
    public const string Playing = "playing";
    public const string Paused = "paused";
    public const string Buffering = "buffering";
    public const string Failed = "failed";
}

/// <summary>
/// SP-0272: one stream as <c>playback status</c> reports it. It carries no address: the title is the person's own text
/// and is redacted again on its way into <see cref="ToJson"/>, because a title may itself be a pasted address.
/// </summary>
public sealed record AppPlaybackSession(string Kind, Guid ChannelId, string Title, string State, bool Recording)
{
    public const string AudioKind = "audio";
    public const string VideoKind = "video";

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["channelId"] = ChannelId.ToString("D"),
        ["title"] = AppPlaybackCommands.SafeTitle(Title),
        ["state"] = State,
        ["recording"] = Recording
    };
}

/// <summary>The streams a <c>playback stop</c> acts on, or the refusal when the request does not name them.</summary>
public sealed record AppStopPlan(IReadOnlyList<AppPlaybackSession> Targets, AppCommandResponse? Refusal);

/// <summary>
/// SP-0272: the sentences of the playback verbs. English, one sentence each, never an address, a path or a title -
/// the same rule <see cref="AppCommandMessages"/> keeps.
/// </summary>
public static class AppPlaybackMessages
{
    public const string PlayNeedsTarget =
        "The command \"play\" needs a target: --id <channel id>, --url <address>, --url-file <path> or --file <path.spres>.";

    public const string PlayOneTarget =
        "The command \"play\" takes one target: --id (with --url or --url-file as its fallback), --url, --url-file, or --file - " +
        "not a mix, and each only once.";

    public const string PlayAddressNotLaunchable = "The address of --url must start with http://, https:// or rtsp://.";

    /// <summary>SP-0279, item H: the address read from the file of <c>--url-file</c> is not a stream address.</summary>
    public const string PlayFileAddressNotLaunchable =
        "The address in the file of --url-file must start with http://, https:// or rtsp://.";

    public const string PlayFileNotSpres = "The file of --file must be a .spres document.";

    public const string LaunchSpellingInvalid =
        "These arguments do not start a stream; use --url <address>, --id <channel id> [--url <address>] or a .spres file, " +
        "or run \"StreamsPlayer.exe commands\" for the commands.";

    public const string FileUnreadable = "The .spres document could not be read; check the path of --file.";

    public const string FileInvalid = "The .spres document is not a valid StreamsPlayer broadcast resource.";

    public const string NeedsPassword =
        "This broadcast needs a link password and none is stored on this PC; open it once in the window and enter it.";

    public const string ChannelNotFound = "No saved channel has this id, and no --url was given to play instead.";

    public const string StreamFailed = "The stream did not open; the log has the details.";

    public const string HostUnreachable = "The stream's host could not be reached.";

    public const string NoSoundInTime = "No sound or picture came in time; the app may still be trying to open the stream.";

    public const string StopChoose =
        "Several streams are playing; add --id <channel id> to stop one or --all to stop every one.";

    public const string StopEither = "Give either --id or --all, not both.";

    public const string NothingPlaying = "Nothing is playing.";

    public const string NoRadio = "No radio is playing; video windows have no pause.";

    public const string NoRadioToResume = "There is no paused radio to resume.";

    public const string VolumeLevelRange = "The value of --level must be a whole number from 0 to 100.";

    public const string VolumeMutedVideoOnly = "--muted applies to the video players only; add --target video.";

    public static string Playing(string kind) => $"The {kind} stream is playing.";

    public static string Stopped(int count) =>
        count == 0 ? NothingPlaying : string.Create(CultureInfo.InvariantCulture, $"Stopped {count} stream(s).");

    public static string Listed(int count) =>
        count == 0 ? NothingPlaying : string.Create(CultureInfo.InvariantCulture, $"{count} stream(s) are open.");

    public static string VolumeSet(int level) => string.Create(CultureInfo.InvariantCulture, $"The volume is {level}.");

    public const string Paused = "The radio is paused.";

    public const string AlreadyPaused = "The radio was already paused.";

    public const string WasNotPaused = "The radio was not paused.";
}

/// <summary>
/// SP-0272: the playback command group's decisions that need no window - what a spelling means, which options go
/// together, which streams a stop acts on, how a failed open is told. The App executes; this decides, so the rules are
/// tested without a player.
/// </summary>
public static class AppPlaybackCommands
{
    public const string PlayVerb = "play";
    public const string StopVerb = "playback stop";
    public const string PauseVerb = "playback pause";
    public const string ResumeVerb = "playback resume";
    public const string StatusVerb = "playback status";
    public const string VolumeVerb = "volume set";

    private const int MaximumTitleLength = 200;

    /// <summary>A refusal of the options of a verb that the manifest alone cannot express.</summary>
    public readonly record struct Violation(string Reason, string Message);

    /// <summary>
    /// What the frozen launch spellings (<c>--url A</c>, <c>--id G</c>, <c>--id G --url A</c>, a lone <c>X.spres</c>) mean
    /// as the options of <c>play</c> - read by <see cref="StreamLaunchRequest.Parse"/>, the shipped parser, so the same
    /// text gives the same target as before. <see langword="null"/> for a spelling that parser calls invalid.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadLaunchSpelling(IReadOnlyList<string> args)
    {
        var launch = StreamLaunchRequest.Parse(args);
        var options = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        switch (launch.Kind)
        {
            case StreamLaunchTargetKind.Url when launch.Url is { } url:
                options["url"] = [url];
                return options;
            case StreamLaunchTargetKind.ChannelId when launch.ChannelId is { } id:
                options["id"] = [id.ToString("D")];
                if (launch.Url is { } fallback)
                {
                    options["url"] = [fallback];
                }

                return options;
            case StreamLaunchTargetKind.File when launch.File is { } file:
                options["file"] = [file];
                return options;
            default:
                return null;
        }
    }

    /// <summary>The options of <c>play</c> written as arguments after the verb - what a request carries in <c>args</c>.</summary>
    public static IReadOnlyList<string> CanonicalArguments(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        var arguments = new List<string>();
        foreach (var name in new[] { "file", "id", "url" })
        {
            if (options.TryGetValue(name, out var values) && values.Count > 0)
            {
                arguments.Add("--" + name);
                arguments.Add(values[^1]);
            }
        }

        return arguments;
    }

    /// <summary>
    /// The target of a <c>play</c> as the shipped launch grammar names it. A relative <c>--file</c> is resolved against
    /// the caller's working directory (rule 7), because the process that opens it is not the caller.
    /// </summary>
    public static StreamLaunchRequest ToLaunchRequest(
        IReadOnlyDictionary<string, IReadOnlyList<string>> options, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Last(options, "file") is { } file)
        {
            return new StreamLaunchRequest(StreamLaunchTargetKind.File, File: ResolvePath(file.Trim(), workingDirectory));
        }

        var url = Last(options, "url")?.Trim();
        return Last(options, "id") is { } id && Guid.TryParse(id.Trim(), out var channelId)
            ? new StreamLaunchRequest(StreamLaunchTargetKind.ChannelId, Url: url, ChannelId: channelId)
            : new StreamLaunchRequest(StreamLaunchTargetKind.Url, Url: url);
    }

    /// <summary>
    /// The same target as the launch line an older running copy understands: used when this process becomes the first
    /// instance with <c>play</c> as what it starts with, and when the first instance predates the command profile.
    /// </summary>
    public static IReadOnlyList<string> LaunchArguments(
        IReadOnlyDictionary<string, IReadOnlyList<string>> options, string? workingDirectory)
    {
        var launch = ToLaunchRequest(options, workingDirectory);
        return launch.Kind switch
        {
            StreamLaunchTargetKind.File => [launch.File!],
            StreamLaunchTargetKind.ChannelId when launch.Url is { } url => ["--id", launch.ChannelId!.Value.ToString("D"), "--url", url],
            StreamLaunchTargetKind.ChannelId => ["--id", launch.ChannelId!.Value.ToString("D")],
            _ => ["--url", launch.Url!]
        };
    }

    /// <summary>
    /// The rules a verb has beyond what its manifest entry declares (rule 3). <see langword="null"/> when the options are
    /// acceptable. Run by the one parser, so the second process and the first instance judge them alike.
    /// </summary>
    public static Violation? Check(AppCommandEntry entry, IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        entry.Name switch
        {
            PlayVerb => CheckPlay(options),
            StopVerb => CheckStop(options),
            VolumeVerb => CheckVolume(options),
            _ => null
        };

    /// <summary>The streams <c>playback stop</c> acts on (rule 4: more than one and no choice is refused, never guessed).</summary>
    public static AppStopPlan PlanStop(
        string requestId, Guid? channelId, bool all, IReadOnlyList<AppPlaybackSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (all)
        {
            return new AppStopPlan(sessions, null);
        }

        if (channelId is { } id)
        {
            return new AppStopPlan(sessions.Where(session => session.ChannelId == id).ToArray(), null);
        }

        return sessions.Count > 1
            ? new AppStopPlan([], AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppPlaybackMessages.StopChoose))
            : new AppStopPlan(sessions, null);
    }

    /// <summary>The answer when a stream that was asked for ended in failure; 15 when the network or the host is the cause.</summary>
    public static AppCommandResponse OpenFailed(string requestId, PlaybackReachability reachability)
    {
        var network = reachability is PlaybackReachability.NetworkUnreachable
            or PlaybackReachability.NameNotResolved
            or PlaybackReachability.ChannelUnreachable;
        return AppCommandResponse.Failed(requestId, AppCommandReason.StreamFailed,
            network ? AppCommandExit.Network : AppCommandExit.Failed,
            network ? AppPlaybackMessages.HostUnreachable : AppPlaybackMessages.StreamFailed);
    }

    /// <summary>The answer of a <c>play</c> or <c>playback resume</c> whose stream plays.</summary>
    public static AppCommandResponse Playing(string requestId, AppPlaybackSession session) =>
        AppCommandResponse.Done(requestId, AppPlaybackMessages.Playing(session.Kind), new JsonObject
        {
            ["channelId"] = session.ChannelId.ToString("D"),
            ["title"] = SafeTitle(session.Title),
            ["kind"] = session.Kind,
            ["state"] = session.State
        });

    /// <summary>The <c>data</c> of <c>playback status</c>.</summary>
    public static JsonObject SessionsData(IEnumerable<AppPlaybackSession> sessions)
    {
        var list = new JsonArray();
        foreach (var session in sessions)
        {
            list.Add(session.ToJson());
        }

        return new JsonObject { ["sessions"] = list };
    }

    /// <summary>
    /// A title for a response: control characters dropped, an address inside it redacted (credentials, a broadcast
    /// capability), the length bounded. A saved channel's title is the person's text and may be a pasted address.
    /// </summary>
    public static string SafeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var printable = new string(title.Where(character => !char.IsControl(character)).ToArray()).Trim();
        var redacted = ExchangeDiagnosticRedactor.RedactCapabilityAddresses(CatalogUrlIdentity.RedactText(printable));
        return redacted.Length <= MaximumTitleLength ? redacted : redacted[..MaximumTitleLength] + "..";
    }

    private static Violation? CheckPlay(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        var ids = Count(options, "id");
        var urls = Count(options, "url");
        var urlFiles = Count(options, AppUrlFile.OptionName);
        var files = Count(options, "file");
        if (ids + urls + urlFiles + files == 0)
        {
            return Bad(AppPlaybackMessages.PlayNeedsTarget);
        }

        // SP-0279, item H: --url-file is the same address given another way, so it is never combined with --url.
        if (ids > 1 || urls > 1 || urlFiles > 1 || files > 1 || (urls + urlFiles > 1) || (files == 1 && ids + urls + urlFiles > 0))
        {
            return Bad(AppPlaybackMessages.PlayOneTarget);
        }

        if (urls == 1 && !StreamMediaKindClassifier.IsLaunchable(Last(options, "url")!.Trim()))
        {
            return Bad(AppPlaybackMessages.PlayAddressNotLaunchable);
        }

        return files == 1 && !Last(options, "file")!.Trim().EndsWith(".spres", StringComparison.OrdinalIgnoreCase)
            ? Bad(AppPlaybackMessages.PlayFileNotSpres)
            : null;
    }

    private static Violation? CheckStop(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        if (IsTrue(options, "all") && options.ContainsKey("id"))
        {
            return Bad(AppPlaybackMessages.StopEither);
        }

        return null;
    }

    private static Violation? CheckVolume(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        if (Last(options, "level") is not { } level ||
            !int.TryParse(level, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ||
            number is < 0 or > 100)
        {
            return Bad(AppPlaybackMessages.VolumeLevelRange);
        }

        return options.ContainsKey("muted") && (Last(options, "target") ?? "audio") != "video"
            ? Bad(AppPlaybackMessages.VolumeMutedVideoOnly)
            : null;
    }

    /// <summary>Reads a bool option the way the parser keeps it: present with <c>true</c> unless it was given <c>false</c>.</summary>
    public static bool IsTrue(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        Last(options, name) is { } value && string.Equals(value, "true", StringComparison.Ordinal);

    public static string? Last(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        options.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;

    private static int Count(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values.Count : 0;

    private static Violation Bad(string message) => new(AppCommandReason.BadArgument, message);

    /// <summary>A relative path taken from the caller's working folder, as rule 7 asks; a path that cannot be combined is kept as given.</summary>
    internal static string ResolvePath(string path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || Path.IsPathRooted(path) || !Path.IsPathRooted(workingDirectory))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
