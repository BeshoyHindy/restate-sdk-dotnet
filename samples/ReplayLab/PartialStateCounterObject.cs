using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E4 — a VirtualObject that writes one state key, suspends, then reads the written key and an
///     UNwritten key after resume (B7). Against a real server the resume arrives with COMPLETE
///     eager state, so this scenario's container discriminating power is B2/B8 (resume must
///     actually happen) plus state continuity across a genuine suspend/resume; the exact B7 paths
///     (replay-SetState cache rebuild, partial-state lazy fallthrough for the unwritten key) are
///     proven deterministically by the in-process P4 harness, which controls the eager-state map.
///     The unwritten key must NOT silently default WITHOUT first attempting a state lookup — the
///     pre-fix cache-conflation bug.
/// </summary>
[VirtualObject]
public sealed class PartialStateCounterObject
{
    /// <summary>The state keys this object reads and writes. A is written; B is intentionally never written.</summary>
    private static class StateKeys
    {
        public static readonly StateKey<string> A = new("a");
        public static readonly StateKey<string> B = new("b");
    }

    /// <summary>
    ///     Sets key A, suspends across an 8s sleep, then reads A (must survive from the eager
    ///     cache rebuilt on replay) and B (never written → must resolve to a legitimate absent
    ///     value, not a silent default skipping the state machinery).
    /// </summary>
    [Handler]
    public async Task<string> Mutate(ObjectContext ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        ctx.Set(StateKeys.A, $"a-{req.ProbeId}");

        // Suspension BETWEEN the Set and the Gets: the resume must rebuild the eager cache so the
        // subsequent Get(A) sees the written value.
        await ctx.Sleep(TimeSpan.FromSeconds(8));

        var a = await ctx.Get(StateKeys.A); // from the eager cache rebuilt on replay
        var b = await ctx.Get(StateKeys.B); // never written → must NOT silently default-without-lookup

        return $"{a}|{b ?? "<null>"}";
    }
}
