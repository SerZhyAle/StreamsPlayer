using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>What a command line turned out to be (SP-0271).</summary>
public enum AppCommandParseKind
{
    /// <summary>A verb of the manifest with valid options.</summary>
    Verb,

    /// <summary>
    /// No argument at all: the frozen alias <c>""</c> (<c>APP-COMMANDS</c> rule 2), resolved to the verb that lists it.
    /// </summary>
    Alias,

    /// <summary>
    /// The first argument is not verb-shaped and the launch grammar cannot be read as <c>play</c> (it is over the
    /// bounds, or the manifest has no <c>play</c>): it is handed over as it always was (SP-0170).
    /// </summary>
    Launch,

    /// <summary>Refused before anything ran (rule 3, exit 10).</summary>
    Refused
}

/// <summary>The outcome of <see cref="AppCommandLine.Parse"/>.</summary>
public sealed record AppCommandParse(AppCommandParseKind Kind)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoOptions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The command, for <see cref="AppCommandParseKind.Verb"/> and <see cref="AppCommandParseKind.Alias"/>.</summary>
    public AppCommandEntry? Entry { get; init; }

    /// <summary>The arguments after the verb's words, exactly as typed - what a request carries in <c>args</c>.</summary>
    public IReadOnlyList<string> ArgumentsAfterVerb { get; init; } = [];

    /// <summary>Each option given, by its manifest name, with every value it was given (an option may repeat).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Options { get; init; } = NoOptions;

    /// <summary>
    /// SP-0272: the command line was one of the frozen launch spellings (a shortcut, a double-clicked <c>.spres</c>), read as
    /// <c>play</c>. The second process then keeps the hand-over those spellings always had when nothing answers its request.
    /// </summary>
    public bool FromLaunchSpelling { get; init; }

    /// <summary>The reason token of a refusal (<c>APP-COMMANDS</c> rule 12).</summary>
    public string? Reason { get; init; }

    /// <summary>One sentence that says what to change.</summary>
    public string? Message { get; init; }

    public bool Has(string option) => Options.ContainsKey(option);

    /// <summary>The last value given for <paramref name="option"/>, or <see langword="null"/>.</summary>
    public string? Value(string option) => Options.TryGetValue(option, out var values) && values.Count > 0 ? values[^1] : null;

    /// <summary>A refusal as a response, for the side that answers it.</summary>
    public AppCommandResponse ToRefusal(string requestId) =>
        AppCommandResponse.Refused(requestId, Reason ?? AppCommandReason.BadArgument, AppCommandExit.Usage,
            Message ?? AppCommandMessages.BadArgument);
}

/// <summary>
/// SP-0271: the one strict parser of <c>APP-COMMANDS</c> rules 1 and 3. The second process runs it to decide what to do
/// and the first instance runs it again on the forwarded words, so a command cannot read differently in the two.
/// </summary>
public static class AppCommandLine
{
    /// <summary>
    /// One or two words of <c>[a-z][a-z0-9-]*</c>. Compared without regard to case (rule 1), so the test accepts both.
    /// </summary>
    public static bool IsVerb(string value)
    {
        var words = value.Split(' ');
        return words.Length is 1 or 2 && words.All(IsVerbWord);
    }

