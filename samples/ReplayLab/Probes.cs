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
///     Out-of-band observation board for the implicit child-cancellation scenario (E9). The cancelled
///     CHILD cannot return its outcome through its own response (it is aborted with a 409), so each
///     child records its lifecycle here — started, then either cancelled (expected) or completed
///     (regression) — keyed by the parent's probe id and the child ordinal. The parent records that
///     it parked. The E2E test reads this board to assert the discriminating post-condition: every
///     child reached <c>cancelled:{i}</c>, NOT <c>completed:{i}</c>. A regression that drops the
///     implicit child-cancel leaves the children running, so they record <c>completed:{i}</c> (or
///     never leave <c>started:{i}</c>), which the test rejects.
/// </summary>
public static class ChildCancelProbe
{
    // probeId -> set of lifecycle marks ("parent:parked", "started:{i}", "cancelled:{i}", "completed:{i}").
    // A concurrent set per probe: parent and N children mark from independent invocation threads.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Marks = new();

    private static void Mark(string probeId, string mark) =>
        Marks.GetOrAdd(probeId, static _ => new ConcurrentDictionary<string, byte>())[mark] = 0;

    /// <summary>Records that the parent issued its child Calls and is about to park.</summary>
    public static void MarkParentParked(string probeId) => Mark(probeId, "parent:parked");

    /// <summary>Records that child <paramref name="index" /> began running (distinguishes cancelled from never-reached).</summary>
    public static void MarkChildStarted(string probeId, int index) => Mark(probeId, $"started:{index}");

    /// <summary>Records that child <paramref name="index" /> was cancelled — the E9 success outcome.</summary>
    public static void MarkChildCancelled(string probeId, int index) => Mark(probeId, $"cancelled:{index}");

    /// <summary>Records that child <paramref name="index" /> ran to completion — the E9 regression outcome.</summary>
    public static void MarkChildCompleted(string probeId, int index) => Mark(probeId, $"completed:{index}");

    /// <summary>Returns whether a given lifecycle <paramref name="mark" /> has been recorded for the probe.</summary>
    public static bool Has(string probeId, string mark) =>
        Marks.TryGetValue(probeId, out var marks) && marks.ContainsKey(mark);

    /// <summary>Returns a point-in-time copy of every mark recorded for <paramref name="probeId" />.</summary>
    public static IReadOnlyCollection<string> Snapshot(string probeId) =>
        Marks.TryGetValue(probeId, out var marks) ? marks.Keys.ToArray() : Array.Empty<string>();
}

/// <summary>
///     Out-of-band mailbox for the named-signal scenario (E10). The awaiting handler cannot return
///     its own invocation id through its response while it is parked on the signal, so it publishes
///     the id here from a journaled Run; the driving test reads it to learn WHERE to send the named
///     signal. Keyed by probeId for isolation.
/// </summary>
public static class NamedSignalMailbox
{
    private static readonly ConcurrentDictionary<string, string> Targets = new();

    /// <summary>Publishes the awaiting invocation's id so the test can target the named signal at it.</summary>
    public static void PublishTarget(string probeId, string invocationId) => Targets[probeId] = invocationId;

    /// <summary>Reads the awaiting invocation's id, or <c>null</c> if it has not published yet.</summary>
    public static string? TryReadTarget(string probeId) =>
        Targets.TryGetValue(probeId, out var id) ? id : null;
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
