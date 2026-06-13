using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ReplayLab;
using Xunit;
using Xunit.Abstractions;

namespace Restate.Sdk.E2E;

/// <summary>
///     The merge-gate E2E suite: every ReplayLab service driven through a REAL restate-server
///     container's ingress, each scenario forcing a genuine suspend → resume (the container's 5s
///     inactivity timeout closes the input on any handler parked &gt;= 5s) and asserting BOTH the
///     durable post-condition AND that the faulty replay path actually ran — the latter via the
///     <see cref="ExecutionProbe" /> attempt counter (>= 2 ⇒ the handler body re-executed on a real
///     resume) and the per-Run counters (== 1 ⇒ each journaled side effect ran exactly once across
///     the two attempts). A scenario that never suspended measures attempt == 1 and FAILS, so a
///     regression that skips an unexercised path cannot skate through.
/// </summary>
[Collection(RestateContainerCollection.Name)]
public sealed class ReplayLabE2eTests
{
    // Every scenario forces an 8s suspend + resume; 180s leaves ample headroom for the server's
    // retry/abort cadence while still failing fast on a genuine hang (a pre-fix B8 no-suspend).
    private const int ScenarioTimeoutMs = 180_000;

    private readonly RestateContainerFixture _rig;
    private readonly ITestOutputHelper _output;

    public ReplayLabE2eTests(RestateContainerFixture rig, ITestOutputHelper output)
    {
        _rig = rig;
        _output = output;
    }

    /// <summary>
    ///     E1 — Run → Sleep → Run (B1/B2/B3/B8). The 8s sleep forces a suspend whose resume batch is
    ///     <c>RunCommand{a}</c> + <c>RunCompletionNotification{a}</c> + <c>SleepCommand</c> +
    ///     <c>SleepCompletionNotification</c>: the exact shape pre-fix mis-decoded (B1), hung on
    ///     inflated <c>known_entries</c> (B2), double-read the pipe (B3), or never suspended (B8).
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E1_RunSleepRun_SuspendsAndReplays_RunResultsSurvive()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<string>(
            "RunSleepRunService", "Execute", new { probeId }, idempotencyKey: probeId);

        // Two 32-hex run results joined by '|' — well-formed JSON string, NOT protobuf garbage.
        Assert.Matches("^[0-9a-f]{32}\\|[0-9a-f]{32}$", result.Value);

