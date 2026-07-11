namespace Restate.Sdk.Internal.StateMachine;

internal enum InvocationState
{
    WaitingStart,
    Replaying,
    Processing,

    /// <summary>
    ///     A SuspensionMessage was written: the invocation is parked until the runtime
    ///     observes one of the awaited completions and re-invokes the service.
    ///     Terminal for this request — no Output/End/Error may follow.
    /// </summary>
    Suspended,
    Closed
}