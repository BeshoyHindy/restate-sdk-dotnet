using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E7 — the single most common user-facing replay shape: a request/response Call that
///     suspends while the callee runs (B1's two-id model, plus B2/B3/B8). The target
///     <see cref="SlowEchoService.Echo" /> durably sleeps 6s &gt; the 5s inactivity timeout, so
///     THIS caller suspends parked on the call's <c>result_idx</c> completion. When Echo finishes,
///     the server-composed resume batch contains ONE replayed
///     <c>CallCommand{invocation_id_idx, result_idx}</c> plus BOTH notifications
///     (<c>CallInvocationIdCompletionNotification</c> + <c>CallCompletionNotification</c>) — the
///     two-ids-one-wire-command shape whose positional-index replay diverged pre-fix (B1: command
///     count ≠ completion-id count) and whose notification-inflated <c>known_entries</c> hung
///     pre-fix (B2). Post-fix the replayed command's two ids are honored by value and the result
///     notification resolves the call.
/// </summary>
[Service]
public sealed class CallAcrossSuspensionService
{
    /// <summary>Calls <see cref="SlowEchoService.Echo" /> and returns its reply, suspending while it runs.</summary>
    [Handler]
    public async Task<string> Relay(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Request/response Call: the caller parks on the result completion while Echo durably
        // sleeps 6s, then the resume batch replays the single CallCommand and both notifications.
        var reply = await ctx.Call<string>("SlowEchoService", "Echo", new SlowEchoRequest(req.ProbeId));

        return reply;
    }
}