        var probe = SnapshotOrThrow(probeId, "E1");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
        // run "a" executed exactly once despite the handler body running twice — the durable result
        // survived the replay rather than being recomputed.
        Assert.Equal(1, probe.GetValueOrDefault("run:a"));
        Assert.Equal(1, probe.GetValueOrDefault("run:b"));
    }

    /// <summary>
    ///     E2 — two awakeables awaited out of creation order (B4/B8/B9). Resolving the SECOND
    ///     awakeable (signal id 18) first is the B4 trap: pre-fix signal index 1 aliased CANCEL.
    ///     The handler publishes both ids to the in-process mailbox; the test reads them, waits past
    ///     the inactivity timeout to force suspension, then resolves through ingress.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E2_AwakeablePair_ResolvedOutOfOrder_CompletesWithCorrectSignalIds()
    {
        var probeId = NewProbeId();

        // Fire-and-forget so the test can drive the wake-up while the handler is still parked.
        await _rig.Ingress.SendAsync("AwakeablePairService", "AwaitTwo", new { probeId }, idempotencyKey: probeId);

        // The handler publishes both awakeable ids from a journaled Run; poll the in-process mailbox.
        var (firstId, secondId) = await PollAsync(
            () => AwakeableMailbox.TryRead(probeId), TimeSpan.FromSeconds(30), "E2 awakeable ids");

        // The user-visible B4 proof at the ingress boundary: the trailing BE32 of each id decodes to
        // the signal id, and the first/second awakeables are signal 17/18 (never 0/1 == CANCEL).
        Assert.Equal(17u, DecodeSignalId(firstId));
        Assert.Equal(18u, DecodeSignalId(secondId));

        // Wait past the 5s inactivity timeout so the handler genuinely suspends parked on signal 18.
        await Task.Delay(TimeSpan.FromSeconds(8));
        await _rig.Ingress.ResolveAwakeableAsync(secondId, "two");

        // Wait again so the resume re-parks on signal 17 (second suspension), then resolve it.
        await Task.Delay(TimeSpan.FromSeconds(8));
        await _rig.Ingress.ResolveAwakeableAsync(firstId, "one");

        // Attach to the completed invocation and assert the durable answer.
        var result = await _rig.Ingress.InvokeAsync<string>(
            "AwakeablePairService", "AwaitTwo", new { probeId }, idempotencyKey: probeId);
        Assert.Equal("one+two", result.Value);

        var probe = SnapshotOrThrow(probeId, "E2");
        // Two suspensions ⇒ at least three handler attempts.
        Assert.True(probe.GetValueOrDefault("attempt") >= 3, $"attempt={Describe(probe)}");
        Assert.Equal(1, probe.GetValueOrDefault("run:publish"));
    }

    /// <summary>
    ///     E3 — 16-way jittered fan-out of Runs across a suspension (B5). Completion order diverges
    ///     from creation order on attempt 1; the resume replays all 16 RunCommands + notifications.
    ///     A correct replay honors completion ids, so the gathered array is exactly <c>[0..15]</c>.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E3_FanOutRuns_AcrossSuspension_NoCrossWiring()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<int[]>(
            "FanOutRunsService", "Scatter", new { probeId }, idempotencyKey: probeId);

        Assert.Equal(Enumerable.Range(0, 16).ToArray(), result.Value);

        var probe = SnapshotOrThrow(probeId, "E3");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
        for (var i = 0; i < 16; i++)
            Assert.Equal(1, probe.GetValueOrDefault($"run:part-{i}"));
    }

    /// <summary>
    ///     E4 — VirtualObject state continuity across a genuine suspend/resume (B2/B8 + state
    ///     continuity; the deterministic B7 lazy path is owned by in-process P4 per §2.4/§3.1). The
    ///     written key survives the resume; the unwritten key resolves to a legitimate absent value;
    ///     a fresh invocation on a new key gets its own value (object state intact).
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E4_PartialStateObject_StateSurvivesSuspension()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<string>(
            "PartialStateCounterObject", "Mutate", new { probeId },
            idempotencyKey: probeId, key: probeId);

        Assert.Equal($"a-{probeId}|<null>", result.Value);

        var probe = SnapshotOrThrow(probeId, "E4");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");

        // A follow-up Mutate on a DIFFERENT key returns its own value — proves per-key isolation and
        // that the object's state machinery (not a silent default) produced E4's answer.
        var otherProbe = NewProbeId();
        var second = await _rig.Ingress.InvokeAsync<string>(
            "PartialStateCounterObject", "Mutate", new { probeId = otherProbe },
            idempotencyKey: otherProbe, key: otherProbe);
        Assert.Equal($"a-{otherProbe}|<null>", second.Value);
    }

    /// <summary>
    ///     E5 — failed durable Run re-raised on replay, then compensation (B10b + B1 failure
    ///     direction). The post-catch 8s sleep forces a suspend AFTER the failed RunCommand +
    ///     RunFailureNotification are journaled, so the resume REPLAYS the failed Run; reaching
    ///     <c>"compensated"</c> proves the journaled failure re-raised <see cref="TerminalException" />.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E5_SagaCompensation_FailedRunReRaisesOnReplay()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<string>(
            "SagaCompensationService", "Book", new { probeId }, idempotencyKey: probeId);

        Assert.Equal("compensated", result.Value);

        var probe = SnapshotOrThrow(probeId, "E5");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
        // The "book" closure ran exactly once although the handler attempted twice — the replayed
        // failed Run re-raised from the journal rather than re-executing the closure.
        Assert.Equal(1, probe.GetValueOrDefault("run:book"));
        Assert.Equal(1, probe.GetValueOrDefault("run:compensate"));
    }

    /// <summary>
    ///     E6 — fire-and-forget Send whose invocation id resolves lazily after a suspend/resume (B6).
    ///     Post-fix the id comes from a replayed <c>CallInvocationIdCompletionNotification</c>, so it
    ///     is a structurally valid <c>inv_</c> id, not the protobuf garbage pre-fix UTF-8-decoded.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E6_LazySend_InvocationIdSurvivesSuspension()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<string>(
            "LazySendService", "SendAndReport", new { probeId }, idempotencyKey: probeId);

        Assert.Matches("^inv_[a-zA-Z0-9]", result.Value);

        var probe = SnapshotOrThrow(probeId, "E6");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
    }

    /// <summary>
    ///     E7 — request/response Call that suspends while the callee runs (B1's two-id model, plus
    ///     B2/B3/B8). The server-composed resume batch is ONE replayed
    ///     <c>CallCommand{invocation_id_idx, result_idx}</c> plus BOTH notifications — the
    ///     two-ids-one-wire-command shape that pre-fix diverged positionally. The reply is the
    ///     probe id round-tripped through Echo as a well-formed JSON string.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E7_CallAcrossSuspension_TwoIdModelHonored()
    {
        var probeId = NewProbeId();
        var result = await _rig.Ingress.InvokeAsync<string>(
            "CallAcrossSuspensionService", "Relay", new { probeId }, idempotencyKey: probeId);

        Assert.Equal(probeId, result.Value);

        var probe = SnapshotOrThrow(probeId, "E7");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");
    }

    /// <summary>
    ///     E8 — Workflow run handler parks on a durable promise until a shared handler resolves it
    ///     (Template A promise replay, plus B1/B2/B8). The run is started fire-and-forget, suspends
    ///     parked on the <c>GetPromiseCommand</c> completion, then <c>Approve</c> resolves the
    ///     promise through ingress; the run's resume replays the promise command + its completion.
    /// </summary>
    [DockerFact(Timeout = ScenarioTimeoutMs)]
    public async Task E8_ApprovalWorkflow_PromiseSuspendThenResolve()
    {
        var probeId = NewProbeId();

        // Workflow handlers reject an explicit idempotency-key — the workflow KEY itself is the
        // idempotency mechanism (the server returns 400 otherwise), so these calls pass only `key`.
        // Start the run keyed by probeId, fire-and-forget (workflow run handlers complete once).
        await _rig.Ingress.SendAsync(
            "ApprovalWorkflow", "Run", new { probeId }, key: probeId);

        // Let the run park on the promise past the inactivity timeout (genuine suspension).
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Resolve the promise from the shared handler.
        await _rig.Ingress.InvokeAsync<object?>(
            "ApprovalWorkflow", "Approve", new { probeId }, key: probeId);

        // Attach to the run's durable result. A workflow run completes once per key — re-invoking
        // Run returns 409 Conflict — so the result is read through the dedicated attach route.
        var result = await _rig.Ingress.AttachWorkflowAsync<string>("ApprovalWorkflow", probeId);
        Assert.Equal($"approved:{probeId}", result);

        var probe = SnapshotOrThrow(probeId, "E8");
        Assert.True(probe.GetValueOrDefault("attempt") >= 2, $"attempt={Describe(probe)}");

        // A second Approve must not crash the server (workflow promise single-resolution); per
        // §2.4/Open-Issue 9 this is relaxed to "no handler crash" — a terminal-by-design rejection
        // is acceptable, an unexpected 5xx is not.
        await AssertNoServerCrashAsync(() => _rig.Ingress.InvokeAsync<object?>(
            "ApprovalWorkflow", "Approve", new { probeId }, key: probeId));
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

    /// <summary>
    ///     Decodes the signal id from an awakeable id of the form
    ///     <c>"sign_1" + Base64UrlSafe(rawInvocationId + BigEndian32(signalId))</c>: strip the
    ///     prefix, base64url-decode, and read the trailing 4 bytes as a big-endian uint.
    /// </summary>
    private static uint DecodeSignalId(string awakeableId)
    {
        const string prefix = "sign_1";
        Assert.StartsWith(prefix, awakeableId);
        var decoded = Base64UrlDecode(awakeableId[prefix.Length..]);
        Assert.True(decoded.Length >= 4, $"awakeable id payload too short: {awakeableId}");
        return BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(decoded.Length - 4, 4));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
        return Convert.FromBase64String(normalized);
    }

    /// <summary>Polls <paramref name="probe" /> until it yields a value or the timeout elapses (bounded, no spin).</summary>
    private static async Task<T> PollAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : struct
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

    /// <summary>
    ///     Asserts the action does not produce a server-side 5xx. A terminal-by-design rejection
    ///     (4xx, surfaced as a handled error) is acceptable for the second promise resolution.
    /// </summary>
    private static async Task AssertNoServerCrashAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status < 500)
        {
            // 4xx — a terminal rejection by design; not a crash.
        }
    }
}
