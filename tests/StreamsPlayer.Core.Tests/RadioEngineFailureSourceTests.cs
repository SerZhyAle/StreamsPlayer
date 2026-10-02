using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0189, enforced over the App's own source (read as text - see <see cref="AppSourceFile"/>): the radio engine
/// reports a failure as what it is, never through an exception it creates for the purpose. It used to stamp
/// <c>InvalidOperationException</c> on every LibVLC error, and the recovery classifier read that type name as a
/// local device fault - so every network failure was given up on at once.
/// </summary>
public sealed class RadioEngineFailureSourceTests
{
    [Fact]
    public void TheRadioEngine_ConstructsNoExceptionToCarryAFailure()
    {
        var source = AppSourceFile.LoadAll("StandardAudioPlayback.cs").Single();

        var construction = Regex.Match(source.Masked, @"\bnew\s+\w*Exception\s*\(");

        Assert.False(construction.Success,
            $"{source.Name}:{(construction.Success ? source.LineAt(construction.Index) : 0)}: report the failure's reason, " +
            "cause and whether it is local in StandardAudioFailedEventArgs instead of wrapping it in an exception.");
    }
}
