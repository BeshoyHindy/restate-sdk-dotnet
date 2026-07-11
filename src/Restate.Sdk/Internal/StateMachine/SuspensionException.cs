namespace Restate.Sdk.Internal.StateMachine;

/// <summary>
///     Control-flow exception used to suspend an invocation. When the input stream closes,
///     no completion can ever arrive, so every pending durable wait is poisoned with this
///     exception; it unwinds the handler at exactly the awaits it is blocked on, and
///     <see cref="InvocationHandler" /> converts it into a SuspensionMessage.
///     Deliberately not a <see cref="TerminalException" /> — suspension is not a failure.
/// </summary>
internal sealed class SuspensionException : Exception
{
    public SuspensionException()
        : base("The invocation is suspending: the input stream closed while a durable operation was pending.")
    {
    }
}
