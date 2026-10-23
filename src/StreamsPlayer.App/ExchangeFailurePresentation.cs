using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class ExchangeFailurePresentation
{
    internal static string Text(ExchangeFailureRow row) => LocalizationService.Get(row.MessageKey)
        + (row.Step is { } step ? Environment.NewLine + LocalizationService.Get(ExchangeDiagnosisSteps.MessageKey(step)) : "");

    internal static ExchangeFailureRow? BroadcastRow(string? reason) => ExchangeFailureRows.ByCode(reason)
        ?? (ExchangeSealedStates.FromCode(reason) is var state && state != ExchangeSealedState.None
            ? new ExchangeFailureRow(state.Code(), ExchangeFailureKind.Local, null, false, state.MessageKey()) : null);
}
