using ReplayLab;
using Xunit;
using Xunit.Abstractions;

namespace Restate.Sdk.E2E;

/// <summary>
///     E2E coverage for the two NEW SDK features, driven through a REAL restate-server container's
///     ingress (same rig as <see cref="ReplayLabE2eTests" />: in-process ReplayLab endpoint, real
///     container with a 5s inactivity timeout as the suspension forcer):
///     <list type="number">
///         <item>
///             <b>E9 — implicit child cancellation.</b> A parent spawns request/response child Calls,
///             parks, and is cancelled through ingress; the SDK auto-cancels its resolved children.
///             The discriminating post-condition is read on the CHILD side via
///             <see cref="ChildCancelProbe" />: every child reaches <c>cancelled:{i}</c>, never
///             <c>completed:{i}</c>. A regression that drops the child-cancel leaves the children
///             running, so they record <c>completed</c> (or never leave <c>started</c>) and the test
///             fails.
///         </item>
///         <item>
///             <b>E10 — named (string-keyed) signals.</b> A handler parks on
///             <c>ctx.NamedSignal&lt;string&gt;("decision")</c> — with no traffic it suspends and would
///             HANG forever if the feature regressed — and resumes with the sender-supplied value only
///             when a matching named signal is delivered by another invocation's
///             <c>ctx.SendSignal</c>. The scenario asserts the durable answer round-trips the value;
///             the bounded timeout turns a regressed (never-completing) await into a fast failure
///             rather than a hang.
///         </item>
///     </list>
/// </summary>
[Collection(RestateContainerCollection.Name)]
public sealed class NewFeaturesE2eTests
{
    // Same budget as the ReplayLab suite: ample headroom for the server's retry/abort cadence while
    // still failing fast on a genuine hang (a regressed named-signal await that never completes).
    private const int ScenarioTimeoutMs = 180_000;

    private readonly RestateContainerFixture _rig;
    private readonly ITestOutputHelper _output;

    public NewFeaturesE2eTests(RestateContainerFixture rig, ITestOutputHelper output)
    {
        _rig = rig;
        _output = output;
    }

    /// <summary>
    ///     E9 — a parent that spawns two request/response child Calls and parks is cancelled; the SDK
    ///     fans out one implicit cancel signal per RESOLVED child, so BOTH children are cancelled (their
    ///     parked sleeps fault with 409) rather than running to completion.
    ///
    ///     The cancel is issued WHILE the parent is parked on its first attempt — within the server's
    ///     inactivity window, before it would suspend — so the inbound CANCEL is processed against the
    ///     live, fully-tracked Processing state (both children tracked AND their invocation-ids
    ///     resolved). The SDK cancels only already-resolved children on the unwinding terminal path, so
    ///     this live-parked timing is what makes the fan-out deterministic.
    ///
    ///     Flow: <c>SendReturningId</c> the parent (learn its invocation id) → wait only until both
    ///     children have STARTED and the parent is live (fast, ~1-2s) → cancel the parent immediately →
    ///     assert both children recorded <c>cancelled:{i}</c> and NEITHER recorded <c>completed:{i}</c>.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E9_ParentCancelled_ChildrenAutoCancelled()
    {
        var probeId = NewProbeId();

        // Fire-and-forget the parent and capture its invocation id (the cancel target).
        var parentId = await _rig.Ingress.SendReturningIdAsync(
            "ChildCancelLabService", "SpawnAndPark", new { probeId }, idempotencyKey: probeId);
        Assert.StartsWith("inv_", parentId);

        // Wait only until the parent is live and BOTH children have started — this proves the children
        // were reached (distinguishing "cancelled" from "never started") and that the parent issued all
        // its Calls. The children start within ~1-2s, comfortably inside the 5s inactivity window, so we
        // can cancel the parent while it is still parked on its FIRST (Processing) attempt.
        await PollUntil(() =>
                ChildCancelProbe.Has(probeId, "parent:parked")
                && ChildCancelProbe.Has(probeId, "started:0")
                && ChildCancelProbe.Has(probeId, "started:1"),
            TimeSpan.FromSeconds(30), "parent live and both children started");

        // Cancel the parent through the admin API (cancellation is an admin-plane op on this server),
        // while it is parked on its first attempt with both children tracked and resolved. The SDK's
        // inbound-CANCEL path emits one cancel SendSignal per resolved child (strictly before the
        // parent's 409 Output), which the server routes to each child.
        await _rig.CancelInvocationAsync(parentId);

        // The discriminating post-condition: BOTH children must reach the cancelled state. If the
        // implicit child-cancel regressed, the children keep sleeping (10 min) and this times out —
        // a fast, unambiguous failure rather than a silent pass.
        await PollUntil(() =>
                ChildCancelProbe.Has(probeId, "cancelled:0")
                && ChildCancelProbe.Has(probeId, "cancelled:1"),
            TimeSpan.FromSeconds(90), "both children cancelled");

        var marks = ChildCancelProbe.Snapshot(probeId);
        _output.WriteLine($"[E9] child-cancel marks for {probeId}: {string.Join(", ", marks.OrderBy(m => m))}");

        // Positive proof: both children were cancelled.
        Assert.Contains("cancelled:0", marks);
        Assert.Contains("cancelled:1", marks);
        // Negative proof: NEITHER child ran to completion — the cancel actually aborted them.
        Assert.DoesNotContain("completed:0", marks);
        Assert.DoesNotContain("completed:1", marks);
    }

