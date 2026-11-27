using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class RemoteControlText
{
    internal static string ClassName(string risk) => LocalizationService.Get(risk switch
    {
        RemoteRiskClass.ReadsUserData => "RemoteClassReadsUserData",
        RemoteRiskClass.WritesUserData => "RemoteClassWritesUserData",
        RemoteRiskClass.LeavesMachine => "RemoteClassLeavesMachine",
        _ => "RemoteClassSafe"
    });

    internal static string Program(string? product) => product switch
    {
        "fms-android" => "Fast Media Sorter & Organizer for Android",
        "fms-wear" => "Fast Media Sorter for Wear OS",
        "fms-windows" => "Fast Media Sorter & Sharing for Windows",
        "fms-web" => "FastMediaSorter for Web",
        "streamsplayer-windows" => "STREAMS Player for Windows",
        "streamsplayer-android" => "STREAMS Player for Android",
        "streamsplayer-wear" => "STREAMS Player for Wear OS",
        "cyrflip-windows" => "CyrFlip",
        _ => product ?? string.Empty
    };

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    internal static string Title(RemoteConsentQuestion question) =>
        LocalizationService.Format("RemotePromptTitle",
            string.IsNullOrWhiteSpace(question.DeviceName) ? LocalizationService.Get("RemoteUnknownDevice") : question.DeviceName,
            LocalizationService.Get("ProductName"));

    internal static string Command(RemoteConsentQuestion question)
    {
        var lines = new List<string> { Summary(question.Command) };
        for (var index = 0; index < question.Words.Count; index += 2)
        {
            var name = question.Words[index];
            var sensitive = question.Command.FindArgument(name[2..])?.Sensitive == true;
            lines.Add(sensitive ? name : $"{name} {question.Words[index + 1]}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string Summary(AppCommandEntry command) => command.Summary;

    internal static string Kind(RemoteConsentQuestion question) =>
        LocalizationService.Format("RemotePromptKind", ClassName(question.Command.Risk));

    internal static string Countdown(RemoteConsentQuestion question) =>
        LocalizationService.Format("RemotePromptCountdown", question.SecondsLeft);

    internal static string Outcome(string outcome) => LocalizationService.Get(outcome switch
    {
        AppCommandStatus.Done => "RemoteOutcomeDone",
        AppCommandStatus.Accepted => "RemoteOutcomeAccepted",
        AppCommandStatus.Busy => "RemoteOutcomeBusy",
        RemoteCommandReason.Declined => "RemoteOutcomeDeclined",
        RemoteCommandReason.NotPermitted => "RemoteOutcomeNotPermitted",
        RemoteCommandReason.Expired => "RemoteOutcomeExpired",
        AppCommandReason.UnknownVerb => "RemoteOutcomeUnknownVerb",
        AppCommandReason.BadArgument => "RemoteOutcomeBadArgument",
        RemoteCommandLog.SealedFailed => "RemoteOutcomeSealedFailed",
        RemoteCommandLog.Repeated => "RemoteOutcomeRepeated",
        _ => "RemoteOutcomeFailed"
    });
}
