using StreamsPlayer.Core;

namespace StreamsPlayer.Cli;

/// <summary>
/// SP-0275: the entry point of <c>StreamsPlayerCli.exe</c>. Everything it does is <see cref="AppCommandConsole.Run"/> in the
/// core, which the in-process tests of the command profile run; this passes it the identity of the copy this process
/// belongs to, this build's stamp and the process's own standard streams, and exits with the code it returns.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        using var standardOutput = Console.OpenStandardOutput();
        using var standardError = Console.OpenStandardError();
        return AppCommandConsole.Run(
            args, AppDataProfile.Identity(), BuildVersion.Of(typeof(Program).Assembly), standardOutput, standardError);
    }
}
