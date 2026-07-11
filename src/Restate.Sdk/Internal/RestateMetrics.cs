using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Restate.Sdk.Internal;

/// <summary>
///     Terminal outcome of an invocation attempt, recorded as the <c>outcome</c> metric tag.
/// </summary>
internal enum InvocationOutcome
{
    /// <summary>The handler completed and its output was journaled.</summary>
    Success,

    /// <summary>The handler failed with a non-retryable <see cref="TerminalException" />.</summary>
    TerminalError,

    /// <summary>The invocation attempt failed with a retryable error.</summary>
    Error,

    /// <summary>The invocation attempt was cancelled by the caller.</summary>
    Cancelled,

    /// <summary>
    ///     The invocation suspended waiting on pending completions or signals
    ///     (a SuspensionMessage terminated the attempt; the runtime resumes it later).
    /// </summary>
    Suspended
}

/// <summary>
///     Static <see cref="System.Diagnostics.Metrics.Meter" /> for the SDK, mirroring the static
///     <c>Restate.Sdk</c> ActivitySource so metrics work without dependency injection
///     (e.g. on AWS Lambda). Instruments are recorded once per invocation attempt —
///     never per journal operation — with stack-allocated <see cref="TagList" /> tag sets.
/// </summary>
internal static class RestateMetrics
{
    /// <summary>Name of the SDK meter, matching the ActivitySource name.</summary>
    public const string MeterName = "Restate.Sdk";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Invocations = Meter.CreateCounter<long>(
        "restate.sdk.invocations",
        "{invocation}",
        "Completed invocation attempts, tagged by service, handler, and outcome.");

    private static readonly Histogram<double> InvocationDuration = Meter.CreateHistogram<double>(
        "restate.sdk.invocation.duration",
        "s",
        "Duration of invocation attempts in seconds, tagged by service, handler, and outcome.");

    private static readonly Histogram<long> ReplayedCommands = Meter.CreateHistogram<long>(
        "restate.sdk.journal.replayed_commands",
        "{command}",
        "Journal commands replayed per invocation attempt, including the input command.");

    /// <summary>
    ///     Records all invocation instruments for a single completed invocation attempt.
    /// </summary>
    public static void RecordInvocation(
        string service, string handler, InvocationOutcome outcome, TimeSpan duration, long replayedCommands)
    {
        if (Invocations.Enabled || InvocationDuration.Enabled)
        {
            var tags = new TagList
            {
                { "restate.service", service },
                { "restate.handler", handler },
                { "outcome", OutcomeName(outcome) }
            };

            Invocations.Add(1, tags);
            InvocationDuration.Record(duration.TotalSeconds, tags);
        }

        if (ReplayedCommands.Enabled)
        {
            var replayTags = new TagList
            {
                { "restate.service", service },
                { "restate.handler", handler }
            };

            ReplayedCommands.Record(replayedCommands, replayTags);
        }
    }

    private static string OutcomeName(InvocationOutcome outcome)
    {
        return outcome switch
        {
            InvocationOutcome.Success => "success",
            InvocationOutcome.TerminalError => "terminal_error",
            InvocationOutcome.Cancelled => "cancelled",
            InvocationOutcome.Suspended => "suspended",
            _ => "error"
        };
    }
}
