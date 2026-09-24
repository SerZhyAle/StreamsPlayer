using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0109: the sentence a failure message shows in place of the exception's own text - a cause the user
/// can act on and the action, in the interface language (<c>APP-BEHAVIOUR</c> rule 6). The exception itself
/// goes to the log at the call site.
/// </summary>
internal static class FailureCauseText
{
    // Literal keys, one per cause: the call-site gate can only see a key it can read.
    internal static string Describe(Exception exception) => FailureCauseClassifier.Classify(exception) switch
    {
        FailureCause.Network => LocalizationService.Get("FailureCauseNetwork"),
        FailureCause.DamagedData => LocalizationService.Get("FailureCauseDamagedData"),
        FailureCause.Storage => LocalizationService.Get("FailureCauseStorage"),
        _ => LocalizationService.Get("FailureCauseUnknown")
    };

    /// <summary>What failed and what was kept, then - as its own paragraph - why and what to do.</summary>
    internal static string Compose(string whatFailed, Exception exception) =>
        $"{whatFailed}{Environment.NewLine}{Environment.NewLine}{Describe(exception)}";
}