    public static bool IsVerbWord(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    public static AppCommandParse Parse(IReadOnlyList<string> args, AppCommandManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(manifest);

        if (args.Count == 0)
        {
            return manifest.FindByAlias(string.Empty) is { } bare
                ? new AppCommandParse(AppCommandParseKind.Alias) { Entry = bare }
                : new AppCommandParse(AppCommandParseKind.Launch);
        }

        if (!IsVerbWord(args[0]))
        {
            return LaunchSpelling(args, manifest);
        }

        if (ExceedsBounds(args))
        {
            return Refuse(AppCommandReason.TooLarge, AppCommandMessages.TooLarge);
        }

        var entry = args.Count >= 2 && IsVerbWord(args[1]) ? manifest.Find(args[0] + " " + args[1]) : null;
        var words = 2;
        if (entry is null)
        {
            entry = manifest.Find(args[0]);
            words = 1;
        }

        if (entry is null)
        {
            return Refuse(AppCommandReason.UnknownVerb, AppCommandMessages.UnknownVerb(args[0]));
        }

        var rest = args.Skip(words).ToArray();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var endOfOptions = false;
        for (var index = 0; index < rest.Length; index++)
        {
            var token = rest[index];
            if (!endOfOptions && token == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (endOfOptions || !token.StartsWith("--", StringComparison.Ordinal))
            {
                return Refuse(AppCommandReason.BadArgument, AppCommandMessages.UnexpectedValue(entry.Name));
            }

            var equals = token.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? token[2..] : token[2..equals];
            var argument = entry.FindArgument(name);
            if (argument is null)
            {
                return Refuse(AppCommandReason.BadArgument, AppCommandMessages.UnknownOption(name, entry.Name));
            }

            string value;
            if (argument.Kind == "bool")
            {
                // `--muted false` and `--muted=false` both read; a bare `--muted` is true. Only the exact words
                // true and false are taken as the value, so a stray word after a flag is still refused.
                value = equals >= 0
                    ? token[(equals + 1)..].ToLowerInvariant()
                    : index + 1 < rest.Length && rest[index + 1] is "true" or "false" ? rest[++index] : "true";
                if (value is not ("true" or "false"))
                {
                    return Refuse(AppCommandReason.BadArgument, AppCommandMessages.BadValue(argument.Name, argument.Kind));
                }
            }
            else if (equals >= 0)
            {
                value = token[(equals + 1)..];
            }
            else if (index + 1 < rest.Length)
            {
                value = rest[++index];
            }
            else
            {
                return Refuse(AppCommandReason.BadArgument, AppCommandMessages.MissingValue(argument.Name));
            }

            if (!IsValid(argument, value))
            {
                return Refuse(AppCommandReason.BadArgument, AppCommandMessages.BadValue(argument.Name, argument.Kind));
            }

            if (!options.TryGetValue(argument.Name, out var values))
            {
                options[argument.Name] = values = [];
            }

            values.Add(value);
        }

        var missing = entry.Args.FirstOrDefault(argument => argument.Required && !options.ContainsKey(argument.Name));
        if (missing is not null)
        {
            return Refuse(AppCommandReason.BadArgument, AppCommandMessages.MissingOption(missing.Name, entry.Name));
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> parsed = options.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(), StringComparer.OrdinalIgnoreCase);
        if ((AppPlaybackCommands.Check(entry, parsed) ?? AppRecordCommands.Check(entry, parsed) ?? AppChannelCommands.Check(entry, parsed)
                ?? AppBroadcastCommands.Check(entry, parsed))
            is { } violation)
        {
            return Refuse(violation.Reason, violation.Message);
        }

        return new AppCommandParse(AppCommandParseKind.Verb) { Entry = entry, ArgumentsAfterVerb = rest, Options = parsed };
    }

    /// <summary>
    /// SP-0272: the frozen launch spellings are the aliases of <c>play</c> (rule 2), so they read as that verb with the
    /// same target they always meant. A spelling the shipped launch parser calls invalid is refused like any other
    /// unreadable command line (rule 3). The launch grammar keeps its own limits and notices (SP-0170): a launch over
    /// the bounds, or a manifest that has no <c>play</c>, takes the old path unchanged.
    /// </summary>
    private static AppCommandParse LaunchSpelling(IReadOnlyList<string> args, AppCommandManifest manifest)
    {
        if (manifest.Find(AppPlaybackCommands.PlayVerb) is not { } play || ExceedsBounds(args))
        {
            return new AppCommandParse(AppCommandParseKind.Launch);
        }

        var options = AppPlaybackCommands.ReadLaunchSpelling(args);
        return options is null
            ? Refuse(AppCommandReason.BadArgument, AppPlaybackMessages.LaunchSpellingInvalid)
            : new AppCommandParse(AppCommandParseKind.Verb)
            {
                Entry = play,
                ArgumentsAfterVerb = AppPlaybackCommands.CanonicalArguments(options),
                Options = options,
                FromLaunchSpelling = true
            };
    }

    private static bool ExceedsBounds(IReadOnlyList<string> args) =>
        args.Count > AppCommandLimits.MaximumArgumentCount ||
        args.Any(argument => AppCommandText.Utf8.GetByteCount(argument) > AppCommandLimits.MaximumArgumentBytes) ||
        args.Sum(argument => (long)AppCommandText.Utf8.GetByteCount(argument)) > AppCommandLimits.MaximumRequestBytes;

    /// <summary>Values are never compared without regard to case (rule 1); an enum value must match exactly.</summary>
    private static bool IsValid(AppCommandArgument argument, string value) => argument.Kind switch
    {
        "int" => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        "guid" => Guid.TryParse(value, out _),
        "enum" => argument.Values.Contains(value, StringComparer.Ordinal),
        "bool" => value is "true" or "false",
        _ => value.Length > 0
    };

    private static AppCommandParse Refuse(string reason, string message) =>
        new(AppCommandParseKind.Refused) { Reason = reason, Message = message };
}
