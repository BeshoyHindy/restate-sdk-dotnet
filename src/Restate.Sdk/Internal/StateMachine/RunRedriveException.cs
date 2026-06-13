namespace Restate.Sdk.Internal.StateMachine;

/// <summary>
///     Control-flow sentinel for the HYBRID Run retry model (G14/G15/G16/G17). When a side effect fails
///     with a NON-terminal exception under an unbounded (Infinite) policy — or any policy that defers the
///     delay to the invoker — the SDK cannot loop forever in-process, so it unwinds the handler with this
///     signal instead of proposing a run completion. The state machine surfaces it as a RETRYABLE Error
///     frame (vm/transitions/journal.rs:756-766 <c>return Err(error)</c>), so the Restate RUNTIME re-drives
///     the invocation and the journal replays — the crash-safe, leader-change-surviving path.
///
///     <see cref="NextRetryDelay" /> is the policy-derived delay before the next attempt (G16); <c>null</c>
///     defers entirely to the runtime's configured invoker policy (the Infinite case, Retry(None)).
/// </summary>
internal sealed class RunRedriveException : Exception
{
    public RunRedriveException(string runName, string reason, TimeSpan? nextRetryDelay)
        : base($"Run '{runName}' failed and will be re-driven by the runtime: {reason}")
    {
        Reason = reason;
        NextRetryDelay = nextRetryDelay;
    }

    /// <summary>The underlying failure message (without the redrive framing).</summary>
    public string Reason { get; }

    /// <summary>
    ///     The SDK-computed delay before the runtime's next attempt, or <c>null</c> to defer to the
    ///     invoker's own retry policy.
    /// </summary>
    public TimeSpan? NextRetryDelay { get; }
}
