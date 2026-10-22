using System.Text;

namespace StreamsPlayer.Core;

/// <summary>What the walk found at one step (SP-0225 reconciliation point 2).</summary>
public enum ExchangeDiagnosisState
{
    /// <summary>Checked, or proven by the answer that came back, and fine.</summary>
    Passed,

    /// <summary>The first step that did not pass.</summary>
    Failed,

    /// <summary>This PC cannot see it; the step is offered to the person as the next thing to try (item CY).</summary>
    CouldNotCheck,

    /// <summary>The step does not belong to this setup. Skipped, never shown as passed (item CY).</summary>
    NotApplicable,

    /// <summary>The walk stopped, or no connection reached it.</summary>
    NotReached
}

public enum ExchangeProbeOutcome
{
    Passed,
    Failed,
    CouldNotCheck
}

/// <summary>The target of a diagnosis. Used by the probes only; never part of a result's report.</summary>
public sealed record ExchangeDiagnosisContext(string Host, int Port);

/// <summary>
/// The read-only checks of the steps this PC can make itself. A probe changes no setting and is called only while
/// a person is waiting for a diagnosis they asked for.
/// </summary>
public interface IExchangeDiagnosisProbes
{
    Task<ExchangeProbeOutcome> ProbeAsync(ExchangeDiagnosisStep step, ExchangeDiagnosisContext context,
        CancellationToken cancellationToken);
}

public sealed record ExchangeDiagnosisStepResult(ExchangeDiagnosisStep Step, ExchangeDiagnosisState State);

public sealed class ExchangeDiagnosisResult
{
    internal ExchangeDiagnosisResult(ExchangeFailureRow row, ExchangeDiagnosisApplies route,
        IReadOnlyList<ExchangeDiagnosisStepResult> steps)
    {
        Row = row;
        Route = route;
        Steps = steps;
    }

    public ExchangeFailureRow Row { get; }

    public ExchangeDiagnosisApplies Route { get; }

    public IReadOnlyList<ExchangeDiagnosisStepResult> Steps { get; }

    /// <summary>
    /// What to try: the first step that failed, else the first one the person is offered. Null when the row has
    /// no step (the row's own text stands alone, item DA).
    /// </summary>
    public ExchangeDiagnosisStep? AdviceStep =>
        Steps.FirstOrDefault(item => item.State == ExchangeDiagnosisState.Failed)?.Step
        ?? Steps.FirstOrDefault(item => item.State == ExchangeDiagnosisState.CouldNotCheck)?.Step;

    /// <summary>
    /// The next step after <paramref name="step"/> that is still left to try; null at the end of the ladder. The
    /// steps an answer from the server would judge (the certificate to the removed device) are not offered after a
    /// failure that never reached a server - there is nothing for the person to try about an account then.
    /// </summary>
    public ExchangeDiagnosisStep? NextAfter(ExchangeDiagnosisStep step) =>
        Steps.Where(item => item.Step > step
                && item.State is ExchangeDiagnosisState.CouldNotCheck or ExchangeDiagnosisState.NotReached
                && !(Row.Walked && item.Step is >= ExchangeDiagnosisStep.Certificate and <= ExchangeDiagnosisStep.DeviceRemoved))
            .Select(item => (ExchangeDiagnosisStep?)item.Step)
            .FirstOrDefault();

    /// <summary>
    /// The one line a diagnosis leaves in the session log (requirement 7): the row's code, the route, the first
    /// failed step and the walk, as codes only. Never an address, a port, a login, a name or a fingerprint.
    /// </summary>
    public string ToRedactedLine()
    {
        var line = new StringBuilder("exchange-diagnosis msg=").Append(Row.Code)
            .Append(";route=").Append(Route == ExchangeDiagnosisApplies.Network ? "network" : "internet")
            .Append(";fail=")
            .Append(Steps.FirstOrDefault(item => item.State == ExchangeDiagnosisState.Failed) is { } failed
                ? ExchangeDiagnosisSteps.IdOf(failed.Step) : "none")
            .Append(";walk=");
        var first = true;
        foreach (var item in Steps.Where(item => item.State != ExchangeDiagnosisState.NotApplicable))
        {
            line.Append(first ? string.Empty : ",").Append(ExchangeDiagnosisSteps.IdOf(item.Step)).Append(':')
                .Append(Word(item.State));
            first = false;
        }

        return line.ToString();
    }

