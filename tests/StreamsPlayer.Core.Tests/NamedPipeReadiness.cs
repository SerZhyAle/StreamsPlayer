using System.Diagnostics;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// <c>ActivationPipeListener.Start()</c> only schedules its accept loop on the pool; the pipe exists once the loop has
/// created its server instance, and on a loaded machine that is seconds later, not microseconds. A test that acts through
/// a client with a short connect bound (the console companion's is one second) therefore waits here first.
/// </summary>
/// <remarks>
/// The pipe is looked for in the pipe directory and never opened. Opening it - <c>File.Exists</c> on the pipe path
/// included, which opens the name to read its attributes - connects as a client and consumes the one instance the listener
/// serves at a time, so the readiness probe would itself be the first, empty, rejected connection.
/// </remarks>
internal static class NamedPipeReadiness
{
    // Only a guard against a listener that never starts; a pipe that is going to appear does so in milliseconds.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private const string PipeDirectory = @"\\.\pipe\";

    public static void WaitUntilListed(string pipeName)
    {
        var clock = Stopwatch.StartNew();
        while (!Directory.EnumerateFileSystemEntries(PipeDirectory, pipeName).Any())
        {
            if (clock.Elapsed > Limit)
            {
                throw new TimeoutException($"The pipe {pipeName} was not listed within {Limit}; its listener never created an instance.");
            }

            Thread.Sleep(10);
        }
    }
}
