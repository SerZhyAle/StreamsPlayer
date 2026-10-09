namespace StreamsPlayer.Core;

/// <summary>
/// SP-0127: the command-line arguments a desktop shortcut or a copied launch command carries for a saved
/// channel, and how a parsed launch finds its channel again.
/// </summary>
/// <remarks>
/// <para>The channel's id alone is not durable. A refresh deletes a catalog row the bank stops listing
/// unless the user authored something on it, and a returning address gets a new id - so a shortcut to an
/// unpinned channel broke for good the first time the bank dropped it. The address rides beside the id,
/// and a launch whose id is gone falls back to it: no state has to be kept in step with files the user
/// may delete from the desktop, and an address was already a supported launch argument.</para>
/// <para>The address is left out, never quoted around, when it could not survive the command line
/// intact: a quote or whitespace would split or end the argument, and a backslash before the closing
/// quote would escape it, or when the log redactor would strip credentials from it. It is also left
/// out when it would push the arguments past what a shortcut can store. Such a channel launches by id
/// alone, exactly as every shortcut did before.</para>
/// </remarks>
public static class StreamLaunchArguments
{
    /// <summary>
    /// A shell link stores at most <c>INFOTIPSIZE</c> (1024) characters of arguments; the margin keeps the
    /// terminating NUL and any rounding in the shell's own accounting out of the question.
    /// </summary>
    public const int MaximumLength = 1000;

    /// <summary>The argument string for <paramref name="channel"/>, ready for a shortcut or a command line.</summary>
    public static string For(StreamChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var idOnly = $"--id \"{channel.Id:D}\"";
        if (!CarriesAddress(channel))
        {
            return idOnly;
        }

        return $"{idOnly} --url \"{channel.Url.Trim()}\"";
    }

    /// <summary>
    /// SP-0184: the line to paste into PowerShell, where a quoted path alone is an expression rather than a
    /// command and needs the call operator. The path is single-quoted so a <c>$</c> or a backtick in a user-profile
    /// folder name is not expanded; the arguments need no escaping because <see cref="CanCarry"/> already keeps
    /// every character PowerShell treats specially out of the address. PowerShell reads four typographic marks
    /// besides <c>'</c> as a single quote, so each of them is doubled in the path as well.
    /// </summary>
    public static string ForPowerShell(string executablePath, StreamChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var quoted = new System.Text.StringBuilder(executablePath.Length + 8);
        foreach (var character in executablePath)
        {
            quoted.Append(character);
            if (IsPowerShellSingleQuote(character))
            {
                quoted.Append(character);
            }
        }

        return $"& '{quoted}' {For(channel)}";
    }

    /// <summary>
    /// The characters PowerShell's tokenizer ends a single-quoted string on: the ASCII apostrophe and the
    /// four typographic marks (left, right, low-9 and high-reversed-9).
    /// </summary>
    private static bool IsPowerShellSingleQuote(char character) =>
        character is '\'' or '‘' or '’' or '‚' or '‛';

    /// <summary>Whether this channel's generated arguments contain its address as a fallback.</summary>
    public static bool CarriesAddress(StreamChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return CanCarry(channel.Url) &&
            $"--id \"{channel.Id:D}\" --url \"{channel.Url.Trim()}\"".Length <= MaximumLength;
    }

    /// <summary>
    /// Whether an existing shortcut's <paramref name="arguments"/> start <paramref name="channelId"/> - the
    /// test that lets a shortcut be rewritten for its own channel but never taken over by another one.
    /// </summary>
    public static bool Names(string? arguments, Guid channelId) =>
        !string.IsNullOrEmpty(arguments) &&
        arguments.Contains(channelId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The saved channel a <see cref="StreamLaunchTargetKind.ChannelId"/> launch names: the row with that
    /// id, otherwise the row at the launch's fallback address, otherwise <see langword="null"/>. The caller
    /// decides what an unresolved launch with an address does - it can still play that address.
    /// </summary>
    public static StreamChannel? Resolve(IEnumerable<StreamChannel> channels, StreamLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != StreamLaunchTargetKind.ChannelId)
        {
            return null;
        }

        var list = channels as IReadOnlyCollection<StreamChannel> ?? channels.ToList();
        var byId = list.FirstOrDefault(channel => channel.Id == request.ChannelId);
        if (byId is not null || string.IsNullOrWhiteSpace(request.Url))
        {
            return byId;
        }

        var address = CatalogUrlIdentity.Normalize(request.Url);
        var matches = list.Where(channel => string.Equals(CatalogUrlIdentity.Normalize(channel.Url), address, StringComparison.Ordinal));

        // A retired row keeps the user's pin and collections, but a live one is what the bank offers now.
        return matches.FirstOrDefault(channel => channel.RetiredAt is null) ?? matches.FirstOrDefault();
    }