    /// <summary>
    ///     E10 — a handler parks on <c>ctx.NamedSignal&lt;string&gt;("decision")</c> (suspends past the
    ///     inactivity timeout with no traffic) and resumes with the sender-supplied value only when a
    ///     separate invocation sends a matching named signal via <c>ctx.SendSignal</c>.
    ///
    ///     Flow: fire-and-forget <c>AwaitDecision</c> → read its published invocation id from the
    ///     mailbox → wait past the 5s inactivity timeout so it genuinely SUSPENDS parked on the named
    ///     signal → <c>Resolve</c> sends the named signal carrying the value → attach to the awaiter and
    ///     assert it returned <c>decided:{value}</c>. A regressed named-signal feature never completes
    ///     the await, so the final attach times out (the bounded scenario timeout turns the hang into a
    ///     fast failure).
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E10_NamedSignal_AwaitedHandlerResumesWithSentValue()
    {
        var probeId = NewProbeId();
        const string decision = "ship-it";

        // Fire-and-forget the awaiter so the test can drive the signal while it is parked.
        await _rig.Ingress.SendAsync(
            "NamedSignalLabService", "AwaitDecision", new { probeId }, idempotencyKey: probeId);

        // The awaiter publishes its OWN invocation id from a journaled Run; poll the mailbox for it.
        var targetId = await PollValue(
            () => NamedSignalMailbox.TryReadTarget(probeId), TimeSpan.FromSeconds(30),
            "named-signal target invocation id");
        Assert.StartsWith("inv_", targetId);

        // Wait past the 5s inactivity timeout so the awaiter genuinely SUSPENDS parked on the named
        // signal (proving the await is a real suspension point, not a busy-wait).
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Send the matching named signal carrying the value from a SEPARATE invocation.
        await _rig.Ingress.InvokeAsync<object?>(
            "NamedSignalLabService", "Resolve",
            new { targetInvocationId = targetId, value = decision });

        // Attach to the awaiter's durable result. This is the discriminating assertion: it returns
        // only because the named signal resolved the parked promise. A regression hangs here until the
        // scenario timeout fires.
        var result = await _rig.Ingress.InvokeAsync<string>(
            "NamedSignalLabService", "AwaitDecision", new { probeId }, idempotencyKey: probeId);
        Assert.Equal($"decided:{decision}", result.Value);

        var probe = SnapshotOrThrow(probeId, "E10");
        // A genuine suspend/resume on the named signal ⇒ the body re-ran at least once.
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
    }

    // ----- helpers -----

    private static string NewProbeId() => Guid.NewGuid().ToString("N");

    private IReadOnlyDictionary<string, int> SnapshotOrThrow(string probeId, string scenario)
    {
        var probe = ExecutionProbe.Snapshot(probeId);
        if (probe.Count == 0)
            _output.WriteLine($"[{scenario}] empty probe snapshot for {probeId}");
        else
            _output.WriteLine($"[{scenario}] probe {probeId}: {Describe(probe)}");
        return probe;
    }

    private static string Describe(IReadOnlyDictionary<string, int> probe) =>
        "{" + string.Join(", ", probe.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")) + "}";

    /// <summary>Polls a predicate until it holds or the timeout elapses (bounded, no spin).</summary>
    private static async Task PollUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
    }

    /// <summary>Polls <paramref name="probe" /> until it yields a non-null value or the timeout elapses.</summary>
    private static async Task<T> PollValue<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
    }
}
