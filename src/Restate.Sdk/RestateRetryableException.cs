namespace Restate.Sdk;

/// <summary>
///     Thrown to fail the current invocation attempt with a <em>retryable</em> error: Restate will
///     re-invoke the handler and replay the journal, per its configured invoker retry policy.
///     Optionally overrides the delay before the next retry attempt.
/// </summary>
/// <remarks>
///     This is the counterpart to <see cref="TerminalException" />: a terminal exception fails the
///     invocation permanently, whereas this one asks the runtime to retry. Throwing an ordinary
///     <see cref="Exception" /> is already treated as retryable; use this type only when you need to
///     set a specific status code or override the next-retry delay — mirroring shared-core's
///     <c>Error::with_next_retry_delay_override</c>. The delay override applies to the
///     <em>next</em> attempt only; bounding total attempts/duration remains the runtime's
///     responsibility (or use <c>ctx.Run(..., RetryPolicy)</c> for SDK-local bounded retries).
/// </remarks>
public sealed class RestateRetryableException : Exception
{
    /// <summary>Creates a retryable failure with an optional status code and next-retry delay override.</summary>
    public RestateRetryableException(string message, ushort code = 500, TimeSpan? nextRetryDelay = null)
        : base(message)
    {
        Code = code;
        NextRetryDelay = nextRetryDelay;
    }

    /// <summary>Creates a retryable failure wrapping an inner exception.</summary>
    public RestateRetryableException(string message, Exception innerException, ushort code = 500,
        TimeSpan? nextRetryDelay = null)
        : base(message, innerException)
    {
        Code = code;
        NextRetryDelay = nextRetryDelay;
    }

    /// <summary>HTTP-style status code reported to Restate (default 500).</summary>
    public ushort Code { get; }

    /// <summary>
    ///     Overrides the delay before the runtime's next retry attempt. <c>null</c> defers entirely
    ///     to the runtime's configured invoker retry policy.
    /// </summary>
    public TimeSpan? NextRetryDelay { get; }
}
