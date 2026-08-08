namespace StreamsPlayer.Core;

/// <summary>
/// Composite formatting for localized templates, with the one property <see cref="string.Format(IFormatProvider, string, object?[])"/>
/// does not have: it cannot throw.
/// </summary>
/// <remarks>
/// SP-0057. <c>string.Format</c> is asymmetric - surplus arguments are ignored in silence, but a template
/// referencing an index the caller did not supply throws <see cref="FormatException"/>. Every localized
/// string in the application is rendered from an <c>async void</c> event handler, and none of them filters
/// for that exception, so adding a placeholder to a shipped string without finding its call site did not
/// produce a wrong status line - it ended the process. The gate in the test project is what stops the
/// mismatch being written; this is what decides its cost if one ever escapes.
/// <para>
/// The parser here is also the one the gate uses, so the two cannot disagree about what a placeholder is.
/// </para>
/// </remarks>
public static class LocalizedFormat
{
    /// <summary>
    /// The ordered multiset of composite-format argument indices a template references.
    /// </summary>
    /// <remarks>
    /// Format specifiers are ignored on purpose: <c>{0:N0}</c> and <c>{0}</c> take the same argument, and a
    /// translator may legitimately want a different one. <c>{{</c> and <c>}}</c> are escapes for literal
    /// braces and reference nothing. A brace group this parser cannot read - unclosed, non-numeric, an
    /// alignment field - yields no index rather than an exception: it is not something the caller can be
    /// asked to supply an argument for, and <see cref="Apply"/>'s catch is what covers it at runtime.
    /// </remarks>
    public static IReadOnlyList<int> PlaceholderIndices(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var indices = new List<int>();
        for (var position = 0; position < template.Length; position++)
        {
            var character = template[position];
            if (character == '}')
            {
                // A doubled closing brace is an escape; a lone one is stray text the runtime will reject.
                if (position + 1 < template.Length && template[position + 1] == '}')
                {
                    position++;
                }

                continue;
            }

            if (character != '{')
            {
                continue;
            }

            if (position + 1 < template.Length && template[position + 1] == '{')
            {
                position++;
                continue;
            }

            var digits = position + 1;
            while (digits < template.Length && char.IsAsciiDigit(template[digits]))
            {
                digits++;
            }

            if (digits == position + 1 || digits >= template.Length)
            {
                continue; // No index, or the group never closes.
            }

            if (template[digits] is not ('}' or ':'))
            {
                continue; // An alignment field or something else this parser does not describe.
            }

            if (int.TryParse(template.AsSpan(position + 1, digits - position - 1), out var index))
            {
                indices.Add(index);
            }

            position = digits;
        }

        indices.Sort();
        return indices;
    }

    /// <summary>
    /// The number of arguments a template needs, which is one past its highest index.
    /// </summary>
    public static int RequiredArgumentCount(string template)
    {
        var indices = PlaceholderIndices(template);
        return indices.Count == 0 ? 0 : indices[^1] + 1;
    }

    /// <summary>
    /// Formats <paramref name="template"/> and never throws.
    /// </summary>
    /// <remarks>
    /// Two defences, because a mismatch has two causes. A template that asks for more arguments than were
    /// supplied is padded with nulls, which render as empty strings: the sentence survives with the
    /// unsupplied positions blank, which is legible and localized rather than absent. A template the
    /// runtime cannot parse at all - unbalanced braces - is returned verbatim, which is exactly how a
    /// missing key already degrades, since the lookup returns the key name.
    /// </remarks>
    public static string Apply(IFormatProvider provider, string template, params object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(template);

        var supplied = arguments ?? [];
        var required = RequiredArgumentCount(template);
        if (supplied.Length < required)
        {
            var padded = new object?[required];
            supplied.CopyTo(padded, 0);
            supplied = padded;
        }

        try
        {
            return string.Format(provider, template, supplied);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
