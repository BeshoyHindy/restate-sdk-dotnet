using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     B8 — EOF-before-park suspension parity (blueprint §4.7, 1.5/2.5, mirrors
///     <c>third_party/sdk-shared-core/src/tests/suspensions.rs</c>).
///
///     The pre-revision design only evaluated the suspension condition when the PUMP read EOF, so
///     any request-response/Lambda delivery (EOF arrives BEFORE the handler parks) deadlocked. The
///     fixed model evaluates the condition at the PARK SITE too: <c>AwaitNotificationAsync</c> calls
///     <c>TrySuspendAsync</c> after registering the awaited id, so a Sleep/awakeable/DurableFuture/
///     send-handle issued AFTER EOF still suspends with exactly the awaited ids.
///
///     Every scenario asserts the wire-order invariant — no frame may follow a Suspension frame, and
///     the SuspensionMessage's waiting sets equal the AWAITED set (HitSuspensionPoint parity), never
///     every registered slot. Every wait is bounded so a regression that hangs fails in 5 s;
///     <c>[Fact(Timeout=10000)]</c> is defense-in-depth (xunit v2).
///
///     Driving paths: low-level scenarios drive <see cref="InvocationStateMachine" /> directly over
///     <see cref="ProtocolTestHarness.StateMachineRig" />; the suspension decision is observed by
///     awaiting the parked SM operation and reading the outbound frame stream. Full-stack scenarios
///     (leak / abort / wire-exclusivity) drive <see cref="Internal.InvocationHandler.HandleAsync" />.
/// </summary>
public class SuspensionTests
{
    // ---- Direct-rig EOF-AFTER-park (cases 1, 6) -------------------------------------------------

    /// <summary>
    ///     §4.7.1 — happy path (EOF-after-park): a parked Sleep, then EOF with no notification →
    ///     Suspension frame whose waiting_completions == [sleep id]; the parked op unwinds in &lt; 5 s.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ParkedSleep_ThenEof_SuspendsWithSleepId()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var sleep = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();
        // Let the SleepCommand flush and the await park before EOF.
        await Task.Yield();
        rig.CompleteInbound();

        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(sleep));
        await AwaitBounded(pump);

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
        Assert.Empty(msg.WaitingSignals);
        Assert.Empty(msg.WaitingNamedSignals);
    }

    /// <summary>
    ///     §4.7.6 — awakeable suspension: a parked awakeable, then EOF → Suspension frame with
    ///     waiting_signals == [17] (first user signal id, B4) and empty waiting_completions.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ParkedAwakeable_ThenEof_SuspendsWithSignal17()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", [0xAB, 0xCD], "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, signalId) = rig.StateMachine.Awakeable();
        Assert.Equal(17u, signalId);
        var park = rig.StateMachine
            .AwaitNotificationAsync(signalId, InvocationStateMachine.NotificationKind.Signal).AsTask();
        await Task.Yield();
        rig.CompleteInbound();

        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(park));
        await AwaitBounded(pump);

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Empty(msg.WaitingCompletions);
        Assert.Equal(new uint[] { 17 }, msg.WaitingSignals.ToArray());
    }

    // ---- Direct-rig EOF-BEFORE-park (cases 7, 8) ------------------------------------------------

    /// <summary>
    ///     §4.7.7 — EOF-before-park, completion (Sleep): EOF delivered immediately after the
    ///     known-entries batch; the handler then issues its FIRST Sleep → Suspension frame with
    ///     waiting_completions == [sleep id], bounded return. Pre-revision design: permanent hang.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofBeforePark_FirstSleep_Suspends()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // EOF lands BEFORE the handler parks (request-response / Lambda shape).
        rig.CompleteInbound();
        await AwaitBounded(pump);

        await Assert.ThrowsAsync<SuspendedException>(async () =>
            await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
    }

    /// <summary>
    ///     §4.7.7 — EOF-before-park, completion (lazy GetState): EOF before park; the handler then
    ///     issues GetState for a key absent from a PARTIAL eager map → a lazy GetState command is
    ///     emitted and the await suspends with that completion id.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofBeforePark_LazyGetState_Suspends()
    {
        using var rig = new StateMachineRig();
        // Partial state, key absent → GetState must round-trip (lazy command), which is a park point.
        rig.StateMachine.Initialize("inv-1", "key-1", 0, 1, eagerState: null, partialState: true);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        await Assert.ThrowsAsync<SuspendedException>(async () =>
            await AwaitBounded(rig.StateMachine.GetStateAsync<int>("missing", CancellationToken.None).AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.GetLazyStateCommand);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
    }

    /// <summary>
    ///     §4.7.7 — EOF-before-park, completion (Call): EOF before park; the handler then issues its
    ///     first Call → Suspension frame with waiting_completions containing the call's result id.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofBeforePark_FirstCall_Suspends()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        await Assert.ThrowsAsync<SuspendedException>(async () =>
            await AwaitBounded(rig.StateMachine
                .CallAsync<string>("Svc", null, "Handler", (object?)"x", CancellationToken.None).AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.CallCommand);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        // Call allocates TWO ids (invocation-id idx then result id); only the awaited RESULT id is in
        // the waiting set — the un-awaited invocation-id slot must NOT appear (awaiting_on parity).
        Assert.Single(msg.WaitingCompletions);
    }

    /// <summary>
    ///     §4.7.8 — EOF-before-park, signal: same shape with an awakeable → waiting_signals == [17].
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofBeforePark_Awakeable_SuspendsWithSignal17()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", [0x01], "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        var (_, signalId) = rig.StateMachine.Awakeable();
        await Assert.ThrowsAsync<SuspendedException>(async () =>
            await AwaitBounded(rig.StateMachine
                .AwaitNotificationAsync(signalId, InvocationStateMachine.NotificationKind.Signal).AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 17 }, msg.WaitingSignals.ToArray());
        Assert.Empty(msg.WaitingCompletions);
    }

    // ---- Race + no-premature/no-spurious (cases 4, 9, 11, 12) -----------------------------------

    /// <summary>
    ///     §4.7.4 — race: the completion notification and EOF arrive back-to-back → the invocation
    ///     completes normally (the await resolves to the value) and NO Suspension frame appears.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NotificationThenEof_CompletesNormally_NoSuspension()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var sleep = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();
        await Task.Yield();

        // Deliver the sleep completion, then EOF — the completion wins, so the await resolves cleanly.
        await rig.DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(1));
        await AwaitBounded(sleep);   // resolves, does not throw
        rig.CompleteInbound();
        await AwaitBounded(pump);

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
    }

    /// <summary>
    ///     §4.7.9 — EOF-while-computing (NO premature suspension): a Sleep future is created (its
    ///     command flushed) but NOT awaited; EOF arrives while the handler is still computing → no
    ///     Suspension frame yet (the awaiting set is empty — a PendingIds-style design over-reports
    ///     here and is the bug this guards). Awaiting the future THEN suspends with exactly its id.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofWhileComputing_UnawaitedTimer_NoSuspensionUntilAwaited()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // SleepFutureAsync journals + flushes the command but returns a resolve thunk — nobody is
        // parked yet, so the awaiting set is empty.
        var resolve = rig.StateMachine.SleepFutureAsync(TimeSpan.FromHours(1), CancellationToken.None);
        rig.CompleteInbound();
        await AwaitBounded(pump);

        // EOF has been processed and TrySuspendAsync ran from the pump — but with an EMPTY awaiting
        // set it must NOT have written a Suspension frame. Now the handler awaits the future → THIS
        // park is the suspension point.
        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(resolve().AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
    }

    /// <summary>
    ///     §4.7.10a — EOF-before-park via DurableFuture: a Timer future created before EOF, awaited
    ///     after through the DurableFuture.GetResult park-thunk path (2.10) → suspends with the
    ///     timer's id. (10b — send handle — lives in §4.5.7.)
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofBeforePark_DurableFutureAwaited_Suspends()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var resolve = rig.StateMachine.SleepFutureAsync(TimeSpan.FromHours(1), CancellationToken.None);
        var future = new Restate.Sdk.Internal.LazyTimerFuture(resolve);
        rig.CompleteInbound();
        await AwaitBounded(pump);

        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(future.GetResult().AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
    }

    /// <summary>
    ///     §4.7.11 — NO spurious suspension from un-awaited registrations: the handler does a Send
    ///     (its invocation-id id is registered in the manager but NEVER awaited), EOF arrives, then
    ///     the handler completes normally → Output + End, NO Suspension frame. A PendingIds-based
    ///     design fails here because the send's id is registered-but-not-awaited.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UnawaitedSendThenComplete_NoSuspension()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Send registers an invocation-id slot in the completion manager but nobody awaits it.
        _ = await AwaitBounded(
            rig.StateMachine.SendAsync("Svc", null, "Handler", (object?)"x", null, null, CancellationToken.None));
        rig.CompleteInbound();
        await AwaitBounded(pump);

        // The handler completes normally despite closed input — sys_write_output is not an await point.
        await AwaitBounded(rig.StateMachine.CompleteAsync("done", CancellationToken.None).AsTask());

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.OutputCommand);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.End);
    }

    /// <summary>
    ///     §4.7.12 — the waiting set is the AWAITED set only (HitSuspensionPoint parity): the handler
    ///     creates three futures (Sleep, Call, Timer) but awaits only the Call; EOF → the Suspension
    ///     frame lists exactly the Call's result id, not the Sleep/Timer ids.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task OnlyAwaitedFuture_AppearsInWaitingSet()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Issue three completable ops in order; each burns completion id(s) deterministically.
        var sleepResolve = rig.StateMachine.SleepFutureAsync(TimeSpan.FromHours(1), CancellationToken.None); // id 1
        var callResolve = rig.StateMachine.CallFutureAsync("Svc", null, "H", "x", CancellationToken.None);   // ids 2,3
        var timerResolve = rig.StateMachine.SleepFutureAsync(TimeSpan.FromHours(2), CancellationToken.None);  // id 4
        _ = sleepResolve;
        _ = timerResolve;

        rig.CompleteInbound();
        await AwaitBounded(pump);

        // Await ONLY the call result — the only id that may appear in the waiting set.
        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(callResolve().AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        // CallFuture allocates invocation-id idx (2) then result id (3); only the awaited result id
        // is reported.
        Assert.Equal(new uint[] { 3 }, msg.WaitingCompletions.ToArray());
        Assert.DoesNotContain(1u, msg.WaitingCompletions);
        Assert.DoesNotContain(4u, msg.WaitingCompletions);
    }

    // ---- Run guard (cases 5, 14, 15) ------------------------------------------------------------

    /// <summary>
    ///     §4.7.5 — Run guard: EOF arrives while a Run closure is mid-execution → no suspension until
    ///     the proposal is flushed (_executingRuns defers). When the closure finishes and proposes,
    ///     the Run-epilogue trigger fires; here no ack follows, so the handler suspends — the key
    ///     assertion is that EOF mid-run does NOT prematurely suspend and the run STILL executes.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EofDuringRunClosure_DefersSuspensionUntilProposalFlushed()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var closureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClosure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runTask = rig.StateMachine.RunAsync("step", async () =>
        {
            closureEntered.SetResult();
            await releaseClosure.Task.ConfigureAwait(false);
            return 7;
        }, CancellationToken.None).AsTask();

        await AwaitBounded(closureEntered.Task);
        // EOF lands WHILE the closure is mid-flight: _executingRuns > 0 must defer suspension.
        rig.CompleteInbound();
        await AwaitBounded(pump);   // pump's EOF TrySuspend sees _executingRuns > 0 and does not suspend

        // Release the closure; the Run epilogue proposes + the await now parks on the ack barrier.
        releaseClosure.SetResult();

        // The handler is still awaiting the run ack with closed input → it suspends (no ack delivered).
        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(runTask));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        // The run command + proposal were both written BEFORE the suspension — the run executed.
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.RunCommand);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.ProposeRunCompletion);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
    }

    /// <summary>
    ///     §4.7.14 — Run terminal failure with input closed (B10b failure direction, 1.7): the Run
    ///     closure throws TerminalException; EOF is already delivered. A saga-compensation flag must
    ///     NOT be set before the failure is durable — the attempt suspends (proposal flushed, then
    ///     Suspension), so the closure's exception NEVER surfaces by rethrow on this attempt.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task RunTerminalFailure_InputClosed_SuspendsWithoutRunningCompensation()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        var compensationRan = false;
        var runTask = rig.StateMachine.RunAsync<int>("step", () =>
            throw new TerminalException("boom"), CancellationToken.None).AsTask();

        // The failure travels via the proposal → notification path; with input closed there is no
        // notification, so the await unwinds with SuspendedException, NOT TerminalException — the
        // compensation (which would run only after a TerminalException) must not run.
        await Assert.ThrowsAsync<SuspendedException>(async () =>
        {
            try { await AwaitBounded(runTask); }
            catch (TerminalException) { compensationRan = true; throw; }
        });
        Assert.False(compensationRan);

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        // The failure proposal IS durably flushed before suspension.
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.ProposeRunCompletion);
        Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
    }

    /// <summary>
    ///     §4.7.15 — signal-only waiters + executing run: the handler parks on an awakeable while a
    ///     Run closure is mid-flight; EOF defers via _executingRuns; after the proposal flushes and
    ///     the run is acked, the only remaining awaited id is the awakeable → Suspension with
    ///     waiting_signals == [17], and the run's resolved completion id absent.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SignalWaiterWithExecutingRun_SuspendsWithSignalAfterProposal()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", [0x02], "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var closureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClosure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Run id is allocated first (id 1); the awakeable uses signal id 17.
        var runTask = rig.StateMachine.RunAsync("step", async () =>
        {
            closureEntered.SetResult();
            await releaseClosure.Task.ConfigureAwait(false);
            return 0;
        }, CancellationToken.None).AsTask();

        await AwaitBounded(closureEntered.Task);

        var (_, signalId) = rig.StateMachine.Awakeable();
        var awakeablePark = rig.StateMachine
            .AwaitNotificationAsync(signalId, InvocationStateMachine.NotificationKind.Signal).AsTask();

        // Buffer the run's ack (id 1) BEFORE closing input (delivery after EOF is impossible). EOF
        // then lands while the run is executing → deferred via _executingRuns. Releasing the closure
        // flushes the proposal; the Run epilogue then runs TrySuspendAsync (run-epilogue trigger
        // site) and, because input is closed and the awakeable (signal 17) is still parked, SUSPENDS.
        // Suspension latches both completion managers (FailAll), so EVERY parked waiter unwinds with
        // SuspendedException — including the run's own ack await. The run's id is resolved/buffered
        // and was never in the awaiting set at suspension time, so it is NOT in the waiting set; only
        // the awakeable's signal is (HitSuspensionPoint parity).
        await rig.DeliverAsync(MessageType.RunCompletion, CreateRunCompletion(1, ReadOnlyMemory<byte>.Empty));
        rig.CompleteInbound();
        await AwaitBounded(pump);
        releaseClosure.SetResult();

        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(runTask));
        await Assert.ThrowsAsync<SuspendedException>(async () => await AwaitBounded(awakeablePark));

        var outbound = await DrainOutboundAsync(rig);
        var frames = AssertFrameStream(outbound);
        // The run command + proposal were both written (the run EXECUTED before suspension).
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.RunCommand);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.ProposeRunCompletion);
        var suspension = Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 17 }, msg.WaitingSignals.ToArray());
        Assert.DoesNotContain(1u, msg.WaitingCompletions);
    }

    // ---- Full-stack: leak / abort / wire-exclusivity (cases 2, 3, 13) ---------------------------

    /// <summary>
    ///     §4.7.2 — leak regression: a WeakReference to the per-request scope object is dead after
    ///     HandleAsync returns (suspension) + GC. Pre-fix this test times out (the handler never
    ///     returns because the suspension never fires). The scope is captured by the sleeping
    ///     handler; once suspension unwinds it, GC must reclaim it.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SuspendedInvocation_DoesNotLeakScope()
    {
        var weak = await RunSleepingHandlerAndGetScopeRefAsync();

        for (var attempt = 0; attempt < 5 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weak.IsAlive, "Suspended handler scope was not reclaimed — suspension leaked a reference");
    }

    // No-inline so the local captured by the handler closure is not kept alive by this frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunSleepingHandlerAndGetScopeRefAsync()
    {
        var scope = new object();
        var weak = new WeakReference(scope);

        var harness = new FullStackHarness(SuspendingServices.SleepDef, () => new SuspendingServices(scope));
        // No completion is ever delivered after EOF → the handler must SUSPEND and return.
        await AwaitBounded(harness.RunUntilCompleteAsync(JsonSerializer.SerializeToUtf8Bytes("x")));

        var frames = AssertFrameOrder(harness.Response);
        Assert.Single(frames, frame => frame.Header.Type == MessageType.Suspension);
        return weak;
    }

    /// <summary>
    ///     §4.7.3 — abort path: cancelling the outer token while the handler is parked → HandleAsync
    ///     returns promptly and NO Suspension frame is written (the abort unwinds via faulted TCSs,
    ///     not via the suspension transition).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task AbortWhileParked_ReturnsPromptly_NoSuspensionFrame()
    {
        var cts = new CancellationTokenSource();
        var harness = new FullStackHarness(SuspendingServices.SleepDef, () => new SuspendingServices(new object()));

        // Drive the handler with NO EOF (input pipe stays open) so it parks on the sleep; then abort.
        var handlerTask = harness.RunOpenEndedAsync(JsonSerializer.SerializeToUtf8Bytes("x"), cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await AwaitBounded(handlerTask);
        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
    }

    /// <summary>
    ///     §4.7.13 — suspension-vs-failure wire exclusivity: a handler whose closure would throw a
    ///     non-terminal exception after waking, driven under EOF → whichever path wins, the stream
    ///     never contains a frame after a Suspension frame and never both a Suspension and an Error
    ///     frame. Driven full-stack so the InvocationHandler catch arms participate.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SuspensionVersusFailure_AreWireExclusive()
    {
        var harness = new FullStackHarness(SuspendingServices.SleepThenThrowDef,
            () => new SuspendingServices(new object()));
        await AwaitBounded(harness.RunUntilCompleteAsync(JsonSerializer.SerializeToUtf8Bytes("x")));

        var frames = AssertFrameOrder(harness.Response);   // enforces "no frame after Suspension"
        var hasSuspension = frames.Any(frame => frame.Header.Type == MessageType.Suspension);
        var hasError = frames.Any(frame => frame.Header.Type == MessageType.Error);
        Assert.False(hasSuspension && hasError, "Stream contains BOTH a Suspension and an Error frame");
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    ///     Drains every frame currently buffered on the rig's outbound pipe. The SM never completes
    ///     that writer (only Dispose does), so a 250 ms idle cancellation terminates the read once no
    ///     more buffered data is immediately available — within the 5 s watchdog, never a hang.
    /// </summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync(
        StateMachineRig rig)
    {
        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await reader.ReadMessageAsync(cts.Token).ConfigureAwait(false) is { } message)
            {
                frames.Add((message.Header, message.Payload.ToArray()));
                message.Dispose();
            }
        }
        catch (OperationCanceledException) { /* no more buffered frames — drain complete */ }

        return frames;
    }

    /// <summary>Runs the suite-wide wire-order assertion over a drained outbound frame list.</summary>
    private static IReadOnlyList<(MessageHeader Header, byte[] Payload)> AssertFrameStream(
        IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
    {
        for (var index = 0; index < frames.Count; index++)
        {
            var isLast = index == frames.Count - 1;
            if (frames[index].Header.Type == MessageType.Suspension)
                Assert.True(isLast, "A frame follows a Suspension frame; suspension must be terminal");
            if (frames[index].Header.Type == MessageType.End)
                Assert.True(isLast, "End is present but not the final frame");
        }

        return frames;
    }

    // ---- Full-stack harness ---------------------------------------------------------------------

    /// <summary>
    ///     Drives <see cref="Internal.InvocationHandler.HandleAsync" /> over a duplex pipe with a
    ///     hand-built <see cref="Restate.Sdk.Endpoint.ServiceDefinition" /> (no source generator) so
    ///     the handler's catch arms, suspension write, and reader teardown all participate. The
    ///     request body is a single Start + Input batch (known_entries = 1); two delivery shapes are
    ///     supported: <see cref="RunUntilCompleteAsync" /> closes the writer immediately (EOF after
    ///     the batch — the request-response shape), and <see cref="RunOpenEndedAsync" /> leaves it
    ///     open so the handler parks indefinitely until the abort token fires.
    /// </summary>
    private sealed class FullStackHarness
    {
        private readonly Restate.Sdk.Endpoint.ServiceDefinition _service;
        private readonly Func<object> _instanceFactory;
        private readonly System.IO.Pipelines.Pipe _request = new();
        private readonly System.IO.Pipelines.Pipe _response = new();

        public FullStackHarness(Restate.Sdk.Endpoint.ServiceDefinition service, Func<object> instanceFactory)
        {
            _service = service;
            _instanceFactory = instanceFactory;
        }

        /// <summary>The drained raw response stream — valid after the run task completes.</summary>
        public byte[] Response { get; private set; } = [];

        public Task RunUntilCompleteAsync(byte[] input) =>
            RunAsync(input, completeAfterBatch: true, CancellationToken.None);

        public Task RunOpenEndedAsync(byte[] input, CancellationToken ct) =>
            RunAsync(input, completeAfterBatch: false, ct);

        private async Task RunAsync(byte[] input, bool completeAfterBatch, CancellationToken ct)
        {
            var handler = new Restate.Sdk.Internal.InvocationHandler();
            var handlerDef = _service.Handlers[0];

            // Write the Start + Input batch (known_entries = 1: just the input entry).
            var start = CreateStartMessage(_service.Name, knownEntries: 1);
            var writer = new ProtocolWriter(_request.Writer);
            writer.WriteMessage(MessageType.Start, start.ToByteArray());
            writer.WriteMessage(MessageType.InputCommand, CreateInputCommand(input).ToByteArray());
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (completeAfterBatch) await _request.Writer.CompleteAsync().ConfigureAwait(false);

            var drain = DrainResponseAsync();
            await handler.HandleAsync(_request.Reader, _response.Writer, _service, handlerDef,
                new SingleInstanceProvider(_instanceFactory()), ct).ConfigureAwait(false);
            await _response.Writer.CompleteAsync().ConfigureAwait(false);
            Response = await drain.ConfigureAwait(false);

            if (!completeAfterBatch)
                try { await _request.Writer.CompleteAsync().ConfigureAwait(false); } catch { /* already torn */ }
        }

        private async Task<byte[]> DrainResponseAsync()
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var read = await _response.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var segment in read.Buffer) buffer.Write(segment.Span);
                _response.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted) break;
            }

            return buffer.ToArray();
        }
    }

    private sealed class SingleInstanceProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => instance;
    }

    /// <summary>
    ///     A non-generated service whose handlers exercise the suspension paths. The captured
    ///     <paramref name="scope" /> proves the per-request object is reclaimable after suspension
    ///     (leak regression, §4.7.2).
    /// </summary>
    private sealed class SuspendingServices(object scope)
    {
        // Build hand-rolled definitions so the full-stack tests need no source-generated registration.
        public static readonly Restate.Sdk.Endpoint.ServiceDefinition SleepDef =
            BuildService("SleepSvc", async (instance, ctx) =>
            {
                // Touch the captured scope so the handler frame holds it until suspension unwinds.
                GC.KeepAlive(((SuspendingServices)instance)._scope);
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        public static readonly Restate.Sdk.Endpoint.ServiceDefinition SleepThenThrowDef =
            BuildService("SleepThrowSvc", async (_, ctx) =>
            {
                // Park first; if a notification ever arrived the closure would then throw a
                // NON-terminal exception. Under EOF the park suspends and the throw is never reached —
                // but either ordering must stay wire-exclusive (no Error after Suspension).
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                throw new InvalidOperationException("non-terminal after wake");
            });

        private readonly object _scope = scope;

        private static Restate.Sdk.Endpoint.ServiceDefinition BuildService(
            string name, Func<object, Restate.Sdk.Context, Task<object?>> body) =>
            new()
            {
                Name = name,
                Type = Restate.Sdk.Endpoint.ServiceType.Service,
                // The handler resolves the instance via Factory(provider); our provider returns the
                // single per-request instance regardless of the requested type.
                Factory = provider => provider.GetService(typeof(SuspendingServices))!,
                Handlers = new[]
                {
                    new Restate.Sdk.Endpoint.HandlerDefinition
                    {
                        Name = "Run",
                        IsShared = false,
                        HasInput = true,
                        HasOutput = true,
                        InputDeserializer = data => JsonSerializer.Deserialize<string>(data.FirstSpan),
                        Invoker = (instance, ctx, _, _) => body(instance, ctx)
                    }
                }
            };
    }
}
