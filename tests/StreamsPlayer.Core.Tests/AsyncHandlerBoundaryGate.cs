using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>An <c>async void</c> method, located in the masked source.</summary>
/// <param name="Name">The method name.</param>
/// <param name="DeclarationOffset">Where <c>async</c> starts.</param>
/// <param name="BodyStart">The opening brace of a block body, or the <c>=&gt;</c> of an expression body.</param>
/// <param name="BodyEnd">One past the closing brace, or one past the terminating semicolon.</param>
/// <param name="IsExpressionBodied">Whether the body is an expression rather than a block.</param>
internal sealed record AsyncVoidMethod(
    string Name,
    int DeclarationOffset,
    int BodyStart,
    int BodyEnd,
    bool IsExpressionBodied);

/// <summary>One place an asynchronous handler runs without a failure boundary.</summary>
internal sealed record BoundaryFinding(string File, int Line, string What);

/// <summary>
/// SP-0119 requirement 2: every asynchronous UI handler has an explicit failure boundary.
/// </summary>
/// <remarks>
/// An exception escaping an <c>async void</c> method - or an <c>async</c> lambda converted to a void
/// delegate, which is the same thing without a name - reaches the dispatcher's last-resort handler, and
/// that ends the process. So each one must either catch everything itself or run inside
/// <c>HandlerBoundary.Run</c>. What counts as "catches everything", read over <see cref="AppSourceFile"/>'s
/// masked text so comments and strings cannot fool it:
/// <list type="bullet">
/// <item>an <c>async void</c> method has a block body whose only statement is a <c>try</c>, and one of that
/// statement's clauses is an unfiltered <c>catch (Exception)</c> or a bare <c>catch</c>;</item>
/// <item>an <c>async</c> lambda is never handed straight to <c>Action</c>, an event-handler delegate or an
/// event subscription - it goes to <c>HandlerBoundary.Run</c>, whose own body satisfies the rule above.</item>
/// </list>
/// Documented limit: an <c>async</c> lambda assigned to a void delegate through a shape the patterns below do
/// not name (a method-group conversion to a custom delegate type) is not seen. The patterns cover every
/// shape the application has ever used.
/// </remarks>
internal static class AsyncHandlerBoundaryGate
{
    private static readonly Regex AsyncVoidDeclaration = new(@"\basync\s+void\s+(?<name>\w+)\s*(<[^>(]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly Regex[] VoidAsyncLambdas =
    [
        // new Action(async () => ..), new EventHandler(async (s, e) => ..), new RoutedEventHandler(async ..)
        new(@"\bnew\s+\w*(Action|Handler)\b(\s*<[^>]*>)?\s*\(\s*async\b", RegexOptions.Compiled),
        // button.Click += async (_, _) => ..
        new(@"\+=\s*async\b", RegexOptions.Compiled),
        // Action run = async () => ..
        new(@"\b(Action|\w*EventHandler)\b(\s*<[^>]*>)?\s+\w+\s*=\s*async\b", RegexOptions.Compiled)
    ];

    private static readonly Regex BoundaryCatch = new(@"^catch\s*(\(\s*(System\.)?Exception(\s+\w+)?\s*\))?\s*\{", RegexOptions.Compiled);

    internal static IReadOnlyList<BoundaryFinding> Findings(AppSourceFile source)
    {
        var findings = new List<BoundaryFinding>();
        foreach (var method in AsyncVoidMethods(source))
        {
            if (!HasBoundary(source, method))
            {
                findings.Add(new BoundaryFinding(
                    source.Name,
                    source.LineAt(method.DeclarationOffset),
                    $"async void {method.Name} has no failure boundary"));
            }
        }

        foreach (var pattern in VoidAsyncLambdas)
        {
            foreach (Match match in pattern.Matches(source.Masked))
            {
                findings.Add(new BoundaryFinding(
                    source.Name,
                    source.LineAt(match.Index),
                    $"async lambda as a void delegate ('{source.Text.Substring(match.Index, match.Length).Trim()}')"));
            }
        }

        return findings;
    }

    internal static IReadOnlyList<AsyncVoidMethod> AsyncVoidMethods(AppSourceFile source)
    {
        var masked = source.Masked;
        var methods = new List<AsyncVoidMethod>();
        foreach (Match match in AsyncVoidDeclaration.Matches(masked))
        {
            var parametersEnd = MatchingClose(masked, match.Index + match.Length - 1, '(', ')', source);
            var position = SkipWhitespace(masked, parametersEnd + 1);
            var name = match.Groups["name"].Value;
            if (masked[position] == '{')
            {
                var close = MatchingClose(masked, position, '{', '}', source);
                methods.Add(new AsyncVoidMethod(name, match.Index, position, close + 1, IsExpressionBodied: false));
            }
            else if (masked[position] == '=' && masked[position + 1] == '>')
            {
                var end = StatementEnd(masked, position + 2, source);
                methods.Add(new AsyncVoidMethod(name, match.Index, position, end + 1, IsExpressionBodied: true));
            }
            else
            {
                throw new InvalidOperationException(
                    $"{source.Name}:{source.LineAt(match.Index)}: async void {name} has neither a block nor an " +
                    "expression body. The gate must be taught this shape rather than allowed to skip it.");
            }
        }

        return methods;
    }

    /// <summary>Whether the method's block body is one <c>try</c> statement with an unfiltered catch-all.</summary>
    internal static bool HasBoundary(AppSourceFile source, AsyncVoidMethod method)
    {
        if (method.IsExpressionBodied)
        {
            return false;
        }

        var masked = source.Masked;
        var bodyClose = method.BodyEnd - 1;
        var position = SkipWhitespace(masked, method.BodyStart + 1);
        if (!IsKeywordAt(masked, position, "try"))
        {
            return false;
        }

        position = SkipWhitespace(masked, position + 3);
        if (masked[position] != '{')
        {
            return false;
        }

        position = SkipWhitespace(masked, MatchingClose(masked, position, '{', '}', source) + 1);
        var catchesEverything = false;
        while (position < bodyClose)
        {
            if (IsKeywordAt(masked, position, "catch"))
            {
                var clauseBrace = masked.IndexOf('{', position);
                if (BoundaryCatch.IsMatch(masked[position..(clauseBrace + 1)]))
                {
                    catchesEverything = true;
                }

                position = SkipWhitespace(masked, MatchingClose(masked, clauseBrace, '{', '}', source) + 1);
                continue;
            }

            if (IsKeywordAt(masked, position, "finally"))
            {
                var clauseBrace = masked.IndexOf('{', position);
                position = SkipWhitespace(masked, MatchingClose(masked, clauseBrace, '{', '}', source) + 1);
                continue;
            }

            return false; // A statement after the try runs outside it.
        }

        return catchesEverything;
    }

    private static bool IsKeywordAt(string masked, int position, string keyword) =>
        string.CompareOrdinal(masked, position, keyword, 0, keyword.Length) == 0 &&
        (position + keyword.Length >= masked.Length || !IsIdentifierCharacter(masked[position + keyword.Length]));

    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static int SkipWhitespace(string masked, int position)
    {
        while (position < masked.Length && char.IsWhiteSpace(masked[position]))
        {
            position++;
        }

        return position;
    }

    private static int MatchingClose(string masked, int open, char opening, char closing, AppSourceFile source)
    {
        var depth = 0;
        for (var position = open; position < masked.Length; position++)
        {
            if (masked[position] == opening)
            {
                depth++;
            }
            else if (masked[position] == closing && --depth == 0)
            {
                return position;
            }
        }

        throw new InvalidOperationException($"{source.Name}:{source.LineAt(open)}: '{opening}' never closes.");
    }

    private static int StatementEnd(string masked, int start, AppSourceFile source)
    {
        var depth = 0;
        for (var position = start; position < masked.Length; position++)
        {
            switch (masked[position])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return position;
            }
        }

        throw new InvalidOperationException($"{source.Name}:{source.LineAt(start)}: an expression body never ends.");
    }
}
