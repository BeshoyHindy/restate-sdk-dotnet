using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plan 07 §1.2 round-1 residual-gap closure for <see cref="InvocationStateMachine" />. Every
///     scenario here drives a branch the §4 + 4b suites leave open in the post-GATE-3 coverage
///     snapshot, asserting a DISCRIMINATING outcome (never a coverage-only smoke):
///       * the blocking-Run TerminalException proposal+re-raise for the VOID overload
///         (<c>ExecuteAndProposeRunVoidAsync</c> catch arm — the typed overload is owned by §4);
///       * the local retry loop for BOTH Run overloads (<c>ExecuteWithRetryAsync</c>): a
///         non-terminal failure is retried with backoff, then exhaustion surfaces a
///         <see cref="TerminalException" /> via the proposal → notification → ThrowIfFailure path;
///       * the lazy <c>GetStateKeysAsync</c> roundtrip under partial state (H15/G10 — §4.6 only
///         exercises the eager/sorted keys path);
///       * the replay arms of <c>ClearAllState</c>, <c>RejectPromise</c> and <c>CompleteAsync</c>
///         (journaled ClearAllState / CompletePromise / Output dequeued during replay);
///       * the async <c>AwaitFlush</c> continuation (a flush that does NOT complete synchronously).
///     All waits flow through the 5 s harness watchdog so a regression that hangs fails fast.
/// </summary>
public sealed class CoverageEdgeTests
{
    private const int WatchdogMs = 10_000;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static readonly string[] ExpectedKeys = ["alpha", "beta"];

    // A retry policy that permits exactly one retry with a near-zero backoff, so the loop body
    // (GetDelay → Task.Delay → attempt++) executes once and then exhausts deterministically and fast.
    private static readonly RetryPolicy OneRetryFast = new()
    {
        MaxAttempts = 2,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(1)
    };

    // ---- Blocking Run: VOID overload, TerminalException → proposal → re-raise -------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task VoidRun_TerminalException_ProposesFailure_AndReRaisesFromNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // The VOID Run overload's closure throws a TerminalException. ExecuteAndProposeRunVoidAsync
        // must catch it, write ProposeRunCompletion{failure} (NOT rethrow the closure exception), and
        // park on the notification. We then deliver the runtime's failure ack so ThrowIfFailure
        // re-raises AFTER durability — the B10b contract for the void overload.
        var runTask = sm.RunAsync("void-term", () => throw new TerminalException("no rooms", 409),
            CancellationToken.None).AsTask();

