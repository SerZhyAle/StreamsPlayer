using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamsPlayer.App;

internal sealed class CurrentLog : IDisposable
{
    private static readonly Regex UrlPattern = new(@"(?:https?|rtsp)://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
                _writer?.WriteLine($"{DateTimeOffset.UtcNow:O} [{severity}] {Sanitize(message)}");
            }
            catch (Exception)
            {
                // Logging has no recovery path; continuing is safer than masking the original application operation.
                _writer = null;
            }
        }
    }

    private static string Sanitize(string message) =>
        UrlPattern.Replace(message.ReplaceLineEndings(" | "), "<url>");
}
