using System.Text;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

public static partial class DiagnosticValueRedactor
{
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 100)]
    private static partial Regex PrivateKeyBlocks();

    [GeneratedRegex(@"(?i)((?:proxy-)?authorization\s*[:=]\s*)[^\r\n|]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationHeaders();

    [GeneratedRegex("(?i)(?<![A-Za-z0-9_])(\"?(?:accessPin|pin|password|apiKey|api_key|client_secret|client_id|refresh_token|RefreshToken|access_token|AccessToken|code_verifier|encryptionSalt|salt|authorization)\"?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s|,;}]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex StructuredSecrets();

    public static string Redact(string text)
    {
        try
        {
            var sanitized = DropStructuredValues(PrivateKeyBlocks().Replace(text, "[REDACTED]"));
            sanitized = AuthorizationHeaders().Replace(sanitized, "$1[REDACTED]");
            return StructuredSecrets().Replace(sanitized, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            // DIAGNOSTIC-REPORT section 8 C permits dropping a value that cannot be sanitized safely.
            return "[REDACTED]";
        }
    }

    private static string DropStructuredValues(string text)
    {
        // Drop JSON blobs rather than infer safety from their outer setting name or raw key spelling.
        // Balanced scanning also covers multiline/escaped keys; an incomplete value costs the remainder.
        StringBuilder? result = null;
        var copied = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var opening = text[index];
            if (opening != '{' && (opening != '[' || !LooksLikeArray(text, index + 1)))
            {
                continue;
            }

            result ??= new StringBuilder();
            result.Append(text, copied, index - copied).Append("[REDACTED]");
            var nesting = new Stack<char>();
            nesting.Push(opening);
            var quoted = false;
            var escaped = false;
            var end = index + 1;
            for (; end < text.Length && nesting.Count > 0; end++)
            {
                var character = text[end];
                if (quoted)
                {
                    if (escaped) { escaped = false; }
                    else if (character == '\\') { escaped = true; }
                    else if (character == '"') { quoted = false; }
                }
                else if (character == '"') { quoted = true; }
                else if (character is '{' or '[') { nesting.Push(character); }
                else if (character is '}' or ']')
                {
                    if (nesting.Pop() != (character == '}' ? '{' : '['))
                    {
                        end = text.Length;
                        break;
                    }
                }
            }
            copied = end;
            index = end - 1;
        }
        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool LooksLikeArray(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) { index++; }
        return index < text.Length && (text[index] is '{' or '[' or '"' or ']' or '-' or 't' or 'f' or 'n'
            || char.IsAsciiDigit(text[index]));
    }
}
