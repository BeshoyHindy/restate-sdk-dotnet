using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E9 — implicit child cancellation. A parent handler issues request/response child Calls and
///     parks; when the parent is cancelled (inbound CANCEL through ingress) the SDK auto-emits one
///     cancel SendSignal per RESOLVED child — the .NET twin of Rust's tracked_invocation_ids loop
///     (vm/mod.rs:445-476), gated on cancel_children_calls=true (the default, lib.rs:255-258). The
///     discriminating post-condition is observable on the CHILD side: each child parks on a long
///     durable sleep, and its parked await faults with a 409 cancellation when the parent's
///     auto-cancel signal lands. The child records that fault into the in-process
///     <see cref="ChildCancelProbe" />, so the E2E test can assert the children were actually
///     cancelled (not that they completed, and not that they were never reached) — a regression that
///     drops the child-cancel would leave the children running to completion (the probe records
///     "completed:{i}" instead of "cancelled:{i}", which the test rejects).
/// </summary>
[Service]
public sealed class ChildCancelLabService
{
    /// <summary>How many children the parent spawns. Two proves the loop emits one signal PER resolved child.</summary>
    public const int ChildCount = 2;

    /// <summary>
    ///     Spawns <see cref="ChildCount" /> request/response child Calls (each tracked for implicit
    ///     cancel), then AWAITS all their results on the SAME (first) attempt. The children each block
    ///     ~10 minutes, so awaiting parks the parent on the result completions while it is still
    ///     actively Processing and connected — the SDK cancels only children whose invocation-id has
    ///     already RESOLVED (it cannot suspend to fetch an unresolved id on the terminal unwind), and a
    ///     request/response Call resolves the child invocation-id eagerly, so by the time the parent is
    ///     parked both children are tracked AND resolved. The parent never returns on its own — the test
    ///     cancels it through the admin API WHILE it is parked on this first attempt (before the
    ///     inactivity timeout would suspend it), so the inbound CANCEL is processed against the live,
    ///     fully-tracked state and the SDK fans out one cancel per resolved child BEFORE the 409 Output.
    /// </summary>
    [Handler]
    public async Task<string> SpawnAndPark(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Mark that the parent has started (before any await) so the test can observe the parent is
        // live without waiting for a suspension. Recorded in-process, not journaled — purely a test
        // liveness signal, idempotent across any (unexpected) replay.
        ChildCancelProbe.MarkParentParked(req.ProbeId);

        // Request/response CallFuture children, keyed by ordinal so each child records its own outcome
        // against the parent's probe id. Each is tracked for implicit cancel at Call time; a Call also
        // resolves the child invocation-id eagerly (the precondition for the implicit cancel).
        var children = new IDurableFuture<string>[ChildCount];
        for (var i = 0; i < ChildCount; i++)
            children[i] = ctx.CallFuture<string>(
                "CancellableChildService", "Block", new ChildBlockRequest(req.ProbeId, i));

        // Await BOTH children on the FIRST attempt (never returns on its own — children block ~10 min).
        // The parent stays connected and parked here; the test cancels it within the inactivity window,
        // so CANCEL is delivered to the live Processing state with both children tracked AND resolved.
        // On the terminal unwind the SDK emits one child-cancel per resolved child BEFORE the 409 Output.
        await ctx.All(children);

        return "unreached";
    }
}

/// <summary>The request shape for a cancellable child: the parent's probe id plus the child's ordinal.</summary>
public sealed record ChildBlockRequest(string ProbeId, int Index);

/// <summary>
///     The child of <see cref="ChildCancelLabService" />. It parks on a long durable sleep and is
///     expected to be cancelled by the parent's implicit child-cancel — NOT to complete on its own.
///     The whole point of E9: when the parent is cancelled the SDK sends THIS invocation a cancel
///     signal, so the parked sleep throws a 409 <see cref="TerminalException" />. The child records
///     that outcome into <see cref="ChildCancelProbe" /> in a catch block, so the test can prove
///     "cancelled" vs the regression outcomes "completed" or "never-reached".
/// </summary>
[Service]
public sealed class CancellableChildService
{
    /// <summary>Parks ~10 minutes; records whether it was cancelled (expected) or completed (regression).</summary>
    [Handler]
    public async Task<string> Block(Context ctx, ChildBlockRequest req)
    {
        // The child began running — distinguishes "cancelled" from "never reached".
        ChildCancelProbe.MarkChildStarted(req.ProbeId, req.Index);
        try
        {
            // Park far longer than any test runs. The expected exit is the cancellation below, not a
            // timeout: if the parent's child-cancel regressed, this completes and records "completed".
            await ctx.Sleep(TimeSpan.FromMinutes(10));
            ChildCancelProbe.MarkChildCompleted(req.ProbeId, req.Index);
            return "completed";
        }
        catch (TerminalException ex) when (ex.Code == 409)
        {
            // The parent's auto-cancel signal aborted this child: the parked sleep unwinds with the
            // 409 "cancelled" terminal. This is the SUCCESS path for E9.
            ChildCancelProbe.MarkChildCancelled(req.ProbeId, req.Index);
            throw;
        }
    }
}
