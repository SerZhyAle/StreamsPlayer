namespace StreamsPlayer.Core;

/// <summary>
/// SP-0230: turns the command template a browser registers for opening an address into a program and its arguments.
/// </summary>
/// <remarks>
/// Why this exists: handing a <c>file:</c> address to the shell opens the page but drops its <c>#anchor</c> and
/// <c>?query</c>, because the shell converts the address to a path first. The help would open at the top of the page
/// instead of at the element or the settings section the user asked about. A browser started with the address on its
/// own command line keeps both. The template is what Windows stores under the browser's registered
/// <c>shell\open\command</c>, for example <c>"C:\...\chrome.exe" --single-argument %1</c> or
/// <c>"C:\...\firefox.exe" -osint -url "%1"</c>.
/// </remarks>
public static class BrowserCommand
{
    /// <summary>
    /// The program and arguments for opening <paramref name="url"/>, or null when the template does not name an
    /// executable. The address is always passed as one quoted argument; a placeholder already inside quotes keeps
    /// the template's own quotes.
    /// </summary>
    public static (string Program, string Arguments)? Build(string? template, string url)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(url) || url.Contains('"'))
        {
            return null;
        }

        template = template.Trim();
        string program;
        string rest;
        if (template[0] == '"')
        {
            var close = template.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = template[1..close];
            rest = template[(close + 1)..].Trim();
        }
        else
        {
            var space = template.IndexOf(' ');
            program = space < 0 ? template : template[..space];
            rest = space < 0 ? string.Empty : template[(space + 1)..].Trim();
        }

        if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var placeholder = FindPlaceholder(rest);
        if (placeholder < 0)
        {
            return (program, rest.Length == 0 ? Quote(url) : $"{rest} {Quote(url)}");
        }

        var quoted = placeholder > 0 && rest[placeholder - 1] == '"'
            && placeholder + 2 < rest.Length && rest[placeholder + 2] == '"';
        var arguments = quoted
            ? rest[..placeholder] + url + rest[(placeholder + 2)..]
            : rest[..placeholder] + Quote(url) + rest[(placeholder + 2)..];
        return (program, arguments);
    }

    private static int FindPlaceholder(string text)
    {
        foreach (var marker in new[] { "%1", "%L", "%l" })
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string Quote(string url) => $"\"{url}\"";
}
