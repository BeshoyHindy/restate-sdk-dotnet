using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Blueprint §4.2 — the single-reader invariant (B3), a port of the Rust ground-truth
///     tests/state.rs:47-70 (entry_already_completed). The pre-fix SM read the inbound pipe from
///     TWO concurrent tasks (the StartAsync preflight AND the per-op replay-advance reader), so a
///     completion that arrived inside the known-entries batch raced a live read and surfaced as
///     "Concurrent reads..." (InvalidOperationException) or a lost notification. The redesign reads
///     the wire from exactly ONE task at any instant: StartAsync buffers the whole batch, then
///     ProcessIncomingMessagesAsync is the sole reader.
///
///     Every scenario runs through the shared <see cref="ProtocolTestHarness.StateMachineRig" />,
///     whose inbound reader is the <see cref="ProtocolTestHarness.CountingPipeReader" /> probe: it
///     asserts <c>pending &lt;= 1</c> the instant a second concurrent <c>ReadAsync</c> appears and
///     records the peak for a post-run check. A violation therefore fails AT the race, not later.
/// </summary>
public class SingleReaderTests
{
    private const int WatchdogMs = 10_000;

    private static async Task<StartInfo> StartReplayAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, byte[] Payload)[] batch)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-single", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in batch)
            await rig.DeliverAsync(type, payload);
        return await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // Drains the outbound frames flushed so far without requiring the outbound writer to complete
    // (the rig only completes it on Dispose); a short cancellation ends the loop so it can never hang.
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

    // §4.2.1 — port of state.rs entry_already_completed: GetLazyStateCommand{id=1} +
    // GetLazyStateCompletionNotification{id=1} both inside the batch; handler GetState then returns.
    // No InvalidOperationException("Concurrent reads..."), bounded completion, Output+End present,
    // and the single-reader peak stays at 1.
    [Fact(Timeout = WatchdogMs)]
    public async Task EntryAlreadyCompleted_GetState_NoConcurrentRead_OutputAndEnd()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.GetLazyStateCommand, CreateGetStateCommand("k", 1).ToByteArray()),
            (MessageType.GetLazyStateCompletion, CreateGetStateCompletion(1, Json("v")).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var value = await AwaitBounded(rig.StateMachine.GetStateAsync<string>("k", CancellationToken.None));
        await AwaitBounded(rig.StateMachine.CompleteAsync(value, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal("v", value);
        Assert.True(rig.Inbound.PeakPendingReads <= 1,
            $"Single-reader invariant violated: peak {rig.Inbound.PeakPendingReads} concurrent reads");

        var frames = await DrainOutboundAsync(rig);
        Assert.Contains(frames, f => f.Header.Type == MessageType.OutputCommand);
        Assert.Equal(MessageType.End, frames[^1].Header.Type);
    }

    // §4.2.2 — same shape for a Sleep completion buffered in the batch.
    [Fact(Timeout = WatchdogMs)]
    public async Task EntryAlreadyCompleted_Sleep_NoConcurrentRead()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.SleepCommand, CreateSleepCommand(1).ToByteArray()),
            (MessageType.SleepCompletion, CreateSleepCompletion(1).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask());
        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.True(rig.Inbound.PeakPendingReads <= 1);
    }

    // §4.2.2 — same shape for a Call completion buffered in the batch.
    [Fact(Timeout = WatchdogMs)]
    public async Task EntryAlreadyCompleted_Call_NoConcurrentRead()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 3,
            (MessageType.CallCommand, ProtobufCodec.CreateCallCommand(
                "Svc", "Handler", "k", Array.Empty<byte>(), 2, 1).ToByteArray()),
            (MessageType.CallCompletion, CreateCallCompletion(2, Json("called")).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var result = await AwaitBounded(rig.StateMachine.CallAsync<string>(
            "Svc", "k", "Handler", null, CancellationToken.None));
        await AwaitBounded(rig.StateMachine.CompleteAsync(result, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal("called", result);
        Assert.True(rig.Inbound.PeakPendingReads <= 1);
    }

    // §4.2.3 — pure-command batch (k=0 notifications) → unchanged Processing transition once drained,
    // still single-reader.
    [Fact(Timeout = WatchdogMs)]
    public async Task PureCommandBatch_NoNotifications_ProcessingAfterDrain()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, CreateSetStateCommand("k", Json(1)).ToByteArray()));

        Assert.True(rig.StateMachine.IsReplaying);
        rig.StateMachine.SetState("k", 1);
        Assert.False(rig.StateMachine.IsReplaying);
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
        Assert.True(rig.Inbound.PeakPendingReads <= 1);
    }

    // §4.2.4 — single-reader invariant across a multi-op replay scenario with the pump running.
    // Several completable ops, a notification delivered live by the pump, all over the counting
    // reader: the peak must never exceed 1.
    [Fact(Timeout = WatchdogMs)]
    public async Task SingleReaderInvariant_AcrossMultiOpReplay()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 4,
            (MessageType.GetLazyStateCommand, CreateGetStateCommand("a", 1).ToByteArray()),
            (MessageType.GetLazyStateCompletion, CreateGetStateCompletion(1, Json("av")).ToByteArray()),
            (MessageType.SleepCommand, CreateSleepCommand(2).ToByteArray()));

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var av = await AwaitBounded(rig.StateMachine.GetStateAsync<string>("a", CancellationToken.None));
        var sleepTask = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();
        // The Sleep is the replay frontier; deliver its completion live through the single pump reader.
        await rig.DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(2).ToByteArray());
        await AwaitBounded(sleepTask);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.Equal("av", av);
        Assert.True(rig.Inbound.PeakPendingReads <= 1,
            $"Single-reader invariant violated: peak {rig.Inbound.PeakPendingReads}");
        Assert.True(rig.Inbound.TotalReads >= 1);   // the pump did read the wire (liveness)
    }

    // §4.2.5 — fuzz: random seeded interleavings of a command + its completion within the batch never
    // break the single-reader invariant and every run terminates bounded.
    [Theory(Timeout = WatchdogMs)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(29)]
    [InlineData(101)]
    [InlineData(997)]
    public async Task Fuzz_RandomBatchShapes_InvariantHoldsAndTerminates(int seed)
    {
        var rng = new Random(seed);
        var opCount = rng.Next(1, 4);   // 1..3 completable ops, each command + completion in the batch

        using var rig = new StateMachineRig();
        var batch = new List<(MessageType, byte[])>();
        uint nextId = 1;
        var ops = new List<uint>();
        for (var i = 0; i < opCount; i++)
        {
            var id = nextId++;
            ops.Add(id);
            batch.Add((MessageType.SleepCommand, CreateSleepCommand(id).ToByteArray()));
            batch.Add((MessageType.SleepCompletion, CreateSleepCompletion(id).ToByteArray()));
        }

        // known_entries = Input + 2 frames per op.
        await StartReplayAsync(rig, knownEntries: (uint)(1 + batch.Count), batch.ToArray());

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        foreach (var _ in ops)
            await AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask());

        rig.CompleteInbound();
        await AwaitBounded(pump);

        Assert.False(rig.StateMachine.IsReplaying);
        Assert.True(rig.Inbound.PeakPendingReads <= 1,
            $"seed {seed}: peak {rig.Inbound.PeakPendingReads} concurrent reads");
    }

    // §4.2.6 — pump-death unwind (2.6(b)). A Command-typed frame delivered AFTER preflight while the
    // handler is parked on a Sleep is a protocol violation: the pump's ProtocolException FailAlls both
    // managers, the parked handler unwinds with that exception, and the wait stays bounded (no
    // deadlock). The single-reader invariant holds throughout.
    [Fact(Timeout = WatchdogMs)]
    public async Task PumpDeath_CommandAfterPreflight_UnwindsParkedHandler()
    {
        using var rig = new StateMachineRig();
        // Fresh invocation so the live Sleep parks on the wire (id 1), then the pump reads a stray
        // command frame and dies, faulting the parked waiter.
        await StartReplayAsync(rig, knownEntries: 1);

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var sleepTask = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();

        // A command outside the replay batch is illegal post-preflight (HandleIncomingMessage guard).
        await rig.DeliverAsync(MessageType.SetStateCommand, CreateSetStateCommand("k", Json(1)).ToByteArray());

        // The pump faults; FailAll unwinds the parked Sleep with the same ProtocolException.
        await Assert.ThrowsAsync<ProtocolException>(() => AwaitBounded(sleepTask));
        await Assert.ThrowsAsync<ProtocolException>(() => AwaitBounded(pump));

        Assert.True(rig.Inbound.PeakPendingReads <= 1);
    }

    // §4.2.6 variant — fault the inbound reader mid-stream (IO error) → the parked handler unwinds
    // with the IO exception, bounded, nothing escapes into a deadlock.
    [Fact(Timeout = WatchdogMs)]
    public async Task PumpDeath_ReaderFault_UnwindsParkedHandler()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 1);

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var sleepTask = rig.StateMachine.SleepAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();

        // Complete the inbound writer with an exception — the pump's ReadAsync observes the IO fault.
        rig.InboundWriter.Complete(new IOException("connection reset"));

        await Assert.ThrowsAsync<IOException>(() => AwaitBounded(sleepTask));
        await Assert.ThrowsAsync<IOException>(() => AwaitBounded(pump));

        Assert.True(rig.Inbound.PeakPendingReads <= 1);
    }
}
