namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0123, enforced over the App's own sources (read as text - see <see cref="AppSourceFile"/>): a channel
/// address reaches the diagnostic log only through the one sink that redacts it.
/// </summary>
/// <remarks>
/// About forty call sites log a stream address, and more will be added. Gating each of them would chase
/// the wrong thing; the sink redacts every line, so what has to hold is that the sink cannot be bypassed.
/// Two rules: inside <c>CurrentLog.cs</c> every text written to the file goes through <c>Flatten</c>,
/// which calls the redactor; and no other App file opens, names or writes a session log.
/// </remarks>
public sealed class LogSinkRedactionSourceTests
{
    private const string SinkFile = "CurrentLog.cs";

    [Fact]
    public void EveryLogLineGoesThroughTheRedactingSink()
    {
        var findings = Findings(AppSourceFile.LoadAll("*.cs")).ToArray();

        Assert.True(
            findings.Length == 0,
            "A log line that skips CurrentLog's redaction can carry rtsp://user:password@ into the archive " +
            "the user mails (DIAGNOSTIC-REPORT rule 3):" + Environment.NewLine +
            string.Join(Environment.NewLine, findings.Select(finding => "  " + finding)));
    }

    [Fact]
    public void TheGateSeesTheSink()
    {
        // A gate that finds nothing to check passes on anything.
        var sink = AppSourceFile.LoadAll(SinkFile).Single();
        Assert.True(sink.Invocations("WriteLine").Count >= 2, "CurrentLog's WriteLine calls were not found.");
    }

    [Theory]
    [InlineData(SinkFile, """
        class CurrentLog
        {
            void Write(string severity, string message) { _writer.WriteLine($"{severity} {message}"); }
            static string Flatten(string message) => CatalogUrlIdentity.RedactText(message);
        }
        """)]
    [InlineData(SinkFile, """
        class CurrentLog
        {
            void Write(string severity, string message) { _writer.WriteLine(Flatten(message)); }
            static string Flatten(string message) => message.ReplaceLineEndings(" | ");
        }
        """)]
    [InlineData(SinkFile, """
        class CurrentLog
        {
            private readonly DiagnosticPathRedactor _paths;
            void Write(string severity, string message) { _writer.WriteLine(Flatten(message)); }
            string Flatten(string message) => CatalogUrlIdentity.RedactText(message).ReplaceLineEndings(" | ");
        }
        """)]
    [InlineData(SinkFile, """
        class CurrentLog
        {
            void Write(string message) { _writer.WriteLine(Flatten(message)); _writer.BaseStream.Write(Encoding.UTF8.GetBytes(message)); }
            static string Flatten(string message) => CatalogUrlIdentity.RedactText(message);
        }
        """)]
    [InlineData("PlayerWindow.Recording.cs", """
        class P
        {
            void Log(StreamChannel channel) =>
                File.AppendAllText(Path.Combine(_dataDirectory, DiagnosticLogFiles.CurrentLogName), channel.Url);
        }
        """)]
    [InlineData("MainWindow.Probe.cs", """
        class M { void Log(string url) => Console.WriteLine($"probe {url}"); }
        """)]
    public void TheGateFailsALogWriteThatBypassesRedaction(string name, string source)
    {
        Assert.NotEmpty(Findings([AppSourceFile.Parse(name, source)]));
    }

    [Fact]
    public void TheGatePassesACallSiteThatUsesTheSink()
    {
        var source = AppSourceFile.Parse("PlayerWindow.Open.cs", """
            class P { void Open(StreamChannel channel) => _log.Event("PLAYER OPEN", $"url={channel.Url}"); }
            """);

        Assert.Empty(Findings([source]));
    }

    private static IEnumerable<string> Findings(IEnumerable<AppSourceFile> sources)
    {
        foreach (var source in sources)
        {
            var findings = source.Name == SinkFile ? SinkFindings(source) : BypassFindings(source);
            foreach (var finding in findings)
            {
                yield return $"{source.Name}:{finding}";
            }
        }
    }

    private static IEnumerable<string> SinkFindings(AppSourceFile source)
    {
        var flatten = source.Masked.IndexOf("string Flatten(", StringComparison.Ordinal);
        var flattenEnd = flatten < 0 ? -1 : source.Masked.IndexOf(';', flatten);
        if (flatten < 0 || flattenEnd < 0 ||
            !source.Masked[flatten..flattenEnd].Contains("CatalogUrlIdentity.RedactText(", StringComparison.Ordinal))
        {
            yield return $"{(flatten < 0 ? 1 : source.LineAt(flatten))} Flatten must pass the message through CatalogUrlIdentity.RedactText";
        }

        // SP-0137: and through the path redactor, so a profile path loses the account name at the sink.
        if (flatten < 0 || flattenEnd < 0 ||
            !source.Masked[flatten..flattenEnd].Contains("_paths.Redact(", StringComparison.Ordinal) ||
            !source.Masked.Contains("DiagnosticPathRedactor _paths", StringComparison.Ordinal))
        {
            yield return $"{(flatten < 0 ? 1 : source.LineAt(flatten))} Flatten must pass the message through a DiagnosticPathRedactor _paths";
        }

        foreach (var call in source.Invocations("WriteLine").Where(call => call.Arguments.Count > 0))
        {
            // Read the raw text: an interpolation hole is blanked in the mask, and Flatten sits in one.
            var argument = source.Text.Substring(call.Arguments[0].Start, call.Arguments[0].Length);
            if (!argument.Contains("Flatten(", StringComparison.Ordinal))
            {
                yield return $"{source.LineAt(call.Offset)} WriteLine writes text that does not go through Flatten";
            }
        }

        // Compaction copies the kept head and tail back as raw bytes; they were redacted when first written.
        foreach (var call in source.Invocations("BaseStream.Write"))
        {
            var argument = call.Arguments.Count == 1
                ? source.Masked.Substring(call.Arguments[0].Start, call.Arguments[0].Length).Trim()
                : string.Empty;
            if (argument is not ("head" or "tail"))
            {
                yield return $"{source.LineAt(call.Offset)} BaseStream.Write may only copy back the compacted head or tail";
            }
        }
    }

    private static readonly string[] BypassMarkers =
    [
        "DiagnosticLogFiles.CurrentLogName",
        "DiagnosticLogFiles.ReserveSessionPath",
        "DiagnosticLogFiles.Rotate",
        "Console.Write",
        "Trace.Write",
    ];

    private static IEnumerable<string> BypassFindings(AppSourceFile source)
    {
        foreach (var marker in BypassMarkers)
        {
            for (var offset = source.Masked.IndexOf(marker, StringComparison.Ordinal);
                 offset >= 0;
                 offset = source.Masked.IndexOf(marker, offset + marker.Length, StringComparison.Ordinal))
            {
                yield return $"{source.LineAt(offset)} {marker} outside {SinkFile} writes a log the sink never redacts";
            }
        }
    }
}