        await DeliverInboundAsync(rig, MessageType.RunCompletion,
            CreateRunCompletionFailure(1u, 409, "no rooms"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)409, ex.Code);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        // Proof the FAILURE proposal (not an empty-success proposal) actually went on the wire.
        var frames = await DrainOutboundAsync(rig);
        var proposal = frames
            .Where(f => f.Header.Type == MessageType.ProposeRunCompletion)
            .Select(f => Gen.ProposeRunCompletionMessage.Parser.ParseFrom(f.Payload))
            .Single();
        Assert.NotNull(proposal.Failure);
        Assert.Equal(409u, proposal.Failure.Code);
    }

    // ---- Blocking Run: retry loop then exhaustion (TYPED overload) ------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task TypedRun_NonTerminalFailure_RetriesThenExhausts_ProposesTerminalFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        var runTask = sm.RunAsync<int>("typed-retry", () =>
        {
            attempts++;
            throw new InvalidOperationException("transient");   // non-terminal → retried
        }, CancellationToken.None, OneRetryFast).AsTask();

        // OneRetryFast allows attempt 1 (retry with 1ms backoff: GetDelay/Task.Delay/attempt++),
        // then exhausts on attempt 2 → ExecuteWithRetryAsync throws a TerminalException, which the
        // proposal catch turns into ProposeRunCompletion{failure}. Deliver the ack to re-raise.
        await DeliverInboundAsync(rig, MessageType.RunCompletion,
            CreateRunCompletionFailure(1u, 500, "exhausted"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)500, ex.Code);
        Assert.Equal(2, attempts);   // the closure ran twice: initial attempt + one retry

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Blocking Run: retry loop then exhaustion (VOID overload) -------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task VoidRun_NonTerminalFailure_RetriesThenExhausts_ProposesTerminalFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        var runTask = sm.RunAsync("void-retry", () =>
        {
            attempts++;
            throw new InvalidOperationException("transient");
        }, CancellationToken.None, OneRetryFast).AsTask();

        await DeliverInboundAsync(rig, MessageType.RunCompletion,
            CreateRunCompletionFailure(1u, 500, "exhausted"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)500, ex.Code);
        Assert.Equal(2, attempts);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Lazy GetStateKeys under partial state (H15 / G10) -------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task PartialState_GetStateKeys_EmitsLazyKeysCommand_AndResolvesFromNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // A PARTIAL fresh start with no eager keys: GetStateKeys cannot answer locally, so it must
        // emit GetLazyStateKeysCommand and await the runtime's keys notification — the §4.6 suite
        // only drives the eager (complete-map) keys path.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-lazy-keys", 1, partialState: true));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var keysTask = sm.GetStateKeysAsync(CancellationToken.None);

        // The lazy keys command's completion id is 1 (first allocated id). Resolve it with a keys
        // notification carrying the StateKeys oneof (field 17), which decodes to a JSON string[].
        var completion = new Gen.GetLazyStateKeysCompletionNotificationMessage
        {
            CompletionId = 1u,
            StateKeys = new Gen.StateKeys()
        };
        completion.StateKeys.Keys.Add(ByteString.CopyFromUtf8("alpha"));
        completion.StateKeys.Keys.Add(ByteString.CopyFromUtf8("beta"));
        await DeliverInboundAsync(rig, MessageType.GetLazyStateKeysCompletion, completion);

        var keys = await AwaitBounded(keysTask);
        Assert.Equal(ExpectedKeys, keys);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        // The lazy keys command — NOT an eager keys command — went on the wire.
        var frames = await DrainOutboundAsync(rig);
        Assert.Contains(frames, f => f.Header.Type == MessageType.GetLazyStateKeysCommand);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.GetEagerStateKeysCommand);
    }

    // ---- Replay arms: ClearAllState / RejectPromise / CompleteAsync ----------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task ClearAllState_DuringReplay_DequeuesJournaledCommand_NoNewWrite()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal a single ClearAllStateCommand; replaying ClearAllState must consume it (and flip
        // to Processing) rather than re-emit a command.
        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-clearall-replay", 2).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.ClearAllStateCommand, ProtobufCodec.CreateClearAllStateCommand().ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.True(sm.IsReplaying);

        sm.ClearAllState();
        Assert.False(sm.IsReplaying);   // the journaled command drained → replay complete

        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.ClearAllStateCommand);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RejectPromise_DuringReplay_DequeuesJournaledCommand_NoNewWrite()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal one CompletePromiseCommand (ResolvePromise/RejectPromise share the journal entry
        // type and burn id 1). Replaying RejectPromise validates the id and returns without writing.
        var journaled = ProtobufCodec.CreateCompletePromiseFailure("approval", 500, "denied", 1);
        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-reject-replay", 2).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.CompletePromiseCommand, journaled.ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.True(sm.IsReplaying);

        sm.RejectPromise("approval", "denied");
        Assert.False(sm.IsReplaying);

        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.CompletePromiseCommand);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CompleteAsync_DuringReplay_DequeuesJournaledOutput_AndEndsClean()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal an OutputCommand so CompleteAsync's replay arm pops it (SysWriteOutput) and SysEnd
        // succeeds with the queue drained — no commands left after Output, so no ProtocolException.
        var output = ProtobufCodec.CreateOutputCommand(Json("done").AsSpan());
        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-output-replay", 2).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.OutputCommand, output.ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.True(sm.IsReplaying);

        await sm.CompleteAsync("done", CancellationToken.None);
        Assert.Equal(InvocationState.Closed, sm.State);

        // Replay path re-emits nothing for the journaled Output; only End is written on close.
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.OutputCommand);
        Assert.Contains(frames, f => f.Header.Type == MessageType.End);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CompleteAsync_DuringReplay_WithCommandsAfterOutput_ThrowsNonDeterministic()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // An OutputCommand followed by ANOTHER journaled command means the journal recorded more than
        // the code produced — CompleteAsync's replay arm must reject it (terminal.rs:56-73 parity).
        var output = ProtobufCodec.CreateOutputCommand(Json("done").AsSpan());
        var trailing = ProtobufCodec.CreateSleepCommand(0, 1);
        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-output-extra", 3).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.OutputCommand, output.ToByteArray()),
            (MessageType.SleepCommand, trailing.ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.CompleteAsync("done", CancellationToken.None).AsTask());
        Assert.Contains("commands after Output", ex.Message);
    }

    // ---- Async AwaitFlush continuation (flush not synchronously complete) -----------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task FlushAsync_WhenWriterFlushDoesNotCompleteSynchronously_AwaitsContinuation()
    {
        // An in-memory Pipe flush usually completes synchronously, so FlushAsync's fast path wins and
        // the AwaitFlush continuation never runs. We wrap the writer in a decorator whose FlushAsync
        // returns a not-yet-completed task that we release after a turn, forcing the async branch.
        var inbound = new Pipe();
        var outbound = new Pipe();
        var gate = new ManualResetEventSlim(false);
        var deferring = new DeferredFlushWriter(outbound.Writer, gate);
        var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader), new ProtocolWriter(deferring));
        using var smHandle = sm;

        await DeliverFramedTo(inbound.Writer,
            (MessageType.Start, CreateStartMessage("inv-await-flush", 1).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        // SetState buffers a command; FlushPendingAsync's flush is deferred (incomplete), so the
        // call must traverse AwaitFlush. Release the gate on a background turn so the await resolves.
        sm.SetState("k", 1);
        var release = Task.Run(() => { Thread.Sleep(20); gate.Set(); });
        await AwaitBounded(sm.FlushPendingAsync(CancellationToken.None));
        await release;

        Assert.True(deferring.SawDeferredFlush, "the deferred (async) flush branch was not exercised");

        inbound.Writer.Complete();
        inbound.Reader.Complete();
        outbound.Writer.Complete();
        outbound.Reader.Complete();
    }

    // ---- Retry loop: TerminalException is NEVER retried (typed + void) -------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task TypedRun_WithRetryPolicy_TerminalException_IsNotRetried_ProposesFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        // Even WITH a retry policy, a TerminalException must hit the `catch (TerminalException) throw;`
        // arm — never the retry arm — so the closure runs exactly once and the failure is proposed.
        var runTask = sm.RunAsync<int>("typed-terminal", () =>
        {
            attempts++;
            throw new TerminalException("fatal", 422);
        }, CancellationToken.None, OneRetryFast).AsTask();

        await DeliverInboundAsync(rig, MessageType.RunCompletion,
            CreateRunCompletionFailure(1u, 422, "fatal"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)422, ex.Code);
        Assert.Equal(1, attempts);   // NOT retried — terminal short-circuits the loop

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task VoidRun_WithRetryPolicy_TerminalException_IsNotRetried_ProposesFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var attempts = 0;
        var runTask = sm.RunAsync("void-terminal", () =>
        {
            attempts++;
            throw new TerminalException("fatal", 422);
        }, CancellationToken.None, OneRetryFast).AsTask();

        await DeliverInboundAsync(rig, MessageType.RunCompletion,
            CreateRunCompletionFailure(1u, 422, "fatal"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));
        Assert.Equal((ushort)422, ex.Code);
        Assert.Equal(1, attempts);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Detached prefix-flush fault routed through RouteFlushFaultAsync (H9 sibling) ----------

    [Fact(Timeout = WatchdogMs)]
    public async Task SleepFuture_PrefixFlushFaultsAsync_RoutesFaultIntoSlot()
    {
        // SleepFutureAsync is synchronous: it cannot await its prefix flush, so it hands the flush
        // ValueTask to ObserveFlushFault. When that flush does NOT complete synchronously and then
        // FAULTS, RouteFlushFaultAsync's catch must TryFail the sleep's completion slot so the
        // future's awaiter observes the failure instead of hanging — the exact arm §4/G5 leave open
        // (G5 faults a LATER flush after the prefix already went out; here the prefix flush itself
        // is the deferred, faulting one).
        var inbound = new Pipe();
        var outbound = new Pipe();
        var faulting = new DeferredFaultingWriter(outbound.Writer);
        var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader), new ProtocolWriter(faulting));
        using var smHandle = sm;

        await DeliverFramedTo(inbound.Writer,
            (MessageType.Start, CreateStartMessage("inv-sleepfuture-fault", 1).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var resolve = sm.SleepFutureAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        faulting.Release();   // let the deferred prefix flush run → it throws → routed via TryFail

        // The routed fault (TryFail with code 500) surfaces through the future's await path as a
        // TerminalException — proof the flush fault was contained, not lost on a discarded ValueTask.
        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(resolve()));
        Assert.Equal((ushort)500, ex.Code);

        inbound.Writer.Complete();
        inbound.Reader.Complete();
        outbound.Writer.Complete();
        outbound.Reader.Complete();
    }

    // ---- SerializeObject(null) + SerializeWithSerde(serde) -------------------------------------

    [Fact]
    public void SerializeObject_Null_ReturnsEmpty_AndSerde_UsesProvidedSerde()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-serde", "", 0, 1);

        // SerializeObject's null arm returns Empty (the request-serialization fast path for a null
        // Call/Send payload), distinct from serializing a JSON null literal.
        Assert.True(sm.SerializeObject(null).IsEmpty);

        // SerializeWithSerde routes through a caller-provided ISerde when present (the
        // ResolveAwakeable-with-serde path), bypassing the default JSON serializer.
        var serde = new UpperBytesSerde();
        var bytes = sm.SerializeWithSerde("hi", serde);
        Assert.Equal("HI"u8.ToArray(), bytes.ToArray());
    }

    // ---- HandleIncomingMessage early-return / failure / named-signal arms ----------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task IncomingMessages_AckAndPayloadlessAndFailureAndNamedSignal_AllRoutedOrIgnored()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Register a user signal (idx 17) so a SignalNotification{idx=17, failure} routes to TryFail.
        var awakeable = sm.Awakeable();   // burns signal id 17

        // (a) EntryAck — acknowledged-and-ignored (HandleIncomingMessage early return).
        await DeliverRawAsync(rig, MessageType.EntryAck, Array.Empty<byte>());
        // (b) payload-less SignalNotification — the no-payload signal arm returns without parsing.
        await DeliverRawAsync(rig, MessageType.SignalNotification, Array.Empty<byte>());
        // (c) payload-less generic notification — the no-payload completion arm returns.
        await DeliverRawAsync(rig, MessageType.GetLazyStateCompletion, Array.Empty<byte>());
        // (d) named (not indexed) signal — built-in/named signals are not consumed → log-and-ignore.
        await DeliverInboundAsync(rig, MessageType.SignalNotification,
            new Gen.SignalNotificationMessage { Name = "named-not-consumed", Void = new Gen.Void() });
        // (e) FAILURE signal for the registered idx 17 → TryFail the awakeable's slot.
        await DeliverInboundAsync(rig, MessageType.SignalNotification,
            CreateSignalNotificationFailure(awakeable.SignalId, 500, "rejected"));

        // Drain the awakeable to prove the failure arm actually faulted its slot (and that the
        // ignored frames above did not corrupt routing): awaiting a failed slot throws the
        // TerminalException carrying the signal's failure code/message.
        var slot = sm.AwaitNotificationAsync(awakeable.SignalId, InvocationStateMachine.NotificationKind.Signal);
        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(slot));
        Assert.Equal((ushort)500, ex.Code);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- StartAsync defensive arms + empty input + StartInfo positional members ----------------

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_WrongState_Throws()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        // A second StartAsync after the SM left WaitingStart hits the state guard.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sm.StartAsync(CancellationToken.None));
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_FirstFrameNotStart_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // The very first frame is an InputCommand, not a Start → "Expected StartMessage".
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => sm.StartAsync(CancellationToken.None));
        Assert.Contains("Expected StartMessage", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_SecondFrameNotInput_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // Start is valid but the second frame is another Start, not an InputCommand.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-bad-input", 1));
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-bad-input-2", 1));
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => sm.StartAsync(CancellationToken.None));
        Assert.Contains("Expected InputCommand", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_NonNotificationNonCommandInReplayBatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // A frame inside the known-entries batch that is neither a command nor a notification (End is
        // a control frame) hits the "Unexpected ... inside the known-entries replay batch" arm.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-bad-batch", 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.End, Array.Empty<byte>());
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => sm.StartAsync(CancellationToken.None));
        Assert.Contains("known-entries replay batch", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_EmptyInputCommand_TakesEmptyInputPath_AndExposesAllStartInfoFields()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // A payload-less InputCommand exercises StartAsync's empty-input branch (input = Empty), and
        // reading every StartInfo positional member covers the record's generated accessors (Key,
        // RandomSeed) that no production caller reads.
        await rig.DeliverAsync(MessageType.Start,
            CreateStartMessage("inv-empty-input", 1, key: "the-key", randomSeed: 7));
        await DeliverRawAsync(rig, MessageType.InputCommand, Array.Empty<byte>());

        var info = await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.Equal("inv-empty-input", info.InvocationId);
        Assert.Equal("the-key", info.Key);
        Assert.Equal(1, info.KnownEntries);
        Assert.Equal(7UL, info.RandomSeed);
        Assert.True(info.Input.IsEmpty);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task StartFreshAsync(StateMachineRig rig)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-cov", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    private static async Task DeliverInboundAsync(StateMachineRig rig, MessageType type, IMessage message)
    {
        var writer = new ProtocolWriter(rig.InboundWriter);
        writer.WriteMessage(type, message.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DeliverRawAsync(StateMachineRig rig, MessageType type, byte[] payload)
    {
        var writer = new ProtocolWriter(rig.InboundWriter);
        writer.WriteMessage(type, payload);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DeliverFramedAsync(StateMachineRig rig,
        params (MessageType Type, byte[] Payload)[] frames)
    {
        foreach (var (type, payload) in frames)
            await rig.DeliverAsync(type, payload).ConfigureAwait(false);
    }

    private static async Task DeliverFramedTo(PipeWriter target,
        params (MessageType Type, byte[] Payload)[] frames)
    {
        var writer = new ProtocolWriter(target);
        foreach (var (type, payload) in frames)
            writer.WriteMessage(type, payload);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // Bounded outbound drain: the SM never completes its outbound writer (it only flushes), so we read
    // until no more buffered frames arrive within a short window rather than blocking on writer close.
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync(
        StateMachineRig rig)
    {
        await rig.StateMachine.FlushPendingAsync(CancellationToken.None);
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

    /// <summary>
    ///     A <see cref="PipeWriter" /> decorator whose first <see cref="FlushAsync" /> returns a task
    ///     that completes only after <paramref name="gate" /> is set — forcing the SM's FlushAsync to
    ///     fall off its synchronous fast path into the <c>AwaitFlush</c> continuation. Subsequent
    ///     flushes delegate normally so teardown does not deadlock.
    /// </summary>
    private sealed class DeferredFlushWriter(PipeWriter inner, ManualResetEventSlim gate) : PipeWriter
    {
        public bool SawDeferredFlush { get; private set; }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (!SawDeferredFlush)
            {
                SawDeferredFlush = true;
                return new ValueTask<FlushResult>(DeferredAsync(cancellationToken));
            }

            return inner.FlushAsync(cancellationToken);
        }

        // Returns a not-yet-completed task: the SM's FlushAsync sees IsCompletedSuccessfully == false
        // and falls into the AwaitFlush continuation we are exercising.
        private async Task<FlushResult> DeferredAsync(CancellationToken cancellationToken)
        {
            await Task.Run(() => gate.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    /// <summary>
    ///     A <see cref="PipeWriter" /> whose <see cref="FlushAsync" /> returns a not-yet-completed task
    ///     that, once released, FAULTS with an <see cref="IOException" />. Used to drive the
    ///     fire-and-forget prefix-flush fault routing (ObserveFlushFault → RouteFlushFaultAsync): the
    ///     non-synchronous completion bypasses ObserveFlushFault's fast path, and the fault is then
    ///     caught and routed into the completion slot via TryFail.
    /// </summary>
    private sealed class DeferredFaultingWriter(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            new(FaultAfterReleaseAsync());

        private async Task<FlushResult> FaultAfterReleaseAsync()
        {
            await _gate.Task.ConfigureAwait(false);
            throw new IOException("simulated prefix-flush fault");
        }

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    /// <summary>A trivial serde that uppercases ASCII bytes, proving SerializeWithSerde used IT.</summary>
    private sealed class UpperBytesSerde : ISerde<string>
    {
        public string ContentType => "application/octet-stream";

        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            var span = writer.GetSpan(value.Length);
            for (var i = 0; i < value.Length; i++)
                span[i] = (byte)char.ToUpperInvariant(value[i]);
            writer.Advance(value.Length);
        }

        public string Deserialize(ReadOnlySequence<byte> data) =>
            System.Text.Encoding.ASCII.GetString(data.ToArray());
    }
}
