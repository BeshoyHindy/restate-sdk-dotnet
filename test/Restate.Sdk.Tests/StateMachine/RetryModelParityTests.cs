using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Retry-model parity (HYBRID) — closes shared-core v0.10.0 gaps G1/G2/G3/G14/G16/G17
///     (audit doc docs/research/shared-core/09-parity-audit.md):
///
///       * G2/G3 — <c>StartMessage.retry_count_since_last_stored_entry</c> (field 7) +
///         <c>duration_since_last_stored_entry</c> (field 8) are parsed and threaded into the SM.
///       * G1 — the FIRST run committed after replay seeds its retry loop from those fields
///         (<c>InferEntryRetryInfo</c> analogue), so MaxAttempts/MaxDuration are CUMULATIVE across
///         runtime re-drives; later runs in the same invocation seed from zero.
///       * G17 — a <c>ctx.Run</c> with NO policy defaults to Infinite: a non-terminal failure is
///         re-driven by the runtime (RunRedriveException → retryable Error frame) rather than
///         collapsing to a single attempt.
///       * G16 — the next-retry delay on the redrive frame is derived from the run's policy.
///       * G14 — RunFuture (detached fan-out) honors a RetryPolicy.
///
///     Pure-unit coverage of <see cref="RetryPolicy.NextRetry" /> + <see cref="EntryRetryInfo" />
///     pins the decision table; SM-driven scenarios prove the wire behavior end to end.
/// </summary>
public sealed class RetryModelParityTests
{
    private const int WatchdogMs = 10_000;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // A bounded policy with a near-zero backoff so the in-process fast path runs quickly.
    private static readonly RetryPolicy ThreeAttemptsFast = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(1)
    };

    // ---- G2/G3: StartMessage fields 7/8 parsed -----------------------------------------------

    [Fact]
    public void ParseStartMessage_ReadsRetryCountAndDurationFields()
    {
        var start = CreateStartMessage("inv-seed", 1,
            retryCountSinceLastStoredEntry: 4, durationSinceLastStoredEntryMillis: 7777);

        var fields = ProtobufCodec.ParseStartMessage(start.ToByteArray());

        Assert.Equal(4u, fields.RetryCountSinceLastStoredEntry);
        Assert.Equal(7777ul, fields.DurationSinceLastStoredEntryMillis);
    }

    [Fact]
    public void ParseStartMessage_OmittedRetryFields_DefaultToZero()
    {
        var start = CreateStartMessage("inv-fresh", 1);

        var fields = ProtobufCodec.ParseStartMessage(start.ToByteArray());

        Assert.Equal(0u, fields.RetryCountSinceLastStoredEntry);
        Assert.Equal(0ul, fields.DurationSinceLastStoredEntryMillis);
    }

    // ---- G17: no-policy Run defaults to Infinite (redrive, no SDK delay) ----------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task NoPolicyRun_NonTerminalFailure_RedrivesWithoutSdkDelay()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        var redrive = await Assert.ThrowsAsync<RunRedriveException>(() => AwaitBounded(
            sm.RunAsync<int>("no-policy", () =>
            {
                attempts++;
                throw new InvalidOperationException("transient");
            }, CancellationToken.None).AsTask()));

        // Infinite policy (InitialDelay == Zero) defers the delay to the runtime → null override.
        Assert.Null(redrive.NextRetryDelay);
        // The closure ran exactly ONCE — no in-process loop for the unbounded default; the runtime re-drives.
        Assert.Equal(1, attempts);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task NoPolicyRun_VoidOverload_NonTerminalFailure_Redrives()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var redrive = await Assert.ThrowsAsync<RunRedriveException>(() => AwaitBounded(
            sm.RunAsync("no-policy-void", () => throw new InvalidOperationException("transient"),
                CancellationToken.None).AsTask()));

        Assert.Null(redrive.NextRetryDelay);
        Assert.Contains("no-policy-void", redrive.Message, StringComparison.Ordinal);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- G16: unbounded-with-backoff policy redrives carrying the policy-computed delay --------

    [Fact(Timeout = WatchdogMs)]
    public async Task UnboundedBackoffPolicy_Redrives_WithPolicyComputedNextRetryDelay()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Unbounded (no MaxAttempts/MaxDuration) but with a positive InitialDelay → NextRetry computes
        // a concrete backoff that rides the redrive Error frame as next_retry_delay (G16). First failure
        // ⇒ retry_count 1 ⇒ GetDelay(0) = InitialDelay = 250ms.
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(250), MaxDelay = TimeSpan.FromSeconds(10) };

        var redrive = await Assert.ThrowsAsync<RunRedriveException>(() => AwaitBounded(
            sm.RunAsync<int>("backoff", () => throw new InvalidOperationException("transient"),
                CancellationToken.None, policy).AsTask()));

        Assert.Equal(TimeSpan.FromMilliseconds(250), redrive.NextRetryDelay);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task FailAsync_FromRedrivePolicyDelay_EmitsErrorWithNextRetryDelay()
    {
        // Proves the redrive delay reaches the wire: FailAsync (the InvocationHandler RunRedrive arm's
        // emitter) writes an ErrorMessage carrying next_retry_delay derived from the policy. FailAsync
        // flushes but never completes the outbound writer, so we read exactly the first frame (the Error)
        // with a bounded reader rather than ReadAllOutboundAsync (which blocks on writer close).
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-redrive", "", 0, 1);

        await rig.StateMachine.FailAsync(500, "Run 'backoff' will be re-driven",
            CancellationToken.None, TimeSpan.FromMilliseconds(250));

        var reader = new ProtocolReader(rig.OutboundReader);
        var message = await AwaitBounded(reader.ReadMessageAsync(CancellationToken.None).AsTask())
                      ?? throw new InvalidOperationException("No frame emitted");
        Assert.Equal(MessageType.Error, message.Header.Type);
        var error = Gen.ErrorMessage.Parser.ParseFrom(message.Payload);
        message.Dispose();
        Assert.True(error.HasNextRetryDelay);
        Assert.Equal(250ul, error.NextRetryDelay);
    }

    // ---- G1/G2/G3: cumulative accounting across a simulated re-drive --------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task FirstRunAfterRedrive_SeedsCumulativeRetryCount_ExhaustsSooner()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // Simulate the runtime re-driving a fresh batch after 2 prior failed attempts: StartMessage
        // carries retry_count_since_last_stored_entry = 2 (field 7). With a MaxAttempts=3 policy the
        // FIRST run resumes the count at 2, so a SINGLE in-process attempt (count → 3) exhausts.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-redrive", 1,
            retryCountSinceLastStoredEntry: 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        var runTask = sm.RunAsync<int>("seeded", () =>
        {
            attempts++;
            throw new InvalidOperationException("still failing");
        }, CancellationToken.None, ThreeAttemptsFast).AsTask();

        // Exhaustion proposes a terminal failure; the runtime acks it and ThrowIfFailure re-raises.
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletionFailure(1u, 500, "exhausted"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)500, ex.Code);
        // Seed 2 + one in-process attempt = 3 == MaxAttempts ⇒ exhausted after exactly ONE local attempt.
        Assert.Equal(1, attempts);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SecondRunInSameInvocation_SeedsFromZero_NotStartMessage()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // Field 7 seeds ONLY the first committed run (processing_first_entry). A first SUCCESSFUL run
        // clears the flag, so a second run that fails seeds from zero and gets the full attempt budget.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-two-runs", 1,
            retryCountSinceLastStoredEntry: 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // First run: succeeds (no retries), clears the first-entry seed flag. Completion id 1.
        var firstTask = sm.RunAsync<int>("first", () => Task.FromResult(7), CancellationToken.None).AsTask();
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletion(1u, Json(7)));
        Assert.Equal(7, await AwaitBounded(firstTask));

        // Second run fails non-terminally under MaxAttempts=3. Because the seed flag is cleared, it gets
        // the full local budget: 3 attempts before exhaustion (not 1). Completion id 2.
        var attempts = 0;
        var secondTask = sm.RunAsync<int>("second", () =>
        {
            attempts++;
            throw new InvalidOperationException("transient");
        }, CancellationToken.None, ThreeAttemptsFast).AsTask();
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletionFailure(2u, 500, "exhausted"));

        await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(secondTask));
        Assert.Equal(3, attempts);   // full budget — the StartMessage seed did NOT bleed into the 2nd run

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task NonRunCommandFirst_ConsumesSeed_LaterRunSeedsFromZero_GetsFullBudget()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // Parity fix: shared-core flips processing_first_entry = false on the FIRST journaled entry of ANY
        // kind during Processing (journal.rs:309/498/878), not only on a Run proposal. So when a NON-Run
        // command (SetState here) is journaled first, IT consumes the StartMessage retry seed; a Run that
        // runs LATER must seed from ZERO and therefore get the FULL local attempt budget — NOT the reduced
        // budget the StartMessage retry_count would imply. Field 7 = 2, field 8 = 5000ms; if the seed
        // wrongly bled into the Run (count 2 + 1 local = 3 == MaxAttempts) it would exhaust after a SINGLE
        // local attempt. Correct behavior: the SetState already cleared the flag, so the Run runs its full
        // 3-attempt budget.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-nonrun-first", 1,
            retryCountSinceLastStoredEntry: 2, durationSinceLastStoredEntryMillis: 5000));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // FIRST journaled command is a NON-Run command (SetState) — it consumes the first-entry seed.
        // SetState is non-completable, so it does NOT allocate a completion id; the Run below is the first
        // completable and gets completion id 1.
        sm.SetState("k", 1);

        // Now a failing Run under MaxAttempts=3. Because the seed was already consumed by the SetState, the
        // Run seeds from zero and runs the FULL 3-attempt budget before exhausting (not 1).
        var attempts = 0;
        var runTask = sm.RunAsync<int>("after-set", () =>
        {
            attempts++;
            throw new InvalidOperationException("still failing");
        }, CancellationToken.None, ThreeAttemptsFast).AsTask();
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletionFailure(1u, 500, "exhausted"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)500, ex.Code);
        Assert.Equal(3, attempts);   // full budget — the non-Run entry consumed the seed, the Run started at zero

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- G14: RunFuture honors a RetryPolicy --------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task RunFuture_WithBoundedPolicy_RetriesInProcess_ThenProposesTerminalFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        // Detached fan-out run under a bounded policy: it retries in-process up to MaxAttempts then
        // proposes a terminal failure. The runtime acks only AFTER it receives the proposal, so we must
        // wait for the ProposeRunCompletion{failure} frame to be flushed (proof the in-process loop
        // exhausted its 3-attempt budget) BEFORE delivering the ack — delivering it early would resolve
        // the future against a still-running loop and race the attempt count.
        var resolve = sm.RunFutureAsync<int>("fanout", () =>
        {
            attempts++;
            throw new InvalidOperationException("transient");
        }, CancellationToken.None, ThreeAttemptsFast);

        var proposal = await AwaitProposeRunFailureAsync(rig);
        Assert.Equal(500u, proposal.Failure.Code);
        Assert.Equal(3, attempts);   // in-process fast path ran the full bounded budget before proposing

        // Now the runtime acks the proposal; the future re-raises the terminal failure (the same surface
        // the public LazyRunFuture exposes via ThrowIfFailure).
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletionFailure(1u, 500, "exhausted"));
        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(resolve().AsTask()));
        Assert.Equal((ushort)500, ex.Code);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RunFuture_NoPolicy_NonTerminalFailure_SurfacesRetryableFailureOnFuture()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // A DETACHED run with no policy (Infinite) that fails non-terminally cannot unwind the handler to
        // ask the runtime to re-drive (it runs off-stack), so the redrive degrades to a per-future failure:
        // the future surfaces a retryable TerminalException (code 500) carrying the failure reason. The
        // blocking ctx.Run is the canonical redrive path (covered above).
        var resolve = sm.RunFutureAsync<int>("fanout-redrive",
            () => throw new InvalidOperationException("transient"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(resolve().AsTask()));
        Assert.Equal((ushort)500, ex.Code);
        Assert.Contains("transient", ex.Message, StringComparison.Ordinal);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- G27: the run closure's IRunContext surfaces EntryRetryInfo ---------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task RunContext_FirstAttempt_SeesZeroRetryInfo()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ctx = new DefaultContext(sm, NullLogger.Instance, CancellationToken.None);

        // The IRunContext handed to the closure exposes the current attempt's retry accounting. On a
        // FRESH first attempt (no runtime re-drive) it is EntryRetryInfo.Zero — count 0, duration 0.
        EntryRetryInfo observed = default;
        var runTask = ctx.Run("observe-retry", ctxRun =>
        {
            observed = ctxRun.RetryInfo;
            return Task.FromResult(7);
        }).AsTask();
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletion(1u, Json(7)));
        Assert.Equal(7, await AwaitBounded(runTask));

        Assert.Equal(0, observed.RetryCount);
        Assert.Equal(TimeSpan.Zero, observed.RetryLoopDuration);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RunContext_FirstRunAfterRedrive_SeesSeededRetryCount()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // Simulate the runtime re-driving after 3 prior failed attempts: StartMessage field 7 carries
        // retry_count_since_last_stored_entry = 3 (field 8 = 1500ms). The FIRST committed run resumes the
        // cumulative seed, so its IRunContext.RetryInfo reports count 3 (the InferEntryRetryInfo seed),
        // letting a backoff-aware closure observe how many times it has already been retried.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-ctx-redrive", 1,
            retryCountSinceLastStoredEntry: 3, durationSinceLastStoredEntryMillis: 1500));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ctx = new DefaultContext(sm, NullLogger.Instance, CancellationToken.None);

        EntryRetryInfo observed = default;
        var runTask = ctx.Run("seeded-observe", ctxRun =>
        {
            observed = ctxRun.RetryInfo;
            return Task.FromResult(9);
        }).AsTask();
        await DeliverInboundAsync(rig, MessageType.RunCompletion, CreateRunCompletion(1u, Json(9)));
        Assert.Equal(9, await AwaitBounded(runTask));

        Assert.Equal(3, observed.RetryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), observed.RetryLoopDuration);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Pure-unit decision table for RetryPolicy.NextRetry + EntryRetryInfo -------------------

    [Fact]
    public void Infinite_NextRetry_AlwaysRetries_WithNoSdkDelay()
    {
        var decision = RetryPolicy.Infinite.NextRetry(new EntryRetryInfo(99, TimeSpan.FromHours(1)));
        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.Delay);
    }

    [Fact]
    public void None_NextRetry_NeverRetries()
    {
        var decision = RetryPolicy.None.NextRetry(EntryRetryInfo.Zero);
        Assert.False(decision.ShouldRetry);
    }

    [Fact]
    public void MaxAttempts_NextRetry_StopsWhenCountReached()
    {
        var policy = RetryPolicy.FixedAttempts(3);
        Assert.True(policy.NextRetry(new EntryRetryInfo(2, TimeSpan.Zero)).ShouldRetry);
        Assert.False(policy.NextRetry(new EntryRetryInfo(3, TimeSpan.Zero)).ShouldRetry);
    }

    [Fact]
    public void MaxDuration_NextRetry_StopsWhenDurationReached()
    {
        var policy = RetryPolicy.WithMaxDuration(TimeSpan.FromSeconds(5));
        Assert.True(policy.NextRetry(new EntryRetryInfo(1, TimeSpan.FromSeconds(4))).ShouldRetry);
        Assert.False(policy.NextRetry(new EntryRetryInfo(1, TimeSpan.FromSeconds(5))).ShouldRetry);
    }

    [Fact]
    public void Bounded_NextRetry_ComputesCappedExponentialDelay()
    {
        var policy = new RetryPolicy
        {
            MaxAttempts = 10,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            ExponentiationFactor = 2.0,
            MaxDelay = TimeSpan.FromMilliseconds(500)
        };
        // retry_count 1 ⇒ GetDelay(0) = 100ms; retry_count 4 ⇒ GetDelay(3) = 800ms capped at 500ms.
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.NextRetry(new EntryRetryInfo(1, TimeSpan.Zero)).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.NextRetry(new EntryRetryInfo(4, TimeSpan.Zero)).Delay);
    }

    [Fact]
    public void FixedDelay_Factory_ProducesConstantInterval()
    {
        var policy = RetryPolicy.FixedDelay(TimeSpan.FromMilliseconds(200), maxAttempts: 5);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.NextRetry(new EntryRetryInfo(1, TimeSpan.Zero)).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.NextRetry(new EntryRetryInfo(3, TimeSpan.Zero)).Delay);
        Assert.False(policy.NextRetry(new EntryRetryInfo(5, TimeSpan.Zero)).ShouldRetry);
    }

    [Fact]
    public void UnboundedWithBackoff_NextRetry_ReturnsComputedDelay()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(250) };
        var decision = policy.NextRetry(new EntryRetryInfo(1, TimeSpan.FromHours(1)));
        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromMilliseconds(250), decision.Delay);
    }

    [Fact]
    public void EntryRetryInfo_Zero_IsEmptyLoop()
    {
        Assert.Equal(0, EntryRetryInfo.Zero.RetryCount);
        Assert.Equal(TimeSpan.Zero, EntryRetryInfo.Zero.RetryLoopDuration);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task StartFreshAsync(StateMachineRig rig)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-retry", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    private static async Task DeliverInboundAsync(StateMachineRig rig, MessageType type,
        Google.Protobuf.IMessage message)
    {
        var writer = new ProtocolWriter(rig.InboundWriter);
        writer.WriteMessage(type, message.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Polls the outbound stream until the detached run flushes its ProposeRunCompletion{failure}
    ///     frame (the proof the in-process retry loop exhausted). Bounded by the harness watchdog so a
    ///     regression that never proposes fails the test instead of hanging.
    /// </summary>
    private static async Task<Gen.ProposeRunCompletionMessage> AwaitProposeRunFailureAsync(StateMachineRig rig)
    {
        await rig.StateMachine.FlushPendingAsync(CancellationToken.None);
        var reader = new ProtocolReader(rig.OutboundReader);
        using var cts = new CancellationTokenSource(WatchdogTimeout);
        while (await reader.ReadMessageAsync(cts.Token).ConfigureAwait(false) is { } message)
        {
            var isProposal = message.Header.Type == MessageType.ProposeRunCompletion;
            var payload = message.Payload.ToArray();
            message.Dispose();
            if (!isProposal) continue;

            var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(payload);
            if (proposal.ResultCase == Gen.ProposeRunCompletionMessage.ResultOneofCase.Failure)
                return proposal;
        }

        throw new InvalidOperationException("No ProposeRunCompletion failure frame was emitted");
    }
}
