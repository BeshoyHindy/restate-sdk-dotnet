namespace Restate.Sdk;

/// <summary>
///     Configures retry behavior for <see cref="IContext.Run{T}(string, Func{Task{T}}, RetryPolicy?)" /> side effects.
///     When a side effect throws a non-terminal exception, the SDK retries it locally
///     with exponential backoff according to this policy before propagating the failure.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Initial delay before the first retry attempt.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Factor by which the delay increases after each attempt.
    ///     For example, a factor of 2.0 doubles the delay each retry.
    /// </summary>
    public double ExponentiationFactor { get; init; } = 2.0;

    /// <summary>Maximum delay between retry attempts. The computed delay is capped at this value.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Maximum number of retry attempts before the failure is propagated.
    ///     <c>null</c> means unlimited attempts (bounded only by <see cref="MaxDuration" />).
    /// </summary>
    public int? MaxAttempts { get; init; }

    /// <summary>
    ///     Maximum total duration across all retry attempts.
    ///     <c>null</c> means unlimited duration (bounded only by <see cref="MaxAttempts" />).
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    ///     Default retry policy with exponential backoff: 100ms initial delay, 2x factor, 5s max delay, unlimited attempts.
    /// </summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    ///     No retries — the side effect failure is immediately propagated as a run completion failure.
    /// </summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 0 };

    /// <summary>
    ///     Creates a retry policy with a fixed number of attempts.
    /// </summary>
    public static RetryPolicy FixedAttempts(int maxAttempts) =>
        new() { MaxAttempts = maxAttempts };

    /// <summary>
    ///     Creates a retry policy limited by total duration.
    /// </summary>
    public static RetryPolicy WithMaxDuration(TimeSpan maxDuration) =>
        new() { MaxDuration = maxDuration };

    /// <summary>
    ///     Computes the delay for the given attempt number (0-based).
    /// </summary>
    internal TimeSpan GetDelay(int attempt)
    {
        var delayMs = InitialDelay.TotalMilliseconds * Math.Pow(ExponentiationFactor, attempt);
        delayMs = Math.Min(delayMs, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>
    ///     Returns <c>true</c> if the given attempt should be retried (0-based attempt count, elapsed time).
    /// </summary>
    internal bool ShouldRetry(int attemptsSoFar, TimeSpan elapsed)
    {
        if (MaxAttempts.HasValue && attemptsSoFar >= MaxAttempts.Value)
            return false;

        if (MaxDuration.HasValue && elapsed >= MaxDuration.Value)
            return false;

        return true;
    }
}
