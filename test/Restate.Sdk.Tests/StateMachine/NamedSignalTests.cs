using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Named-signal parity (Rust NotificationId::SignalName + sys_complete_signal). Two halves:
///     <list type="bullet">
///         <item>AWAIT side — RegisterNamedSignal/AwaitNamedSignalAsync park on a string NAME (no
///             numeric id, no command, no journal entry). On EOF the invocation suspends LISTING the
///             name in SuspensionMessage.waiting_named_signals (proto field 3); a matching named
///             SignalNotification resumes it and returns the value. A failure variant faults with a
///             TerminalException. A named signal nobody awaits is safely ignored (the disjoint
///             string-keyed manager never touches numeric awakeables).</item>
///         <item>SEND side — SendSignalAsync journals a SendSignalCommand with the NAME oneof + a
///             value/failure result, the same non-completable journaled command class as
///             CancelInvocationAsync. Replay dequeues the journaled SendSignal and emits nothing.</item>
///     </list>
///     Driven directly against <see cref="InvocationStateMachine" /> over the shared rig, every wait
///     watchdog-bounded so a regression FAILS rather than freezes.
/// </summary>
public sealed class NamedSignalTests
{
    private const int Timeout = 10_000;

    private static readonly byte[] MyValueJson = "\"hello-signal\""u8.ToArray();

    // ---- AWAIT side --------------------------------------------------------------------------

    // A handler parked solely on a named signal must SUSPEND on EOF, listing the name in
    // waiting_named_signals — the load-bearing parity for "the runtime resumes a named-signal park".
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_OnEof_SuspendsListingTheName()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");
        var awaitTask = sm.AwaitNamedSignalAsync("approval");

        // EOF with no delivery → the SM must emit a Suspension naming "approval" and nothing after it.
        rig.CompleteInbound();
        await SwallowPumpAsync(pump);

        var frames = await DrainOutboundAsync(rig);
        var suspension = frames.Single(f => f.Header.Type == MessageType.Suspension);
        var parsed = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Contains("approval", parsed.WaitingNamedSignals);
        Assert.Empty(parsed.WaitingCompletions);
        Assert.Empty(parsed.WaitingSignals);

