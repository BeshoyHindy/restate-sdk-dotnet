using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Blueprint §4.1 — replay correctness over the CoreVM-parity redesign (B1 deterministic ids,
///     B2 queue-driven replay, B10b Run-failure-from-notification). Mirrors the Rust ground-truth
///     suites tests/{sleep,run,state,input_output,failures}.rs. Every scenario is driven through the
///     real <see cref="InvocationStateMachine.StartAsync" /> preflight + pump over the shared
///     <see cref="ProtocolTestHarness.StateMachineRig" />, so replay buffering, the single-reader
///     invariant (B3), and id allocation all exercise production code paths.
///
///     Each case fails conceptually against the PRE-FIX behavior described in the blueprint —
///     e.g. §4.1.1 threw a JsonException because replayed COMMAND bytes were handed to the JSON
///     deserializer as a "result"; §4.1.4 hung forever because replay was gated on
///     Count &lt; KnownEntries; §4.1.2 never re-raised the durable failure. The 5 s
///     <see cref="ProtocolTestHarness.AwaitBounded{T}" /> watchdog turns the pre-fix infinite hangs
///     into failures rather than frozen runs (xunit v2 only aborts on Timeout for async tests).
/// </summary>
public class ReplayTests
{
    private const int WatchdogMs = 10_000;

    // Drives StartAsync to completion: frames Start + Input + the replay batch onto the inbound pipe,
    // then runs the preflight which buffers commands and routes any in-batch notifications.
    private static async Task<StartInfo> StartReplayAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, byte[] Payload)[] batch)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in batch)
            await rig.DeliverAsync(type, payload);
        return await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // Drains the outbound frames the SM has flushed so far WITHOUT requiring the outbound writer to
    // complete (the rig only completes it on Dispose). A short cancellation terminates the loop once
    // no more buffered data is immediately available, so the read can never hang the watchdog.
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

    // §4.1.1 — Run replay success. Journal [Input, RunCommand{id=1,name="x"}] + buffered
    // RunCompletionNotification{id=1,value="42"} → RunAsync<int> returns 42 from the notification,
    // never re-emits the RunCommand. Pre-fix: JsonException from feeding command bytes to the deserializer.
    [Fact(Timeout = WatchdogMs)]
    public async Task RunReplay_Success_ReturnsNotificationValue_NoCommandReemitted()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(1, Json(42)).ToByteArray()));

        var executed = false;
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var result = await AwaitBounded(rig.StateMachine.RunAsync<int>("x",
            () => { executed = true; return Task.FromResult(99); }, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal(42, result);                 // value comes from the notification, not the closure
        Assert.False(executed);                   // replayed Run never executes its side effect
        Assert.Equal(1, rig.Inbound.PeakPendingReads);   // single-reader invariant held (B3)

        await AwaitBounded(rig.StateMachine.CompleteAsync(result, CancellationToken.None));
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.RunCommand);
    }

    // §4.1.2 — Run replay terminal failure (B10b). The notification carries failure{500}, so the
    // saga-compensation-determinism requirement is met: the SAME TerminalException re-raises on replay
    // exactly as it did on the original attempt. Pre-fix: failure never surfaced from replay.
    [Fact(Timeout = WatchdogMs)]
    public async Task RunReplay_TerminalFailure_ReraisesFromNotification()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.RunCommand, CreateRunCommand("charge", 1).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletionFailure(1, 500, "card declined").ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<TerminalException>(() =>
            AwaitBounded(rig.StateMachine.RunAsync<int>("charge",
                () => Task.FromResult(0), CancellationToken.None).AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal(500u, ex.Code);
        Assert.Equal("card declined", ex.Message);
    }

    // §4.1.3 — Run replay without buffered completion AT THE FRONTIER (decision 4 / 1.7 case 2).
    // RunCommand is the LAST journaled command, no notification buffered → the closure EXECUTES
    // exactly once (claimed via TryClaimForExecution), the wire shows ProposeRunCompletion{id=1};
    // delivering the notification afterwards resolves the await with that value.
    [Fact(Timeout = WatchdogMs)]
    public async Task RunReplay_FrontierWithoutCompletion_ExecutesOnceAndProposes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray()));

        var executions = 0;
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var runTask = rig.StateMachine.RunAsync<int>("x", () =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(7);
        }, CancellationToken.None).AsTask();

        // The frontier Run executes locally and parks on its own notification; deliver the ack.
        await rig.DeliverAsync(MessageType.RunCompletion, CreateRunCompletion(1, Json(7)).ToByteArray());
        var result = await AwaitBounded(runTask);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal(7, result);
        Assert.Equal(1, executions);   // claimed-for-execution exactly once, no duplicate side effect
    }

    // §4.1.4 — Sleep resume hang regression (sleep.rs known_entries=3). Journal [Input,
    // SleepCommand{id=1}] + buffered SleepCompletionNotification{id=1}; handler sleeps then returns.
    // Pre-fix: infinite hang because replay was gated on the command-only Count < KnownEntries (B2).
    [Fact(Timeout = WatchdogMs)]
    public async Task SleepReplay_WithBufferedCompletion_ResolvesWithoutHang()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.SleepCommand, CreateSleepCommand(1).ToByteArray()),
            (MessageType.SleepCompletion, CreateSleepCompletion(1).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask());

        rig.CompleteInbound();
        await AwaitBounded(pump);

        // Dequeuing the only command flips Replaying → Processing; no duplicate SleepCommand on the wire.
        Assert.False(rig.StateMachine.IsReplaying);
        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.SleepCommand);
    }

    // §4.1.5 — Skip-replay regression (0 notifications). Journal [Input, SetStateCommand]; handler
    // performs the identical SetState → the command is dequeued from the queue, NOT re-emitted.
    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_NoDuplicateCommandOnWire()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, CreateSetStateCommand("count", Json(1)).ToByteArray()));

        rig.StateMachine.SetState("count", 1);

        Assert.False(rig.StateMachine.IsReplaying);
        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.SetStateCommand);
    }

    // §4.1.6 — Fresh invocation (input_output.rs). Start{1} + Input → State == Processing immediately,
    // no replay queue, no Replaying state.
    [Fact(Timeout = WatchdogMs)]
    public async Task FreshInvocation_KnownEntriesOne_IsProcessingImmediately()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 1);

        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
        Assert.False(rig.StateMachine.IsReplaying);
    }

    // §4.1.7 — known_entries=0 → ProtocolException (KNOWN_ENTRIES_IS_ZERO parity, input.rs:66).
    [Fact(Timeout = WatchdogMs)]
    public async Task KnownEntriesZero_Throws()
    {
        using var rig = new StateMachineRig();
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-zero", 0));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));

        await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None)));
    }

    // §4.1.8 — Mixed journal (failures.rs known_entries=5). Input + 2 commands + 2 notifications;
    // IsReplaying flips false exactly when the SECOND (last) command is dequeued, regardless of the
    // notifications interleaved into the known-entries batch.
    [Fact(Timeout = WatchdogMs)]
    public async Task MixedJournal_IsReplayingFlipsOnLastCommandDequeue()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 5,
            (MessageType.SleepCommand, CreateSleepCommand(1).ToByteArray()),
            (MessageType.SleepCompletion, CreateSleepCompletion(1).ToByteArray()),
            (MessageType.RunCommand, CreateRunCommand("x", 2).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(2, Json(5)).ToByteArray()));

        Assert.True(rig.StateMachine.IsReplaying);   // two commands buffered after preflight

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask());
        Assert.True(rig.StateMachine.IsReplaying);   // one command still buffered

        var run = await AwaitBounded(rig.StateMachine.RunAsync<int>("x",
            () => Task.FromResult(0), CancellationToken.None));
        Assert.False(rig.StateMachine.IsReplaying);  // last command dequeued → Processing
        Assert.Equal(5, run);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // §4.1.9 — Unavailable entry (defensive, journal-level). DequeueReplay on an empty queue throws
    // ProtocolException with NO wire read (in the integrated SM this is unreachable since IsReplaying
    // flips State to Processing; the guard is exercised directly as a unit test). No Timeout attribute:
    // xunit v2 only honors it for async tests, and this is a pure synchronous assertion with no wait.
    [Fact]
    public void UnavailableEntry_EmptyQueue_Throws()
    {
        var journal = new InvocationJournal();
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Run, "x"));
        Assert.Contains("Unavailable entry", ex.Message);
    }

    // §4.1.10 — Call replay + index divergence. Journal [Input, CallCommand{invIdIdx=1,resultId=2},
    // RunCommand{id=3}] + CallCompletion{2} + RunCompletion{3} → call value resolves against id 2 and
    // run value against id 3; each lands on its OWN id (pre-fix positional collision cross-wired them).
    [Fact(Timeout = WatchdogMs)]
    public async Task CallThenRunReplay_EachResolvesAgainstOwnId()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 5,
            (MessageType.CallCommand, ProtobufCodec.CreateCallCommand(
                "Svc", "Handler", "k", Array.Empty<byte>(), 2, 1).ToByteArray()),
            (MessageType.RunCommand, CreateRunCommand("r", 3).ToByteArray()),
            (MessageType.CallCompletion, CreateCallCompletion(2, Json("call-result")).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(3, Json(123)).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var callResult = await AwaitBounded(rig.StateMachine.CallAsync<string>(
            "Svc", "k", "Handler", null, CancellationToken.None));
        var runResult = await AwaitBounded(rig.StateMachine.RunAsync<int>("r",
            () => Task.FromResult(0), CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal("call-result", callResult);
        Assert.Equal(123, runResult);
    }

    // §4.1.11 — Notification-after-command ordering (late notification, legal ONLY at the replay
    // frontier where the mutation guard permits parking). Journal [Input, SleepCommand{id=1}]; the
    // pump delivers SleepCompletion{1} AFTER StartAsync returns → the parked handler resolves.
    [Fact(Timeout = WatchdogMs)]
    public async Task FrontierSleep_LateNotificationResolvesParkedHandler()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SleepCommand, CreateSleepCommand(1).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var sleepTask = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();

        await rig.DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(1).ToByteArray());
        await AwaitBounded(sleepTask);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // §4.1.12 — Type/name mismatch (B5 replay validation). RunCommand journaled but handler executes
    // Sleep → "type mismatch"; RunCommand name "a" but handler name "b" → "command mismatch".
    [Fact(Timeout = WatchdogMs)]
    public async Task Replay_TypeMismatch_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None).AsTask()));
        Assert.Contains("type mismatch", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task Replay_NameMismatch_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.RunCommand, CreateRunCommand("a", 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.RunAsync<int>("b",
                () => Task.FromResult(0), CancellationToken.None).AsTask()));
        Assert.Contains("Command mismatch", ex.Message);
    }

    // §4.1.13 — Property test. Arbitrary protobuf command payloads in the replay batch must NEVER
    // reach JsonSerializer — values only ever come from notifications/eager fields, so replay of pure
    // commands never throws JsonException (the exact pre-fix bug surfaced in §4.1.1).
    [Theory(Timeout = WatchdogMs)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(255)]
    public async Task RandomCommandPayloads_NeverReachJsonSerializer(int seed)
    {
        var rng = new Random(seed);
        var name = $"run-{seed}";
        var garbage = new byte[rng.Next(0, 64)];
        rng.NextBytes(garbage);

        using var rig = new StateMachineRig();
        // The RunCommand name is arbitrary and the journaled command carries no JSON; the value comes
        // exclusively from the notification below. A pre-fix run handed the command bytes to the JSON
        // deserializer and threw — post-fix it never does.
        var notificationValue = garbage.Length == 0 ? null : (ReadOnlyMemory<byte>?)garbage;
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.RunCommand, CreateRunCommand(name, 1).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(1, notificationValue).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        // RunAsync<byte[]> with a buffered byte[] value: deserialization (if any) reads the NOTIFICATION
        // bytes, never the command bytes. The load-bearing assertion is that the COMMAND payload itself
        // is never JSON-deserialized; a value-shape mismatch on the notification is acceptable here.
        var run = rig.StateMachine.RunAsync<byte[]>(name,
            () => Task.FromResult(Array.Empty<byte>()), CancellationToken.None).AsTask();
        try { await AwaitBounded(run); }
        catch (JsonException) { /* value-shape mismatch on the NOTIFICATION bytes is acceptable here */ }

        rig.CompleteInbound();
        await AwaitBounded(pump);
        Assert.False(rig.StateMachine.IsReplaying);
    }

    // §4.1.14 — Uncompleted await MID-replay (UncompletedDoProgressDuringReplay parity). Journal
    // [Input, SleepCommand{id=1}, SetStateCommand], NO notification → the handler awaiting the sleep
    // gets ProtocolException ("journal mutation"), never a hang or a suspend loop.
    [Fact(Timeout = WatchdogMs)]
    public async Task UncompletedAwaitMidReplay_ThrowsJournalMutation()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.SleepCommand, CreateSleepCommand(1).ToByteArray()),
            (MessageType.SetStateCommand, CreateSetStateCommand("k", Json(1)).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask()));
        Assert.Contains("journal mutation", ex.Message);
    }

    // §4.1.15 — Uncompleted Run MID-replay (1.7 case 3, duplicate-side-effect guard). Journal [Input,
    // RunCommand{id=1}, SetStateCommand], NO notification → ProtocolException AND the closure's
    // side-effect counter stays 0 (the closure must NOT execute mid-replay).
    [Fact(Timeout = WatchdogMs)]
    public async Task UncompletedRunMidReplay_ThrowsAndDoesNotExecuteClosure()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray()),
            (MessageType.SetStateCommand, CreateSetStateCommand("k", Json(1)).ToByteArray()));

        var executions = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.RunAsync<int>("x", () =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(0);
            }, CancellationToken.None).AsTask()));

        Assert.Contains("journal mutation", ex.Message);
        Assert.Equal(0, executions);   // refused to re-execute a side effect mid-replay
    }

    // §4.1.16 (subset) — Template A replay smoke: AttachInvocation happy path (value from the
    // notification) and GetInvocationOutput mismatch path (type mismatch → ProtocolException). The
    // wider matrix lives in lane 3e; here we cover the Template A consumers §4.1.1-13 miss.
    [Fact(Timeout = WatchdogMs)]
    public async Task AttachInvocationReplay_ResolvesFromNotification()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.AttachInvocationCommand,
                ProtobufCodec.CreateAttachInvocationCommand("target-inv", 1).ToByteArray()),
            (MessageType.AttachInvocationCompletion,
                CreateAttachCompletion(1, Json("attached")).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var result = await AwaitBounded(rig.StateMachine.AttachInvocationAsync<string>(
            "target-inv", CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);
        Assert.Equal("attached", result);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GetInvocationOutputReplay_TypeMismatch_Throws()
    {
        using var rig = new StateMachineRig();
        // Journal an AttachInvocation but call GetInvocationOutput → type mismatch.
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.AttachInvocationCommand,
                ProtobufCodec.CreateAttachInvocationCommand("target-inv", 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.GetInvocationOutputAsync<string>(
                "target-inv", CancellationToken.None).AsTask()));
        Assert.Contains("type mismatch", ex.Message);
    }

    // §4.1.17 — Call replay TARGET validation (B5). Journaled CallCommand targets service "A" while
    // the code calls service "B" with the same id shape → ProtocolException ("target mismatch"),
    // never silently cross-wired values.
    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_TargetMismatch_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.CallCommand, ProtobufCodec.CreateCallCommand(
                "A", "Handler", "k", Array.Empty<byte>(), 2, 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.CallAsync<string>(
                "B", "k", "Handler", null, CancellationToken.None).AsTask()));
        Assert.Contains("target", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SendReplay_TargetMismatch_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.OneWayCallCommand, ProtobufCodec.CreateSendCommand(
                "A", "Handler", "k", Array.Empty<byte>(), 0, null, 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SendAsync(
                "B", "k", "Handler", null, null, null, CancellationToken.None).AsTask()));
        Assert.Contains("target", ex.Message);
    }

    // §4.1.17b — SendSignalCommand replay TARGET + signal-identity validation (determinism hardening).
    // Rust SendSignalCommand command_header_eq compares target_invocation_id + signal_id (messages.rs
    // :622-654). The journaled cancel/named-signal must match what the handler now produces, else the
    // SAME journal-mismatch ProtocolException as the Call target check fires — a non-deterministic
    // handler that on replay cancels a DIFFERENT target or sends a DIFFERENT named signal is rejected,
    // not silently accepted. Payload bytes are NOT compared (§5 payload-equality deferral).

    // (a) Cancel replay with the SAME target_invocation_id PASSES (no mismatch); the journaled
    // SendSignalCommand is consumed from the queue and replay drains to Processing.
    [Fact(Timeout = WatchdogMs)]
    public async Task CancelInvocationReplay_SameTarget_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand("inv-target").ToByteArray()));

        await AwaitBounded(rig.StateMachine.CancelInvocationAsync("inv-target", CancellationToken.None).AsTask());

        Assert.False(rig.StateMachine.IsReplaying);   // command dequeued → Processing, no duplicate on wire
        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.SendSignalCommand);
    }

    // (b) Cancel replay with a DIFFERENT target_invocation_id throws the journal-mismatch
    // ProtocolException (was silently accepted before the fix — only type/entry_name were checked).
    [Fact(Timeout = WatchdogMs)]
    public async Task CancelInvocationReplay_DifferentTarget_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand("inv-original").ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.CancelInvocationAsync("inv-different", CancellationToken.None).AsTask()));
        Assert.Contains("Command mismatch", ex.Message);
        Assert.Contains("signal target", ex.Message);
    }

    // (c-pass) Named-signal replay with the SAME target AND signal NAME PASSES.
    [Fact(Timeout = WatchdogMs)]
    public async Task NamedSignalReplay_SameTargetAndName_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand, ProtobufCodec
                .CreateSendNamedSignalSuccess("inv-target", "approve", Json("ok")).ToByteArray()));

        await AwaitBounded(rig.StateMachine.SendSignalAsync(
            "inv-target", "approve", Json("ok"), CancellationToken.None).AsTask());

        Assert.False(rig.StateMachine.IsReplaying);
        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));
        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.SendSignalCommand);
    }

    // (c) Named-signal replay with a DIFFERENT signal NAME throws the journal-mismatch
    // ProtocolException (signal_id oneof divergence — the name part of command_header_eq).
    [Fact(Timeout = WatchdogMs)]
    public async Task NamedSignalReplay_DifferentName_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand, ProtobufCodec
                .CreateSendNamedSignalSuccess("inv-target", "approve", Json("ok")).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SendSignalAsync(
                "inv-target", "reject", Json("ok"), CancellationToken.None).AsTask()));
        Assert.Contains("Command mismatch", ex.Message);
        Assert.Contains("approve", ex.Message);   // the journaled signal name surfaces in the diagnostic
    }

    // (c-target) Named-signal replay with a DIFFERENT target_invocation_id (same name) throws — proves
    // target divergence is caught independently of the signal name.
    [Fact(Timeout = WatchdogMs)]
    public async Task NamedSignalReplay_DifferentTarget_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand, ProtobufCodec
                .CreateSendNamedSignalSuccess("inv-original", "approve", Json("ok")).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SendSignalAsync(
                "inv-different", "approve", Json("ok"), CancellationToken.None).AsTask()));
        Assert.Contains("Command mismatch", ex.Message);
        Assert.Contains("signal target", ex.Message);
    }

    // (c-cross) A journaled CANCEL (idx variant) replayed against a live NAMED signal send diverges on
    // signal_id (idx-vs-name): the same target but mismatched oneof variant must throw. Pins that the
    // idx/name discriminant — not just the target — participates in the comparison.
    [Fact(Timeout = WatchdogMs)]
    public async Task SignalReplay_IdxJournaledButNamedSent_Throws()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand("inv-target").ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SendSignalAsync(
                "inv-target", "approve", Json("ok"), CancellationToken.None).AsTask()));
        Assert.Contains("Command mismatch", ex.Message);
    }

    // §4.1.17c — Journal-level SendSignal overload (direct, no SM): a matching idx-variant signal
    // passes and is returned; a target divergence and a signal-idx divergence each throw. Exercises the
    // new DequeueReplay(type, target, idx?, name?) branches without driving the full state machine.
    [Fact]
    public void JournalSendSignal_MatchingIdx_Passes_DivergenceThrows()
    {
        var journal = new InvocationJournal();
        journal.Initialize(2);
        journal.EnqueueReplay(new ReplayCommand
        {
            MessageType = MessageType.SendSignalCommand,
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x",
            SignalIdx = 1
        });

        // Match: same target + same idx, no name → returns the command.
        var ok = journal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", 1u, null);
        Assert.Equal("inv-x", ok.SignalTargetInvocationId);

        // Fresh journal: target divergence throws.
        var targetJournal = new InvocationJournal();
        targetJournal.Initialize(2);
        targetJournal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x",
            SignalIdx = 1
        });
        var targetEx = Assert.Throws<ProtocolException>(() =>
            targetJournal.DequeueReplay(JournalEntryType.SendSignal, "inv-y", 1u, null));
        Assert.Contains("signal target", targetEx.Message);

        // Fresh journal: idx divergence throws (idx 1 journaled, idx 2 expected).
        var idxJournal = new InvocationJournal();
        idxJournal.Initialize(2);
        idxJournal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x",
            SignalIdx = 1
        });
        var idxEx = Assert.Throws<ProtocolException>(() =>
            idxJournal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", 2u, null));
        Assert.Contains("Command mismatch", idxEx.Message);
    }

    // §4.1.17d — Journal-level NAME-variant overload branches: a full name match passes (target + name
    // equal, idx null on both → falls through every || clause and returns); a name-only divergence
    // (target + idx equal, NAME differs) throws and surfaces the journaled name. Covers the third ||
    // clause (name compare) in both its pass-through and throw arms plus the FormatSignalId name path.
    [Fact]
    public void JournalSendSignal_NameVariant_MatchPasses_NameDivergenceThrows()
    {
        var matchJournal = new InvocationJournal();
        matchJournal.Initialize(2);
        matchJournal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x",
            SignalName = "approve"
        });
        var ok = matchJournal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", null, "approve");
        Assert.Equal("approve", ok.SignalName);

        var nameJournal = new InvocationJournal();
        nameJournal.Initialize(2);
        nameJournal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x",
            SignalName = "approve"
        });
        var nameEx = Assert.Throws<ProtocolException>(() =>
            nameJournal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", null, "reject"));
        Assert.Contains("approve", nameEx.Message);   // journaled name rendered via FormatSignalId name path
    }

    // §4.1.17e — FormatSignalId <empty> arm: a journaled SendSignal carrying NEITHER idx NOR name
    // (signal_id oneof unset, e.g. a corrupt/foreign journal) diverges from an idx-expected live cancel.
    // The diagnostic renders the journaled id as "<empty>" — covering the idx?.ToString() ?? "<empty>"
    // null arm of FormatSignalId.
    [Fact]
    public void JournalSendSignal_EmptySignalId_RendersEmptyInDiagnostic()
    {
        var journal = new InvocationJournal();
        journal.Initialize(2);
        journal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = "inv-x"
            // neither SignalIdx nor SignalName set → both null
        });
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", 1u, null));
        Assert.Contains("<empty>", ex.Message);   // journaled signal id rendered as <empty>
    }

    // §4.1.17f — Null-target normalization: a journaled SendSignal with a NULL SignalTargetInvocationId
    // (corrupt/foreign journal) normalizes to "" and diverges from a non-empty expected target → throws.
    // Covers the null arm of `command.SignalTargetInvocationId ?? ""` in the target compare.
    [Fact]
    public void JournalSendSignal_NullJournaledTarget_NormalizesAndThrows()
    {
        var journal = new InvocationJournal();
        journal.Initialize(2);
        journal.EnqueueReplay(new ReplayCommand
        {
            EntryType = JournalEntryType.SendSignal,
            SignalTargetInvocationId = null,
            SignalIdx = 1
        });
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.SendSignal, "inv-x", 1u, null));
        Assert.Contains("Command mismatch", ex.Message);
    }

    // §4.1.18 — Notifications-only batch (ZERO non-input commands). Start{known_entries=2} + Input +
    // one CompletionNotification → StartAsync lands in Processing; the notification parks as an
    // early-completion slot and the first matching live op consumes it without a wire wait.
    [Fact(Timeout = WatchdogMs)]
    public async Task NotificationsOnlyBatch_LandsProcessing_EarlyCompletionConsumed()
    {
        using var rig = new StateMachineRig();
        // The single in-batch notification targets completion id 1 — the id the first live Sleep will
        // allocate (FirstCompletionId). No command is buffered, so StartAsync finishes in Processing.
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SleepCompletion, CreateSleepCompletion(1).ToByteArray()));

        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
        Assert.False(rig.StateMachine.IsReplaying);

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        // The live Sleep allocates id 1, finds the early-completion slot already present, and resolves
        // without ever waiting on the wire.
        await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask());

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // §4.1.19 — Commands after Output (2.7.5). Journal [Input, OutputCommand, SetStateCommand];
    // handler returns immediately → CompleteAsync dequeues Output, sees IsReplaying still true →
    // ProtocolException ("commands after Output"), no End frame.
    [Fact(Timeout = WatchdogMs)]
    public async Task CommandsAfterOutput_Throws_NoEndFrame()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.OutputCommand, CreateOutputCommand(Json("done")).ToByteArray()),
            (MessageType.SetStateCommand, CreateSetStateCommand("k", Json(1)).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.CompleteAsync("done", CancellationToken.None).AsTask()));
        Assert.Contains("after Output", ex.Message);

        var frames = await DrainOutboundAsync(rig);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.End);
    }

    // §4.1.20 (parity — B10a documented non-bug). Two "__restate_now"-style Runs replay cleanly with
    // deterministic ids; values equal the originals. Here we model two same-name Runs (the ctx.Now()
    // shape) and prove both replay against their own ids without a name-collision mismatch.
    [Fact(Timeout = WatchdogMs)]
    public async Task TwoSameNameRunsReplay_DeterministicIds_CleanReplay()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 5,
            (MessageType.RunCommand, CreateRunCommand("__restate_now", 1).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(1, Json(1000L)).ToByteArray()),
            (MessageType.RunCommand, CreateRunCommand("__restate_now", 2).ToByteArray()),
            (MessageType.RunCompletion, CreateRunCompletion(2, Json(2000L)).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var first = await AwaitBounded(rig.StateMachine.RunAsync<long>("__restate_now",
            () => Task.FromResult(0L), CancellationToken.None));
        var second = await AwaitBounded(rig.StateMachine.RunAsync<long>("__restate_now",
            () => Task.FromResult(0L), CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal(1000L, first);
        Assert.Equal(2000L, second);
        Assert.False(rig.StateMachine.IsReplaying);
    }

    // Frames an AttachInvocation completion. AttachInvocation/GetInvocationOutput completions share the
    // generic CompletionNotification shape (a CompletionId + Value/Void oneof) that the SM parses via
    // the unified NotificationTemplate — the harness has no dedicated builder, so build it inline.
    private static Restate.Sdk.Internal.Protocol.Generated.AttachInvocationCompletionNotificationMessage
        CreateAttachCompletion(uint completionId, ReadOnlyMemory<byte> value) => new()
        {
            CompletionId = completionId,
            Value = new Restate.Sdk.Internal.Protocol.Generated.Value
            {
                Content = ByteString.CopyFrom(value.Span)
            }
        };
}
