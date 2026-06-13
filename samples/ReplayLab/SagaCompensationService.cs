using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E5 — a failed durable Run whose failure must RE-RAISE on replay (B10b), plus B1's failure
///     direction. Attempt 1 runs the "book" closure once, the server acks the failure proposal,
///     the catch is entered, and the post-catch 8s sleep suspends the invocation AFTER
///     <c>RunCommand{book}</c> + <c>RunFailureNotification</c> (+ <c>SleepCommand</c>) are
///     journaled. Attempt 2's resume batch REPLAYS the failed Run — and only reaches
///     <c>"compensated"</c> if the replayed journal entry re-raises the <see cref="TerminalException" />.
///     Pre-fix the replayed Run produced an empty-success journal entry, so attempt 2 returned
///     <c>"booked"</c> or JsonException-looped — the exact B10b path. The "book" closure carries
///     no <see cref="RetryPolicy" />: a <see cref="TerminalException" /> is a business failure and
///     is never retried, so it fails the Run on the first throw.
/// </summary>
[Service]
public sealed class SagaCompensationService
{
    /// <summary>
    ///     Attempts a booking that always fails terminally, then compensates. The compensation is
    ///     reached on attempt 2 only because the replayed failed Run re-raises its journaled failure.
    /// </summary>
    [Handler]
    public async Task<string> Book(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        try
        {
            // void Run overload, NO RetryPolicy — TerminalException is a business failure and is
            // never retried, so this Run fails on the first throw and journals a failure entry.
            // Func<Task> signature is explicit (the throw-only body is otherwise overload-ambiguous).
            await ctx.Run("book", Task () =>
            {
                ExecutionProbe.Increment(req.ProbeId, "run:book");
                throw new TerminalException("no rooms", 500);
            });

            return "booked";
        }
        catch (TerminalException)
        {
            // FIRST statement of the catch is the discriminator. The 8s sleep (> 5s inactivity)
            // forces a genuine suspension AFTER the failed RunCommand{book} + RunFailureNotification
            // (+ SleepCommand) are journaled, so the resume batch REPLAYS the failed Run and must
            // re-raise to reach this catch again.
            await ctx.Sleep(TimeSpan.FromSeconds(8));

            await ctx.RunAsync("compensate", () =>
            {
                ExecutionProbe.Increment(req.ProbeId, "run:compensate");
                return Task.FromResult(true);
            }).GetResult();

            return "compensated";
        }
    }
}
