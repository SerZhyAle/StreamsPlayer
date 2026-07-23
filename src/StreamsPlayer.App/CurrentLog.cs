using System.IO;
using System.Text;

namespace StreamsPlayer.App;

internal sealed class CurrentLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public CurrentLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var stream = new FileStream(Path.Combine(directory, "Current.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        }
        catch (Exception)
        {
            // Diagnostics must not prevent the application from opening when local storage is unavailable.
        }
    }

    public void Information(string message) => Write("Information", message);

    // Measurable diagnostic record: a category plus KEY=value fields joined by " | " so lines stay greppable.
    public void Event(string category, params string[] fields) =>
        Write("Diag", fields.Length == 0 ? category : $"{category} | {string.Join(" | ", fields)}");

    public void Error(string operation, Exception exception) => Write("Error", $"{operation}: {exception}");

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // A failed diagnostic flush must not interrupt WPF shutdown.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private void Write(string severity, string message)
    {
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine($"{DateTimeOffset.UtcNow:O} [{severity}] {Flatten(message)}");
            }
            catch (Exception)
            {
                // Logging has no recovery path; continuing is safer than masking the original application operation.
                _writer = null;
            }
        }
    }

    // Full URLs are retained for measurement; only line breaks are flattened so each record stays on one line.
    private static string Flatten(string message) => message.ReplaceLineEndings(" | ");
}