    private static string Word(ExchangeDiagnosisState state) => state switch
    {
        ExchangeDiagnosisState.Passed => "passed",
        ExchangeDiagnosisState.Failed => "failed",
        ExchangeDiagnosisState.CouldNotCheck => "offered",
        ExchangeDiagnosisState.NotApplicable => "n/a",
        _ => "not-reached"
    };
}

/// <summary>
/// SP-0225: walks the ladder of <c>DEVICE-EXCHANGE</c> item CY for one failure and stops at the first step that does
/// not pass. A wire refusal proves the server answered, so the link steps before the one it names are passed on that
/// evidence and nothing is probed; a failure with no reason of its own on the wire (item CY: unreachable, unanswered,
/// tls-failed) is walked from its step to the end, probing what this PC can see.
/// </summary>
public static class ExchangeDiagnosisEngine
{
    // Steps 1 to 9 are what this PC can look at: the other end, the path, the protected connection. Steps 10 to 14
    // are judged by an answer from the server (a refusal that names them) and cannot be probed; the last is the
    // person's fallback. A connection that came up and was accepted proves steps 1 to 10, the certificate included.
    private const int LastProbedStep = (int)ExchangeDiagnosisStep.DeviceClock;
    private const int LastStepProvenByAConnection = (int)ExchangeDiagnosisStep.Certificate;

    public static async Task<ExchangeDiagnosisResult> RunAsync(ExchangeFailureRow row, string host, int port,
        IExchangeDiagnosisProbes probes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(probes);
        var route = ExchangeDiagnosisRoute.Of(host);
        if (row.Step is not { } own)
        {
            return new ExchangeDiagnosisResult(row, route, []);
        }

        var context = new ExchangeDiagnosisContext(host, port);
        var states = new List<ExchangeDiagnosisStepResult>();
        var stopped = false;
        foreach (var step in ExchangeDiagnosisSteps.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await StateOfAsync(step, own, row.Walked, route, stopped, context, probes, cancellationToken)
                .ConfigureAwait(false);
            states.Add(new ExchangeDiagnosisStepResult(step, state));
            stopped |= state == ExchangeDiagnosisState.Failed;
        }

        return new ExchangeDiagnosisResult(row, route, states);
    }

    private static async Task<ExchangeDiagnosisState> StateOfAsync(ExchangeDiagnosisStep step, ExchangeDiagnosisStep own,
        bool walked, ExchangeDiagnosisApplies route, bool stopped, ExchangeDiagnosisContext context,
        IExchangeDiagnosisProbes probes, CancellationToken cancellationToken)
    {
        if (!AppliesHere(step, route))
        {
            return ExchangeDiagnosisState.NotApplicable;
        }

        if (stopped)
        {
            return ExchangeDiagnosisState.NotReached;
        }

        if (step < own)
        {
            // The failure happened beyond this step. For a link step the answer or the connection that did come
            // proves it; a step of the account's judging was never put to the server.
            return (int)step <= LastStepProvenByAConnection
                ? ExchangeDiagnosisState.Passed : ExchangeDiagnosisState.NotReached;
        }

        if (!walked)
        {
            return step == own ? ExchangeDiagnosisState.Failed : ExchangeDiagnosisState.NotReached;
        }

        if ((int)step is > LastProbedStep and < (int)ExchangeDiagnosisStep.Operator)
        {
            return ExchangeDiagnosisState.NotReached;
        }

        return await probes.ProbeAsync(step, context, cancellationToken).ConfigureAwait(false) switch
        {
            ExchangeProbeOutcome.Passed => ExchangeDiagnosisState.Passed,
            ExchangeProbeOutcome.Failed => ExchangeDiagnosisState.Failed,
            _ => ExchangeDiagnosisState.CouldNotCheck
        };
    }

    // This product signs in only and never registers (SP-0221), so an invitation is never put to a server here and
    // its step does not belong to the setup (item CY: skipped, never shown as passed).
    private static bool AppliesHere(ExchangeDiagnosisStep step, ExchangeDiagnosisApplies route) =>
        step != ExchangeDiagnosisStep.Invitation
        && (ExchangeDiagnosisSteps.AppliesOf(step) == ExchangeDiagnosisApplies.Any
            || ExchangeDiagnosisSteps.AppliesOf(step) == route);
}
