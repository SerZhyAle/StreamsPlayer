using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// Asserts on what a persisted JSON document <em>says</em>, not on how it is laid out.
/// </summary>
/// <remarks>
/// These assertions exist to prove a contract - an enum is written by name so an older build can read
/// it back, a property is omitted rather than written as null. Spelling them as literal substrings with
/// a <c>": "</c> separator quietly pinned them to <c>WriteIndented = true</c> as well, and SP-0067
/// turning indentation off (a 15 MB state file is machine state, not a document) failed four tests that
/// had nothing to do with the change. Matching the separator loosely keeps the contract and drops the
/// accident.
/// </remarks>
internal static class JsonAssert
{
    /// <summary>Asserts that <paramref name="property"/> is present with exactly <paramref name="value"/>.</summary>
    /// <param name="value">The value token as it appears in JSON - quoted for a string, bare for a number.</param>
    public static void HasProperty(string json, string property, string value)
    {
        var pattern = $"\"{Regex.Escape(property)}\"\\s*:\\s*{Regex.Escape(value)}";
        Assert.True(
            Regex.IsMatch(json, pattern),
            $"Expected property \"{property}\" with value {value}.{Environment.NewLine}Actual JSON: {json}");
    }
}
