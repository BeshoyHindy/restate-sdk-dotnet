namespace Restate.Sdk.Internal.StateMachine;

/// <summary>
///     Thrown when the replayed journal does not match the operation the handler is performing.
///     <see cref="InvocationHandler" /> reports it to the runtime as an ErrorMessage with code
///     <see cref="ErrorCode" /> and RETRY behavior.
///     Deliberately not a <see cref="TerminalException" /> — a mismatch is a failed attempt, not
///     an invocation outcome: the usual cause is a deploy that changed handler code under
///     in-flight invocations, and redeploying the previous code has to be able to recover them.
///     Keeping it out of the <see cref="TerminalException" /> hierarchy also means handler code
///     that catches terminal failures (a saga's compensation block, say) never swallows it.
/// </summary>
internal sealed class JournalMismatchException : Exception
{
    /// <summary>
    ///     ErrorMessage code for a journal mismatch, matching the shared core's
    ///     <c>JOURNAL_MISMATCH</c>; the server reports the same condition as <c>RT0016</c>.
    /// </summary>
    public const ushort ErrorCode = 570;

    public JournalMismatchException(int journalIndex, string message)
        : base(message)
    {
        JournalIndex = journalIndex;
    }

    /// <summary>
    ///     Journal index the mismatch was detected at, reported as the ErrorMessage's
    ///     <c>related_command_index</c>.
    /// </summary>
    public int JournalIndex { get; }
}
