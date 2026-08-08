using System.Text;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>One argument of one call, as a span of the source text.</summary>
internal readonly record struct ArgumentSpan(int Start, int Length);

/// <summary>One call to a named method, with its top-level arguments.</summary>
internal sealed record Invocation(int Offset, IReadOnlyList<ArgumentSpan> Arguments);

/// <summary>
/// One application source file, read as text rather than compiled.
/// </summary>
/// <remarks>
/// SP-0057. The gate has to compare shipped strings against the code that formats them, and the code lives
/// in a WPF application this test project deliberately does not reference - the dependency direction is
/// Tests -&gt; Core only, and the thirteen resource dictionaries are already linked in as data for exactly
/// the same reason. So the sources are read the same way.
/// <para>
/// Reading C# as text is the whole risk, and it is contained by masking instead of parsing. One pass
/// rewrites the body of every comment and every string or character literal to spaces, preserving length
/// and line breaks, so a second pass can match brackets and split arguments on top-level commas without
/// ever tripping over a comma inside a string, a <c>//</c> inside a URL, or a brace inside an
/// interpolation hole. The literal values are kept in a side table keyed by the offset of their opening
/// quote, which is what lets a caller recover a key while still treating the file structurally.
/// </para>
/// <para>
/// Two documented limits, both of which fail loudly rather than silently. A comma inside a generic
/// argument list at call depth zero would be read as an argument separator; no gated entry point takes
/// one, and if that ever changes the gate reports a wrong argument count rather than skipping the call.
/// A multi-line raw string is not offered as a literal value, because its indentation rules are its own
/// small language; a key has never been written that way.
/// </para>
/// </remarks>
internal sealed record AppSourceFile(
    string Name,
    string Text,
    string Masked,
    IReadOnlyDictionary<int, string> Literals)
{
    /// <summary>Where the linked application sources land beside the test assembly.</summary>
    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "app-source");

    /// <summary>
    /// Words that may precede a call. Anything else that is an identifier - a return type, a modifier -
    /// means the parentheses belong to a method *declaration*, which is not a call site.
    /// </summary>
    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.Ordinal)
    {
        "return", "await", "else", "yield", "case", "new", "in", "is", "and", "or", "not",
        "throw", "when", "out", "ref", "do", "try", "checked", "unchecked"
    };

    internal static IReadOnlyList<AppSourceFile> LoadAll(string searchPattern)
    {
        var files = System.IO.Directory.GetFiles(Directory, searchPattern).OrderBy(path => path).ToArray();
        Assert.NotEmpty(files);
        return [.. files.Select(path => Parse(Path.GetFileName(path), File.ReadAllText(path)))];
    }

    internal static AppSourceFile Parse(string name, string text)
    {
        var masked = text.ToCharArray();
        var literals = new Dictionary<int, string>();

        for (var position = 0; position < text.Length;)
        {
            var character = text[position];
            if (character == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                position = BlankUntil(text, masked, position, "\n", inclusive: false);
                continue;
            }

            if (character == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                position = BlankUntil(text, masked, position + 2, "*/", inclusive: true);
                continue;
            }

            if (character == '\'')
            {
                position = ConsumeCharLiteral(text, masked, position);
                continue;
            }

            if (character == '"')
            {
                position = ConsumeStringLiteral(text, masked, literals, position);
                continue;
            }

            position++;
        }

        return new AppSourceFile(name, text, new string(masked), literals);
    }

    /// <summary>The one-based line the offset falls on, for a failure message a reader can act on.</summary>
    internal int LineAt(int offset)
    {
        var line = 1;
        for (var position = 0; position < offset && position < Text.Length; position++)
        {
            if (Text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// Every call to <paramref name="qualifiedName"/> in this file, with its top-level arguments.
    /// </summary>
    /// <remarks>
    /// The name may be qualified (<c>LocalizationService.Format</c>) or bare (<c>SetStatus</c>). A call
    /// whose parentheses never close is thrown rather than skipped: a silent skip is the exact failure
    /// mode this ticket exists to close.
    /// </remarks>
    internal IReadOnlyList<Invocation> Invocations(string qualifiedName)
    {
        var pattern = @"(?<![\w])" + string.Join(
            @"\s*\.\s*",
            qualifiedName.Split('.').Select(Regex.Escape)) + @"\s*\(";
        var found = new List<Invocation>();

        foreach (Match match in Regex.Matches(Masked, pattern))
        {
            if (IsDeclaration(match.Index))
            {
                continue;
            }

            found.Add(new Invocation(match.Index, SplitArguments(match.Index + match.Length)));
        }

        return found;
    }

    /// <summary>Every simple string literal that occurs inside a span, in source order.</summary>
    /// <remarks>
    /// A list rather than a single value, so <c>SetStatus(x ? "A" : "B")</c> - a real call site - is gated
    /// on both keys without the gate needing to know what a conditional expression is. An empty result
    /// means the argument is a variable, which no amount of reading text can resolve.
    /// </remarks>
    internal IReadOnlyList<string> LiteralsIn(ArgumentSpan span) =>
        [.. Enumerable.Range(span.Start, span.Length)
            // The mask test is what excludes a literal nested inside an interpolation hole: masking the
            // outer literal overwrote its quote, so only a literal the code really passes survives here.
            .Where(offset => Masked[offset] == '"' && Literals.ContainsKey(offset))
            .Select(offset => Literals[offset])];

    /// <summary>Whether the parentheses at this name belong to a declaration rather than a call.</summary>
    private bool IsDeclaration(int nameOffset)
    {
        var position = nameOffset - 1;
        while (position >= 0 && char.IsWhiteSpace(Masked[position]))
        {
            position--;
        }

        if (position < 0 || (!char.IsLetterOrDigit(Masked[position]) && Masked[position] != '_'))
        {
            return false; // Preceded by punctuation - a call.
        }

        var end = position + 1;
        while (position >= 0 && (char.IsLetterOrDigit(Masked[position]) || Masked[position] == '_'))
        {
            position--;
        }

        return !ExpressionKeywords.Contains(Masked[(position + 1)..end]);
    }

    /// <summary>Splits an argument list that starts just past its opening parenthesis.</summary>
    private IReadOnlyList<ArgumentSpan> SplitArguments(int start)
    {
        var arguments = new List<ArgumentSpan>();
        var depth = 0;
        var argumentStart = start;

        for (var position = start; position < Masked.Length; position++)
        {
            var character = Masked[position];
            if (character is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (character is ']' or '}')
            {
                depth--;
                continue;
            }

            if (character == ')')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                AddArgument(arguments, argumentStart, position);
                return arguments;
            }

            if (character == ',' && depth == 0)
            {
                AddArgument(arguments, argumentStart, position);
                argumentStart = position + 1;
            }
        }

        throw new InvalidOperationException(
            $"{Name}: an argument list starting at line {LineAt(start)} never closes. The source reader " +
            "must be fixed rather than allowed to skip the call.");
    }

    private void AddArgument(List<ArgumentSpan> arguments, int start, int end)
    {
        var span = Masked[start..end];
        if (arguments.Count == 0 && span.Trim().Length == 0)
        {
            return; // An empty argument list, not a first argument that happens to be blank.
        }

        arguments.Add(new ArgumentSpan(start, end - start));
    }

    private static int BlankUntil(string text, char[] masked, int start, string terminator, bool inclusive)
    {
        var index = text.IndexOf(terminator, start, StringComparison.Ordinal);
        var end = index < 0 ? text.Length : inclusive ? index + terminator.Length : index;
        Blank(text, masked, start, end);
        return end;
    }

    /// <summary>Replaces a span with spaces, keeping line breaks so offsets and lines stay comparable.</summary>
    private static void Blank(string text, char[] masked, int start, int end)
    {
        for (var position = start; position < end && position < text.Length; position++)
        {
            masked[position] = text[position] is '\r' or '\n' ? text[position] : ' ';
        }
    }

    private static int ConsumeCharLiteral(string text, char[] masked, int start)
    {
        for (var position = start + 1; position < text.Length; position++)
        {
            if (text[position] == '\\')
            {
                position++;
                continue;
            }

            if (text[position] == '\'')
            {
                Blank(text, masked, start + 1, position);
                return position + 1;
            }
        }

        return text.Length;
    }

    /// <summary>
    /// Consumes one string literal, whatever form it takes, and returns the offset just past it.
    /// </summary>
    /// <remarks>
    /// <paramref name="quote"/> is the first quote character; the <c>$</c> and <c>@</c> prefixes are read
    /// backwards from it, which is unambiguous because neither can be the tail of an identifier. Inside an
    /// interpolation hole the scanner recurses, because a hole may hold a literal of its own - without
    /// that, <c>$"{map["k"]}"</c> would be read as ending three characters early and every bracket after
    /// it would be counted wrong.
    /// </remarks>
    private static int ConsumeStringLiteral(
        string text,
        char[] masked,
        IDictionary<int, string> literals,
        int quote)
    {
        var prefixStart = quote;
        while (prefixStart > 0 && text[prefixStart - 1] is '$' or '@')
        {
            prefixStart--;
        }

        var prefix = text[prefixStart..quote];
        var verbatim = prefix.Contains('@');
        var interpolated = prefix.Contains('$');

        var run = 0;
        while (quote + run < text.Length && text[quote + run] == '"')
        {
            run++;
        }

        var end = run >= 3
            ? ConsumeRawString(text, quote, run)
            : ConsumeSimpleString(text, masked, literals, quote, verbatim, interpolated);

        if (run >= 3)
        {
            var body = text[(quote + run)..Math.Max(quote + run, end - run)];
            if (!interpolated && !body.Contains('\n'))
            {
                literals[quote] = body;
            }
        }

        Blank(text, masked, quote + 1, Math.Max(quote + 1, end - 1));
        return end;
    }

    private static int ConsumeRawString(string text, int quote, int run)
    {
        for (var position = quote + run; position < text.Length; position++)
        {
            if (text[position] != '"')
            {
                continue;
            }

            var closing = 0;
            while (position + closing < text.Length && text[position + closing] == '"')
            {
                closing++;
            }

            if (closing >= run)
            {
                return position + closing;
            }

            position += closing - 1;
        }

        return text.Length;
    }

    private static int ConsumeSimpleString(
        string text,
        char[] masked,
        IDictionary<int, string> literals,
        int quote,
        bool verbatim,
        bool interpolated)
    {
        var depth = 0;
        var position = quote + 1;

        while (position < text.Length)
        {
            var character = text[position];

            if (!verbatim && character == '\\' && position + 1 < text.Length)
            {
                position += 2;
                continue;
            }

            if (interpolated && character == '{')
            {
                if (depth == 0 && position + 1 < text.Length && text[position + 1] == '{')
                {
                    position += 2;
                    continue;
                }

                depth++;
                position++;
                continue;
            }

            if (interpolated && character == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }
                else if (position + 1 < text.Length && text[position + 1] == '}')
                {
                    position++;
                }

                position++;
                continue;
            }

            if (depth > 0 && character == '\'')
            {
                position = ConsumeCharLiteral(text, masked, position);
                continue;
            }

            if (character == '"')
            {
                if (depth > 0)
                {
                    position = ConsumeStringLiteral(text, masked, literals, position);
                    continue;
                }

                if (verbatim && position + 1 < text.Length && text[position + 1] == '"')
                {
                    position += 2;
                    continue;
                }

                if (!interpolated)
                {
                    literals[quote] = Decode(text[(quote + 1)..position], verbatim);
                }

                return position + 1;
            }

            position++;
        }

        return text.Length;
    }

    private static string Decode(string body, bool verbatim)
    {
        if (verbatim)
        {
            return body.Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        var decoded = new StringBuilder(body.Length);
        for (var position = 0; position < body.Length; position++)
        {
            if (body[position] != '\\' || position + 1 >= body.Length)
            {
                decoded.Append(body[position]);
                continue;
            }

            position++;
            decoded.Append(body[position] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                var other => other
            });
        }

        return decoded.ToString();
    }
}