        // The parked await unwinds with SuspendedException once suspension latched the managers.
        await AwaitBounded(Assert.ThrowsAsync<SuspendedException>(async () => await awaitTask));
    }

    // The happy path: a named SignalNotification carrying a payload resolves the parked await and the
    // handler reads the value back. StartAsync/the pump route the named frame BY NAME into the
    // string-keyed manager.
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_ResumesOnMatchingNotification_ReturnsValue()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");
        var awaitTask = sm.AwaitNamedSignalAsync("approval");

        await rig.DeliverAsync(MessageType.SignalNotification,
            CreateNamedSignalNotification("approval", MyValueJson));

        var result = await AwaitBounded(awaitTask);
        Assert.True(result.IsSuccess);
        Assert.Equal(MyValueJson, result.Value.ToArray());

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // A named signal carrying a FAILURE result faults the await with a TerminalException carrying the
    // wire code + message — the rejected-named-signal direction.
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_FailureNotification_FaultsWithTerminal()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");
        var awaitTask = sm.AwaitNamedSignalAsync("approval");

        await rig.DeliverAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Name = "approval",
            Failure = new Gen.Failure { Code = 409, Message = "rejected" }
        });

        // TryFail sets the TCS exception, so awaiting the named signal THROWS the TerminalException
        // (the await rethrows the faulted task) — identical to a rejected awakeable.
        var ex = await AwaitBounded(Assert.ThrowsAsync<TerminalException>(async () => await awaitTask));
        Assert.Equal(409, ex.Code);
        Assert.Equal("rejected", ex.Message);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // A named signal carrying a VOID result (no value) resolves the await with an empty success —
    // covers the value-is-null branch of the named routing.
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_VoidNotification_ResolvesEmptySuccess()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");
        var awaitTask = sm.AwaitNamedSignalAsync("approval");

        // value == null → the harness builds a Void-result named signal.
        await rig.DeliverAsync(MessageType.SignalNotification, CreateNamedSignalNotification("approval"));

        var result = await AwaitBounded(awaitTask);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsEmpty);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // An early-delivered named signal (notification arrives BEFORE the handler parks) resolves the
    // await synchronously from the buffered slot — the string-keyed early-completion path.
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_EarlyDelivery_ResolvesFromBufferedSlot()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");

        // Deliver BEFORE awaiting — the manager buffers it, then the await drains the buffered slot.
        await rig.DeliverAsync(MessageType.SignalNotification,
            CreateNamedSignalNotification("approval", MyValueJson));
        // Give the pump a turn so the early signal lands in the slot before we park.
        await Task.Yield();

        var result = await AwaitBounded(sm.AwaitNamedSignalAsync("approval"));
        Assert.True(result.IsSuccess);
        Assert.Equal(MyValueJson, result.Value.ToArray());

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // A named signal with NO waiter is safely ignored: it is buffered in the disjoint string-keyed
    // manager and never touches a numeric awakeable. A subsequent numeric idx-17 signal still resolves
    // the awakeable, proving the named frame stranded no one (existing behavior preserved).
    [Fact(Timeout = Timeout)]
    public async Task NamedSignal_WithNoWaiter_IsIgnored_NumericAwakeableStillResolves()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", [0xAB, 0xCD], "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var (_, firstSignalId) = sm.Awakeable();
        Assert.Equal(17u, firstSignalId);
        var awakeableAwait = sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);

        // A named signal that nobody awaits — must be ignored, not fault, not resolve the awakeable.
        await rig.DeliverAsync(MessageType.SignalNotification,
            CreateNamedSignalNotification("orphan-name", new byte[] { 0x99 }));

        Assert.False(awakeableAwait.IsCompleted, "an orphan named signal must not resolve a numeric awakeable");
        Assert.False(sm.IsCancellationRequested, "an orphan named signal must not cancel the invocation");

        // The numeric awakeable still resolves normally.
        await rig.DeliverAsync(MessageType.SignalNotification, CreateSignalNotification(17u, new byte[] { 0x2A }));
        var result = await AwaitBounded(awakeableAwait);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x2A }, result.Value.ToArray());

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // Awaiting a named signal MID-replay with a journaled command still pending and NO buffered named
    // result is an added-await-point (UncompletedDoProgressDuringReplay) → ProtocolException, never a
    // hang. Mirrors the numeric-await guard; proves the named await shares the replay-mutation guard.
    [Fact(Timeout = Timeout)]
    public async Task AwaitNamedSignal_UncompletedMidReplay_ThrowsJournalMutation()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal: [Input, SetStateCommand] with NO named-signal result buffered. known_entries = 2
        // leaves the SetState command queued, so IsReplaying stays true when the await is reached.
        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("k", "1"u8)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RegisterNamedSignal("approval");

        var ex = await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.AwaitNamedSignalAsync("approval")));
        Assert.Contains("journal mutation", ex.Message);

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- SEND side ---------------------------------------------------------------------------

    // SendSignalAsync (success) journals a SendSignalCommand with the NAME oneof + Value result.
    [Fact(Timeout = Timeout)]
    public async Task SendSignal_Processing_EmitsSendSignalCommandWithNameAndValue()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(sm.SendSignalAsync("inv_target", "approval", MyValueJson, CancellationToken.None));

        var command = await FirstCommandAsync(rig, sm, pump, MessageType.SendSignalCommand);
        var parsed = Gen.SendSignalCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("inv_target", parsed.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Name, parsed.SignalIdCase);
        Assert.Equal("approval", parsed.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Value, parsed.ResultCase);
        Assert.Equal(MyValueJson, parsed.Value.Content.ToByteArray());
    }

    // SendSignalAsync (failure) journals a SendSignalCommand with the NAME oneof + Failure result.
    [Fact(Timeout = Timeout)]
    public async Task SendSignal_Processing_Failure_EmitsSendSignalCommandWithNameAndFailure()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-named", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(sm.SendSignalAsync("inv_target", "approval",
            ReadOnlyMemory<byte>.Empty, CancellationToken.None, (500, "denied")));

        var command = await FirstCommandAsync(rig, sm, pump, MessageType.SendSignalCommand);
        var parsed = Gen.SendSignalCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("inv_target", parsed.TargetInvocationId);
        Assert.Equal("approval", parsed.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Failure, parsed.ResultCase);
        Assert.Equal(500u, parsed.Failure.Code);
        Assert.Equal("denied", parsed.Failure.Message);
    }

    // Replay determinism: a journaled SendSignal (named) is dequeued+validated on replay and NO new
    // command is emitted — the identical replay-safety of CancelInvocationAsync, name-keyed.
    [Fact(Timeout = Timeout)]
    public async Task SendSignal_Replay_DequeuesSendSignal_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand,
                ProtobufCodec.CreateSendNamedSignalSuccess("inv_target", "approval", MyValueJson)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(sm.SendSignalAsync("inv_target", "approval", MyValueJson, CancellationToken.None));

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.SendSignalCommand));
    }

    // A type mismatch on the journaled command (the live call expected SendSignal but the journal holds
    // something else) is a non-deterministic replay → ProtocolException, exactly like the cancel path.
    [Fact(Timeout = Timeout)]
    public async Task SendSignal_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("k", "1"u8)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.SendSignalAsync("inv_target", "approval", MyValueJson, CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- Helpers (mirrors PromiseTests) ------------------------------------------------------

    private static async Task DeliverReplayBatchAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, IMessage Message)[] frames)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("abc", knownEntries, key: "key"));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, message) in frames)
            await rig.DeliverAsync(type, message);

        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    /// <summary>Drains outbound frames written so far, stopping at End or writer completion.</summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync(
        StateMachineRig rig)
    {
        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        while (true)
        {
            var message = await AwaitBounded(reader.ReadMessageAsync(CancellationToken.None).AsTask());
            if (message is not { } frame) break;
            var header = frame.Header;
            var payload = frame.Payload.ToArray();
            frame.Dispose();
            frames.Add((header, payload));
            // Suspension is terminal (no End follows it); End is terminal too — stop without blocking.
            if (header.Type is MessageType.End or MessageType.Suspension) break;
        }

        return frames;
    }

    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> CollectFramesAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump)
    {
        rig.CompleteInbound();
        await AwaitBounded(pump);
        await AwaitBounded(sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None));

        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        while (true)
        {
            var message = await AwaitBounded(reader.ReadMessageAsync(CancellationToken.None).AsTask());
            if (message is not { } frame) break;
            var header = frame.Header;
            var payload = frame.Payload.ToArray();
            frame.Dispose();
            frames.Add((header, payload));
            if (header.Type == MessageType.End) break;
        }

        return frames;
    }

    private static async Task<IReadOnlyList<byte[]>> CommandsOfTypeAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump, MessageType type)
    {
        var frames = await CollectFramesAsync(rig, sm, pump);
        return frames.Where(f => f.Header.Type == type).Select(f => f.Payload).ToList();
    }

    private static async Task<byte[]> FirstCommandAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump, MessageType type)
    {
        var matches = await CommandsOfTypeAsync(rig, sm, pump, type);
        Assert.NotEmpty(matches);
        return matches[0];
    }

    private static async Task SwallowPumpAsync(Task pump)
    {
        try
        {
            await AwaitBounded(pump);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // Expected: the pump observed EOF after a suspension/mismatch already faulted the run.
        }
    }
}
