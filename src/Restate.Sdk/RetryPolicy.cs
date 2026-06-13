namespace Restate.Sdk;

/// <summary>
///     Accumulated retry state for one <c>ctx.Run</c> retry loop — the .NET twin of shared-core's
///     <c>EntryRetryInfo { retry_count, retry_loop_duration }</c> (lib.rs:173-178). <see cref="RetryCount" />
///     is the number of attempts already made in this loop (cumulative across runtime re-drives via the
///     StartMessage seeds); <see cref="RetryLoopDuration" /> is the wall-clock time the loop has consumed.
/// </summary>
public readonly record struct EntryRetryInfo(int RetryCount, TimeSpan RetryLoopDuration)
{
    /// <summary>The zero seed — a fresh retry loop with no prior attempts.</summary>
    public static EntryRetryInfo Zero => new(0, TimeSpan.Zero);
}

/// <summary>
///     Configures retry behavior for <see cref="IContext.Run{T}(string, Func{Task{T}}, RetryPolicy?)" /> side effects.
///     When a side effect throws a non-terminal exception, the SDK consults this policy to decide whether to
///     retry — either locally with exponential backoff (a bounded fast path) or by letting the Restate runtime
///     re-drive the invocation (the durable, crash-safe path, used for the unbounded default).
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
    ///     Mirrors shared-core <c>RetryPolicy::None</c> (retries.rs:13-16, <c>next_retry</c> ⇒ DoNotRetry).
    /// </summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 0 };

    /// <summary>
    ///     Infinite retries — the .NET twin of shared-core's <c>RetryPolicy::Infinite</c> default
    ///     (retries.rs:6-12). A no-policy <c>ctx.Run</c> resolves to this, so a failing side effect is
    ///     re-driven by the runtime indefinitely (with the runtime's own backoff) rather than failing the
    ///     invocation. Both bounds are unset; the delay is left to the invoker, so <see cref="NextRetry" />
    ///     returns a retry with NO SDK-computed delay (the runtime supplies one).
    /// </summary>
    public static RetryPolicy Infinite { get; } = new() { InitialDelay = TimeSpan.Zero, MaxAttempts = null, MaxDuration = null };

    /// <summary>
    ///     The policy a <c>ctx.Run</c> with NO explicit policy resolves to (G17). Shared-core's
    ///     <c>RetryPolicy::default()</c> is <c>Infinite</c>, so a side effect with no policy is re-driven by
    ///     the runtime forever, never coerced to a single attempt.
    /// </summary>
    internal static RetryPolicy DefaultForRun => Infinite;

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
    ///     Creates a fixed-delay retry policy (G25): a constant <paramref name="interval" /> between
    ///     attempts (exponentiation factor 1.0). <paramref name="maxAttempts" />/<paramref name="maxDuration" />
    ///     bound the loop; both null ⇒ infinite. Mirrors shared-core <c>RetryPolicy::fixed_delay</c>.
    /// </summary>
    public static RetryPolicy FixedDelay(TimeSpan interval, int? maxAttempts = null, TimeSpan? maxDuration = null) =>
        new()
        {
            InitialDelay = interval,
            ExponentiationFactor = 1.0,
            MaxDelay = interval,
            MaxAttempts = maxAttempts,
            MaxDuration = maxDuration
        };

    /// <summary>
    ///     <c>true</c> when this policy places NO bound on attempts or duration — the infinite case, where
    ///     a bounded in-process loop would never terminate. Such a policy is served by runtime re-drive
    ///     rather than an in-process <see cref="System.Threading.Tasks.Task.Delay(TimeSpan)" /> loop.
    /// </summary>
    internal bool IsUnbounded => MaxAttempts is null && MaxDuration is null;

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

    /// <summary>
    ///     The shared-core <c>RetryPolicy::next_retry</c> analogue (retries.rs:110-151): given the
    ///     accumulated <paramref name="retryInfo" /> (count + duration already INCLUDING the just-failed
    ///     attempt), decide whether to retry and, when retrying, the SDK-computed delay before the next
    ///     attempt (or <c>null</c> to defer to the runtime's invoker policy — the Infinite case).
    ///
    ///     Bound check mirrors Rust exactly: give up when <c>max_attempts &lt;= retry_count</c> OR
    ///     <c>max_duration &lt;= retry_loop_duration</c>. Otherwise retry; the delay is the exponential
    ///     backoff for the NEXT attempt, capped at <see cref="MaxDelay" />.
    /// </summary>
    internal RetryDecision NextRetry(EntryRetryInfo retryInfo)
    {
        // Unbounded (no max attempts AND no max duration): always retry. The delay either defers to the
        // runtime (Retry(None) — the pure Infinite case, InitialDelay == Zero) or carries the SDK-computed
        // backoff so the runtime honors it on the redrive Error frame (G16, error.next_retry_delay).
        if (IsUnbounded)
            return RetryDecision.Retry(
                InitialDelay > TimeSpan.Zero ? GetDelay(Math.Max(0, retryInfo.RetryCount - 1)) : null);

        if (MaxAttempts is { } maxAttempts && maxAttempts <= retryInfo.RetryCount)
            return RetryDecision.DoNotRetry;
        if (MaxDuration is { } maxDuration && maxDuration <= retryInfo.RetryLoopDuration)
            return RetryDecision.DoNotRetry;

        // Bounded: retry with an SDK-computed exponential delay. retry_count is 1-based here (the just-
        // failed attempt is counted), so the backoff exponent is retry_count-1 — the same shape Rust uses
        // (initial_interval * factor^(retry_count-1)), capped at MaxDelay.
        return RetryDecision.Retry(GetDelay(Math.Max(0, retryInfo.RetryCount - 1)));
    }
}

/// <summary>
///     The shared-core <c>NextRetry</c> analogue (retries.rs:75-79): either retry (optionally with an
///     SDK-computed delay; <c>null</c> defers to the runtime) or give up and propose the terminal failure.
/// </summary>
internal readonly record struct RetryDecision(bool ShouldRetry, TimeSpan? Delay)
{
    public static RetryDecision Retry(TimeSpan? delay) => new(true, delay);
    public static RetryDecision DoNotRetry { get; } = new(false, null);
}