    private static bool CanCarry(string? url) =>
        LaunchableAddress.IsLaunchable(url) &&
        !CatalogUrlIdentity.HasCredentials(url!) &&
        // SP-0201 requirement 5: a relay or tunnel address is the right to listen to that broadcast, and a
        // shortcut file or a copied command is exactly where it must not be written.
        !ExchangeDiagnosticRedactor.ContainsBroadcastPath(url!) &&
        !url!.Trim().Any(character => character is '"' or '\'' or '<' or '>' or '|' or '\\' or '$' or '`' ||
            IsTypographicQuote(character) || char.IsWhiteSpace(character) || char.IsControl(character)) &&
        // Release audit 26.1001.0140: the copied launch command is pasted into a shell. PowerShell expands $(..)
        // and $var inside double quotes, and cmd expands %NAME% inside them, so such an address would run or
        // change. The channel then falls back to its id, which needs nothing quoted.
        !ContainsCmdVariable(url!);

    /// <summary>
    /// Release audit 26.1010.0106 (A1): PowerShell reads U+2018..U+201B as single quotes and U+201C..U+201E as
    /// double quotes, so a typographic mark ends the quoted <c>--url</c> argument exactly as an ASCII quote
    /// does and what follows runs as a command. The whole General Punctuation quotation block is refused
    /// (U+201F is no PowerShell quote, but no stream address needs it either). Inside the double quotes
    /// nothing else is special to PowerShell: <c>&amp; ; ( ) { } # @ ,</c> are literal there, and cmd keeps
    /// <c>^ &amp; |</c> literal inside quotes too.
    /// </summary>
    private static bool IsTypographicQuote(char character) =>
        character is >= '‘' and <= '‟';

    /// <summary>
    /// A candidate <c>%NAME%</c>. The closing percent is only looked ahead at, not consumed, so two tokens
    /// that share a percent sign are both examined. Everything up to the closing percent belongs to the
    /// token: cmd also expands <c>%NAME:~0,5%</c> and <c>%NAME:a=b%</c>, which would put part of the user's
    /// environment into the address.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CmdVariableToken = new(
        @"%[A-Za-z_][^%\s]*(?=%)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// SP-0184 (A16-2): whether the address holds a <c>%NAME%</c> that cmd would expand. A percent-encoded
    /// path (<c>caf%C3%A9</c>, <c>%C3%A9t%C3%A9</c>) also reads as <c>%NAME%</c> to the pattern, but there both
    /// percent signs begin a valid <c>%HH</c> escape, and an address that merely contains non-ASCII text lost
    /// its shortcut fallback for it. A token whose opening and closing percent each start a hex pair is an
    /// escape sequence; <c>%DATE%</c> and <c>%CD%</c> followed by anything else still count as variables.
    /// </summary>
    private static bool ContainsCmdVariable(string url)
    {
        foreach (System.Text.RegularExpressions.Match token in CmdVariableToken.Matches(url))
        {
            var closing = token.Index + token.Length;
            // %CD% is the one cmd variable whose name is also a hex pair; it expands to the working directory.
            if (token.ValueSpan.Equals("%CD", StringComparison.OrdinalIgnoreCase) ||
                !(StartsHexPair(url, token.Index + 1) && StartsHexPair(url, closing + 1)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsHexPair(string text, int index) =>
        index + 1 < text.Length && char.IsAsciiHexDigit(text[index]) && char.IsAsciiHexDigit(text[index + 1]);
}
