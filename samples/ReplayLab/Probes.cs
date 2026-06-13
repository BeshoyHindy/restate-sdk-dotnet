using System.Collections.Concurrent;
using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     The request shape every ReplayLab handler accepts. <see cref="ProbeId" /> threads a
///     caller-chosen correlation id through the handler, its Run closures, and (for the
///     awakeable scenario) the out-of-band mailbox, so a test — whether it reads the static
///     <see cref="ExecutionProbe" /> directly (in-process E2E) or through
///     <see cref="ProbeService" /> ingress (standalone sample) — can prove WHICH path actually
///     executed and HOW MANY times.
/// </summary>
public sealed record ProbeRequest(string ProbeId);

/// <summary>
///     Cross-attempt execution probe. Restate replays handler code on resume, so a handler that
///     suspended and resumed runs its top-level body more than once while each journaled side
///     effect (a <c>ctx.Run</c> closure) executes EXACTLY once. Two counters per probe make that
///     observable:
///     <list type="bullet">
///         <item>
///             <c>"attempt"</c> — incremented as the first statement of every handler body, so it
///             counts re-invocations. A scenario that genuinely suspended/replayed measures
///             <c>attempt &gt;= 2</c>; a scenario that never suspended measures <c>1</c> and the
///             test FAILS — a regression that skips an unexercised path cannot skate through.
///         </item>
///         <item>
///             <c>"run:{name}"</c> — incremented INSIDE a journaled Run closure. It stays at
///             <c>1</c> across any number of replays, proving the durable result survived replay
///             rather than being re-computed (the exactly-once side-effect assertion).
///         </item>
///     </list>
///     The store is a per-process static so the in-process E2E host shares it with the test
///     thread; a fresh <see cref="ProbeId" /> isolates each scenario.
/// </summary>
public static class ExecutionProbe
{
    // probeId -> (counterName -> count). Both levels are concurrent: replay re-execution and the
    // jittered fan-out of E3 increment from arbitrary threads.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> Counters = new();

    /// <summary>Atomically bumps <paramref name="counter" /> for <paramref name="probeId" />.</summary>
    public static void Increment(string probeId, string counter)
    {
        var perProbe = Counters.GetOrAdd(probeId, static _ => new ConcurrentDictionary<string, int>());
        perProbe.AddOrUpdate(counter, 1, static (_, existing) => existing + 1);
    }

    /// <summary>
    ///     Returns a point-in-time copy of every counter recorded for <paramref name="probeId" />
    ///     (empty if the probe was never touched). A copy — not the live map — so callers can read
    ///     it without racing concurrent increments.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Snapshot(string probeId)
    {
        return Counters.TryGetValue(probeId, out var perProbe)
            ? new Dictionary<string, int>(perProbe)
            : new Dictionary<string, int>();
    }
}

/// <summary>
///     Out-of-band mailbox for the awakeable scenario (E2). A handler cannot return its awakeable
///     ids through its own response while it is still parked on them, so it publishes them here
///     from inside a journaled Run; the driving test polls the mailbox to learn the ids, then
///     resolves the awakeables through ingress. Keyed by probeId for isolation.
/// </summary>
public static class AwakeableMailbox
{
    private static readonly ConcurrentDictionary<string, (string First, string Second)> Box = new();

    /// <summary>Publishes the two awakeable ids created by an <see cref="AwakeablePairService" /> invocation.</summary>
    public static void Publish(string probeId, string firstId, string secondId)
    {
        Box[probeId] = (firstId, secondId);
    }

    /// <summary>Reads the published ids, or <c>null</c> if the handler has not published yet.</summary>
    public static (string First, string Second)? TryRead(string probeId)
    {
        return Box.TryGetValue(probeId, out var ids) ? ids : null;
    }
}

/// <summary>
///     Exposes <see cref="ExecutionProbe" /> snapshots through ingress so the standalone
///     <c>samples/ReplayLab</c> exe is probe-readable by hand and by the integration-test smoke
///     script. The in-process E2E tests read the static directly (same process), so they do not
///     need this — it exists purely for out-of-process observability.
/// </summary>
[Service]
public sealed class ProbeService
{
    /// <summary>Returns the recorded counters for <paramref name="req" />'s probe id.</summary>
    [Handler]
    public Task<IReadOnlyDictionary<string, int>> Get(Context ctx, ProbeRequest req)
    {
        return Task.FromResult(ExecutionProbe.Snapshot(req.ProbeId));
    }
}
