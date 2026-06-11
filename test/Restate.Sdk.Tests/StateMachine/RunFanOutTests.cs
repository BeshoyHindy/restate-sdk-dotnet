using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     The dedicated, parallelization-disabled collection shared by the two B5/B9 stress suites
///     (<see cref="RunFanOutTests" /> and <see cref="Journal.CompletionManagerRaceTests" />).
///     <see cref="System.Threading.ThreadPool.SetMinThreads" /> is process-global, so running the
///     min-thread-bumped fan-out loops concurrently with other parallel collections would skew
///     thread-pool behaviour and reintroduce flakes (§4.4 / §6.6 zero-flakes gate). The fixture
///     captures the original min-thread counts and restores them on disposal.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xunit collection-definition types conventionally end in 'Collection'.")]
[CollectionDefinition(StressCollection.Name, DisableParallelization = true)]
public sealed class StressCollection : ICollectionFixture<MinThreadFixture>
{
    public const string Name = "CoreVM stress (parallelization disabled)";
}

/// <summary>
///     Raises the thread-pool minimum worker count for the duration of the stress collection so the
///     64-task fan-out barrier actually releases its tasks in parallel instead of being drip-fed by
///     the pool's slow-ramp heuristic (which would serialize the race the test exists to exercise).
///     Restores the original counts on disposal — the bump must not leak into other collections.
/// </summary>
public sealed class MinThreadFixture : IDisposable
{
    private readonly int _originalWorker;
    private readonly int _originalIo;

    public MinThreadFixture()
    {
        System.Threading.ThreadPool.GetMinThreads(out _originalWorker, out _originalIo);
        System.Threading.ThreadPool.SetMinThreads(Math.Max(_originalWorker, 128), _originalIo);
    }

    public void Dispose() => System.Threading.ThreadPool.SetMinThreads(_originalWorker, _originalIo);
}

/// <summary>
///     B5 fan-out / journal-order suite (blueprint §4.4), mirroring shared-core <c>tests/run.rs</c>
///     fan-out. The pre-fix SDK derived wire completion ids from <c>_journal.Count</c> and let an
///     async write gate decide journaling order, so a fan-out attempt could journal commands in
///     completion order (not creation order) with colliding ids — poisoning every replay of a
///     correct program. The redesign (1.6/1.7) allocates ids and journals inside one synchronous
///     <c>_commandLock</c> section, so allocation order == journal order == wire order by
///     construction. These tests prove that structurally:
///       * completion-order-independent journal order with distinct sequential ids,
///       * replay type/name mismatch throws (never silently cross-wires values),
///       * a 64-task concurrency stress whose emitted RunCommand ids are monotonically increasing,
///       * output-payload integrity against a concurrent straggler Run proposal, and
///       * the frontier late-notification claim race (1.7 case 2 / TryClaimForExecution).
/// </summary>
[Collection(StressCollection.Name)]
public sealed class RunFanOutTests
{
    private const int StressRunCount = 64;
    private const int StressIterations = 100;
    private const int FrontierRaceIterations = 1_000;

    // ---- Local duplex rig ----------------------------------------------------------------------
    // A small purpose-built rig: it owns the SM plus its inbound/outbound pipes. A single background
    // collector task is the sole reader of the outbound pipe; tests observe emitted frames through
    // it, and can wait until a specific proposal has actually been flushed before delivering its ack
    // notification — the realistic protocol ordering (the runtime acks only AFTER receiving the
    // proposal), which a naive eager-deliver would race.

    private sealed class Rig : IDisposable
    {
        private readonly Pipe _inbound = new();
        private readonly Pipe _outbound = new();
        private readonly ConcurrentQueue<(MessageHeader Header, byte[] Payload)> _emitted = new();
        private readonly SemaphoreSlim _frameSignal = new(0);
        private readonly Task _collector;

        public Rig()
        {
            var reader = new ProtocolReader(_inbound.Reader);
            var writer = new ProtocolWriter(_outbound.Writer);
            StateMachine = new InvocationStateMachine(reader, writer);
            _collector = CollectOutboundAsync();
        }

        public InvocationStateMachine StateMachine { get; }

        public Task DeliverAsync(MessageType type, IMessage message)
        {
            var writer = new ProtocolWriter(_inbound.Writer);
            writer.WriteMessage(type, message.ToByteArray());
            return writer.FlushAsync().AsTask();
        }

        public void CompleteInbound() => _inbound.Writer.Complete();

        public async Task FeedAsync(params (MessageType Type, byte[] Payload)[] frames)
        {
            var writer = new ProtocolWriter(_inbound.Writer);
            foreach (var (type, payload) in frames)
                writer.WriteMessage(type, payload);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private async Task CollectOutboundAsync()
        {
            var protocolReader = new ProtocolReader(_outbound.Reader);
            while (await protocolReader.ReadMessageAsync().ConfigureAwait(false) is { } message)
            {
                _emitted.Enqueue((message.Header, message.Payload.ToArray()));
                message.Dispose();
                _frameSignal.Release();
            }
        }

        public async Task<(MessageHeader Header, byte[] Payload)> WaitForFrameAsync(
            Func<(MessageHeader Header, byte[] Payload), bool> predicate)
        {
            while (true)
            {
                foreach (var frame in _emitted)
                    if (predicate(frame))
                        return frame;
                await AwaitBounded(_frameSignal.WaitAsync()).ConfigureAwait(false);
            }
        }

        public IReadOnlyList<(MessageHeader Header, byte[] Payload)> EmittedFrames() => _emitted.ToArray();

        public async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync()
        {
            await _outbound.Writer.CompleteAsync().ConfigureAwait(false);
            await AwaitBounded(_collector).ConfigureAwait(false);
            return _emitted.ToArray();
        }

        public static byte[] ToRawStream(IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
        {
            var buffer = new ArrayBufferWriter<byte>();
            Span<byte> header = stackalloc byte[MessageHeader.Size];
            foreach (var (h, payload) in frames)
            {
                MessageHeader.Create(h.Type, MessageFlags.None, (uint)payload.Length).Write(header);
                buffer.Write(header);
                buffer.Write(payload);
            }

            return buffer.WrittenSpan.ToArray();
        }

        public void Dispose()
        {
            StateMachine.Dispose();
            _inbound.Writer.Complete();
            _inbound.Reader.Complete();
            _outbound.Writer.Complete();
            _outbound.Reader.Complete();
            _frameSignal.Dispose();
        }
    }

    private static byte[] Frame(IMessage message) => message.ToByteArray();

    private static bool IsRunCommand((MessageHeader Header, byte[] Payload) frame) =>
        frame.Header.Type == MessageType.RunCommand;

    private static bool IsProposalFor((MessageHeader Header, byte[] Payload) frame, uint id) =>
        frame.Header.Type == MessageType.ProposeRunCompletion
        && Gen.ProposeRunCompletionMessage.Parser.ParseFrom(frame.Payload).ResultCompletionId == id;

    // ---- §4.4.1 Ordering: completion order must not affect journal order ------------------------

    /// <summary>
    ///     Two RunFutures "A" then "B" are created sequentially; their RunCommands must journal in
    ///     creation order with distinct sequential ids (1, 2). The closures' proposals are flushed
    ///     (we wait for both on the wire), THEN their ack notifications are delivered in REVERSE id
    ///     order (B=2 before A=1). Neither the journaled RunCommand order nor the proposal id-value
    ///     pairing depends on that completion order — the structural B5 guarantee.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunFanOut_JournalOrderIsCreationOrder_RegardlessOfCompletionOrder()
    {
        using var rig = new Rig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-fanout", "", 0, 1);   // fresh: Processing
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var futureA = sm.RunFutureAsync("A", () => Task.FromResult("A"), CancellationToken.None);
        var futureB = sm.RunFutureAsync("B", () => Task.FromResult("B"), CancellationToken.None);

        // Wait until BOTH proposals are on the wire (realistic ordering: the runtime acks only after
        // receiving the proposal), then deliver completions in REVERSE id order.
        await rig.WaitForFrameAsync(frame => IsProposalFor(frame, 1));
        await rig.WaitForFrameAsync(frame => IsProposalFor(frame, 2));
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletion(2, JsonSerializer.SerializeToUtf8Bytes("B", sm.JsonOptions)));
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletion(1, JsonSerializer.SerializeToUtf8Bytes("A", sm.JsonOptions)));

        var resultB = await AwaitBounded(futureB());
        var resultA = await AwaitBounded(futureA());
        Assert.Equal("A", sm.Deserialize<string>(resultA.Value));
        Assert.Equal("B", sm.Deserialize<string>(resultB.Value));

        await AwaitBounded(sm.CompleteAsync(null, CancellationToken.None));
        rig.CompleteInbound();
        await AwaitBounded(pump);

        var frames = AssertFrameOrder(Rig.ToRawStream(await rig.DrainOutboundAsync()));
        var runCommands = frames
            .Where(f => f.Header.Type == MessageType.RunCommand)
            .Select(f => Gen.RunCommandMessage.Parser.ParseFrom(f.Payload))
            .ToList();
        Assert.Equal(2, runCommands.Count);
        // Creation order, NOT completion order.
        Assert.Equal("A", runCommands[0].Name);
        Assert.Equal("B", runCommands[1].Name);
        // Distinct, sequential ids starting at FirstCompletionId (1).
        Assert.Equal(1u, runCommands[0].ResultCompletionId);
        Assert.Equal(2u, runCommands[1].ResultCompletionId);

        // Proposals reference matching ids irrespective of flush order: id 1 carries "A", id 2 "B".
        // ProposeRunCompletionMessage.Value is a bytes field (ByteString), not a nested Value message.
        var proposals = frames
            .Where(f => f.Header.Type == MessageType.ProposeRunCompletion)
            .Select(f => Gen.ProposeRunCompletionMessage.Parser.ParseFrom(f.Payload))
            .ToDictionary(p => p.ResultCompletionId, p => p.Value.ToByteArray());
        Assert.Equal("A", JsonSerializer.Deserialize<string>(proposals[1], sm.JsonOptions));
        Assert.Equal("B", JsonSerializer.Deserialize<string>(proposals[2], sm.JsonOptions));
    }

    // ---- §4.4.2 Replay cross-wiring: reversed journal names must throw -------------------------

    /// <summary>
    ///     The journal records RunCommands in name order [B, A], but the handler executes Run("A")
    ///     first. The replay dequeue compares the whole expected command (name included), so the
    ///     mismatch must raise a ProtocolException rather than silently swapping values across the
    ///     two side effects (check_entry_header_match parity).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunReplay_ReversedJournalNames_ThrowsCommandMismatch()
    {
        using var rig = new Rig();
        var sm = rig.StateMachine;

        // Start{known_entries=3} + Input + RunCommand("B",1) + RunCommand("A",2): journal order [B, A].
        await rig.FeedAsync(
            (MessageType.Start, Frame(CreateStartMessage("inv-replay", 3))),
            (MessageType.InputCommand, Frame(CreateInputCommand([]))),
            (MessageType.RunCommand, Frame(CreateRunCommand("B", 1))),
            (MessageType.RunCommand, Frame(CreateRunCommand("A", 2))));

        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.Equal(InvocationState.Replaying, sm.State);

        // The handler runs "A" first; the frontier dequeue expects name "A" but the journal has "B".
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(sm.RunAsync("A", () => Task.FromResult(0), CancellationToken.None)));
        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The dual of the name mismatch: a journaled Run where the handler executes a Sleep at the
    ///     same position is a type mismatch (also B5 replay validation).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunReplay_TypeMismatch_ThrowsTypeMismatch()
    {
        using var rig = new Rig();
        var sm = rig.StateMachine;

        await rig.FeedAsync(
            (MessageType.Start, Frame(CreateStartMessage("inv-typemismatch", 2))),
            (MessageType.InputCommand, Frame(CreateInputCommand([]))),
            (MessageType.RunCommand, Frame(CreateRunCommand("x", 1))));

        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.Equal(InvocationState.Replaying, sm.State);

        // The handler sleeps where a Run was journaled → type mismatch.
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(sm.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None)));
        Assert.Contains("type mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- §4.4.3 Concurrency stress: monotonic ids prove id-order == journal-order --------------

    /// <summary>
    ///     <see cref="StressRunCount" /> RunFutures are created concurrently behind a single barrier
    ///     (min threads raised by the collection fixture). Because id allocation, RunCommand
    ///     journaling, and the buffer write all happen inside one synchronous <c>_commandLock</c>
    ///     section, the emitted RunCommand ids MUST appear in strictly increasing order in the wire
    ///     stream regardless of which thread won the lock — the structural proof that
    ///     id-allocation order equals journal order (1.6). Repeated <see cref="StressIterations" />
    ///     times; every frame header must be valid and the journal must contain exactly {1..N}.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunFanOut_64Concurrent_EmitsMonotonicIdsAndValidFrames()
    {
        for (var iteration = 0; iteration < StressIterations; iteration++)
        {
            using var rig = new Rig();
            var sm = rig.StateMachine;
            sm.Initialize($"inv-stress-{iteration}", "", 0, 1);
            var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var creators = new Task<Func<ValueTask<CompletionResult>>>[StressRunCount];
            for (var index = 0; index < StressRunCount; index++)
            {
                var name = $"run-{index}";
                creators[index] = Task.Run(async () =>
                {
                    await barrier.Task.ConfigureAwait(false);
                    return sm.RunFutureAsync(name, () => Task.FromResult(0), CancellationToken.None);
                });
            }

            barrier.SetResult();
            var futures = await AwaitBounded(Task.WhenAll(creators));

            // Wait for every RunCommand to be journaled before delivering acks, then deliver ids 1..N.
            await rig.WaitForFrameAsync(_ => rig.EmittedFrames().Count(IsRunCommand) == StressRunCount);
            for (uint id = 1; id <= StressRunCount; id++)
                await rig.DeliverAsync(MessageType.RunCompletion,
                    CreateRunCompletion(id, JsonSerializer.SerializeToUtf8Bytes(0, sm.JsonOptions)));
            foreach (var future in futures)
                await AwaitBounded(future());

            await AwaitBounded(sm.CompleteAsync(null, CancellationToken.None));
            rig.CompleteInbound();
            await AwaitBounded(pump);

            var frames = AssertFrameOrder(Rig.ToRawStream(await rig.DrainOutboundAsync()));
            var runCommandIds = frames
                .Where(f => f.Header.Type == MessageType.RunCommand)
                .Select(f => Gen.RunCommandMessage.Parser.ParseFrom(f.Payload).ResultCompletionId)
                .ToList();

            Assert.Equal(StressRunCount, runCommandIds.Count);
            // Strictly increasing in stream order — the heart of B5. Pre-fix (Count-derived ids under
            // an async write gate) this could interleave or collide.
            for (var pos = 1; pos < runCommandIds.Count; pos++)
                Assert.True(runCommandIds[pos] > runCommandIds[pos - 1],
                    $"RunCommand ids not monotonically increasing at position {pos}: " +
                    $"{runCommandIds[pos - 1]} then {runCommandIds[pos]} (iteration {iteration})");
            // The full id set is exactly {1..N}: no id skipped, none duplicated.
            Assert.Equal(Enumerable.Range(1, StressRunCount).Select(value => (uint)value), runCommandIds);

            Assert.Equal(InvocationState.Closed, sm.State);
            Assert.False(sm.IsReplaying);
        }
    }

    // ---- §4.4.4 Journal order under contention, proven via replay ------------------------------

    /// <summary>
    ///     The SemaphoreSlim-gate design that 1.6 replaced would let a slow flush reorder journaling
    ///     vs call order. Here a FRESH run interleaves a RunFuture fan-out with synchronous
    ///     SetState/ClearState; the captured COMMAND + completion stream is REPLAYED against a second
    ///     SM doing the identical sequence: zero type/name/id mismatch proves journal order equals
    ///     call order even when the closures complete out of band. (On replay the Runs are consumed
    ///     from their buffered completions — they are NOT at the frontier, so the completions must be
    ///     part of the known-entries batch, exactly as the runtime would resend them.)
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunFanOut_InterleavedWithState_ReplaysWithoutMismatch()
    {
        // ---- Capture run: interleave Run fan-out with sync state ops on a fresh invocation. ----
        IReadOnlyList<(MessageHeader Header, byte[] Payload)> captured;
        using (var rig = new Rig())
        {
            var sm = rig.StateMachine;
            sm.Initialize("inv-capture", "key", 0, 1);
            var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

            var first = sm.RunFutureAsync("first", () => Task.FromResult(1), CancellationToken.None);
            sm.SetState("a", 1);                  // synchronous command between two runs
            var second = sm.RunFutureAsync("second", () => Task.FromResult(2), CancellationToken.None);
            sm.ClearState("a");                   // another synchronous command

            // Wait for both proposals before acking (realistic ordering), then deliver acks.
            await rig.WaitForFrameAsync(frame => IsProposalFor(frame, 1));
            await rig.WaitForFrameAsync(frame => IsProposalFor(frame, 2));
            await rig.DeliverAsync(MessageType.RunCompletion,
                CreateRunCompletion(1, JsonSerializer.SerializeToUtf8Bytes(1, sm.JsonOptions)));
            await rig.DeliverAsync(MessageType.RunCompletion,
                CreateRunCompletion(2, JsonSerializer.SerializeToUtf8Bytes(2, sm.JsonOptions)));
            await AwaitBounded(first());
            await AwaitBounded(second());

            await AwaitBounded(sm.CompleteAsync(null, CancellationToken.None));
            rig.CompleteInbound();
            await AwaitBounded(pump);
            captured = await rig.DrainOutboundAsync();
        }

        // The journaled COMMAND order, in emission order.
        var journaledCommands = captured
            .Where(f => f.Header.Type is MessageType.RunCommand
                or MessageType.SetStateCommand or MessageType.ClearStateCommand)
            .ToList();
        Assert.Equal(
            new[]
            {
                MessageType.RunCommand, MessageType.SetStateCommand,
                MessageType.RunCommand, MessageType.ClearStateCommand
            },
            journaledCommands.Select(f => f.Header.Type));

        // ---- Replay run: feed the captured commands back as a known-entries batch, with the two ----
        //      Run completions buffered (the runtime resends them with the batch). known_entries
        //      counts commands AND notifications (protocol.proto:60-61).
        using (var replayRig = new Rig())
        {
            var replaySm = replayRig.StateMachine;
            const int notificationCount = 2;   // the two RunCompletions
            var knownEntries = (uint)(1 + journaledCommands.Count + notificationCount);
            var batch = new List<(MessageType, byte[])>
            {
                (MessageType.Start, Frame(CreateStartMessage("inv-capture", knownEntries, "key"))),
                (MessageType.InputCommand, Frame(CreateInputCommand([])))
            };
            batch.AddRange(journaledCommands.Select(f => (f.Header.Type, f.Payload)));
            batch.Add((MessageType.RunCompletion,
                Frame(CreateRunCompletion(1, JsonSerializer.SerializeToUtf8Bytes(1, replaySm.JsonOptions)))));
            batch.Add((MessageType.RunCompletion,
                Frame(CreateRunCompletion(2, JsonSerializer.SerializeToUtf8Bytes(2, replaySm.JsonOptions)))));
            await replayRig.FeedAsync(batch.ToArray());

            await AwaitBounded(replaySm.StartAsync(CancellationToken.None));
            Assert.Equal(InvocationState.Replaying, replaySm.State);
            var pump = replaySm.ProcessIncomingMessagesAsync(CancellationToken.None);

            // Replay the IDENTICAL call sequence. Each dequeue validates type/name/id against the
            // journal; any reorder during capture would surface as a ProtocolException here. The Runs
            // consume their buffered completions without re-executing.
            var firstReplay = replaySm.RunFutureAsync("first", () => Task.FromResult(1), CancellationToken.None);
            replaySm.SetState("a", 1);
            var secondReplay = replaySm.RunFutureAsync("second", () => Task.FromResult(2), CancellationToken.None);
            replaySm.ClearState("a");

            Assert.Equal(1, replaySm.Deserialize<int>((await AwaitBounded(firstReplay())).Value));
            Assert.Equal(2, replaySm.Deserialize<int>((await AwaitBounded(secondReplay())).Value));

            await AwaitBounded(replaySm.CompleteAsync(null, CancellationToken.None));
            replayRig.CompleteInbound();
            await AwaitBounded(pump);   // no ProtocolException ⇒ journal order equalled call order
        }
    }

    // ---- §4.4.5 Output integrity under straggler proposals -------------------------------------

    /// <summary>
    ///     An un-awaited RunFuture's closure serializes its proposal (sharing <c>_serializeBuffer</c>)
    ///     concurrently with the handler returning a large result. The OutputCommand payload must
    ///     equal the expected serialization byte-for-byte — a regression guard for the torn
    ///     <c>_serializeBuffer</c> race (2.7.5 / 2.8): the result serialization happens inside the
    ///     same lock the straggler proposal contends for, so neither can shred the other. The
    ///     straggler proposes (and we await it on the wire) before the handler closes, mirroring the
    ///     runtime's ack-after-proposal ordering.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunFanOut_StragglerProposal_DoesNotTearOutputPayload()
    {
        using var rig = new Rig();
        var sm = rig.StateMachine;
        sm.Initialize("inv-straggler", "", 0, 1);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // A large, distinctive result whose serialization is many KB — a torn write would corrupt it.
        var largeResult = Enumerable.Range(0, 4096).Select(value => $"item-{value:D5}").ToArray();
        var expectedOutput = JsonSerializer.SerializeToUtf8Bytes(largeResult, sm.JsonOptions);

        // A straggler Run that serializes a non-trivial proposal value (sharing _serializeBuffer).
        var stragglerPayload = Enumerable.Range(0, 1024).ToArray();
        var expectedProposal = JsonSerializer.SerializeToUtf8Bytes(stragglerPayload, sm.JsonOptions);
        var straggler = sm.RunFutureAsync("straggler", () => Task.FromResult(stragglerPayload), CancellationToken.None);

        // Await the straggler proposal on the wire, deliver its ack so the future resolves, then close
        // the handler with its large result. The output serialization and the straggler proposal
        // serialization both went through _serializeBuffer; both must be byte-exact.
        await rig.WaitForFrameAsync(frame => IsProposalFor(frame, 1));
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletion(1, JsonSerializer.SerializeToUtf8Bytes(stragglerPayload, sm.JsonOptions)));
        await AwaitBounded(straggler());
        await AwaitBounded(sm.CompleteAsync(largeResult, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);

        var frames = AssertFrameOrder(Rig.ToRawStream(await rig.DrainOutboundAsync()));
        var output = frames.Single(f => f.Header.Type == MessageType.OutputCommand);
        var outputMsg = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.Equal(expectedOutput, outputMsg.Value.Content.ToByteArray());

        // The straggler proposal survived intact too (the other side of the shared buffer).
        var proposal = frames.Single(f => f.Header.Type == MessageType.ProposeRunCompletion);
        var proposalMsg = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload);
        Assert.Equal(expectedProposal, proposalMsg.Value.ToByteArray());
    }

    // ---- §4.4.6 Frontier late-notification claim race (TryClaimForExecution) -------------------

    /// <summary>
    ///     At the replay frontier (1.7 case 2) a Run with no buffered completion executes inline iff
    ///     the atomic <c>TryClaimForExecution</c> wins; otherwise a raced notification is consumed
    ///     without executing. This loops <see cref="FrontierRaceIterations" /> times, racing the
    ///     pump's delivery of the RunCompletionNotification against the closure start: the closure
    ///     executes AT MOST once and AT MOST one ProposeRunCompletion is emitted — never a duplicate
    ///     side effect or a double proposal.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RunReplay_FrontierLateNotificationRace_ExecutesAtMostOnce()
    {
        for (var iteration = 0; iteration < FrontierRaceIterations; iteration++)
        {
            using var rig = new Rig();
            var sm = rig.StateMachine;

            // Journal: Start{2} + Input + RunCommand("frontier",1) — the Run is the replay frontier.
            await rig.FeedAsync(
                (MessageType.Start, Frame(CreateStartMessage($"inv-frontier-{iteration}", 2))),
                (MessageType.InputCommand, Frame(CreateInputCommand([]))),
                (MessageType.RunCommand, Frame(CreateRunCommand("frontier", 1))));
            await AwaitBounded(sm.StartAsync(CancellationToken.None));

            var executionCount = 0;
            var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

            // Race: the runtime delivers the notification (consume path) against the handler issuing
            // the Run (which may execute inline if it claims first). Both start as near-simultaneously
            // as the scheduler allows.
            var deliver = Task.Run(() => rig.DeliverAsync(MessageType.RunCompletion,
                CreateRunCompletion(1, JsonSerializer.SerializeToUtf8Bytes(99, sm.JsonOptions))));
            var run = sm.RunAsync("frontier", () =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.FromResult(99);
            }, CancellationToken.None);

            await AwaitBounded(Task.WhenAll(deliver, run.AsTask()));

            await AwaitBounded(sm.CompleteAsync(null, CancellationToken.None));
            rig.CompleteInbound();
            await AwaitBounded(pump);

            // At most once executed (claimed-vs-consumed is mutually exclusive).
            Assert.True(executionCount <= 1, $"closure executed {executionCount} times (iteration {iteration})");

            var frames = await rig.DrainOutboundAsync();
            var proposals = frames.Count(f => f.Header.Type == MessageType.ProposeRunCompletion);
            // If the closure ran (claim won), exactly one proposal; if the notification won, zero.
            // Never both a proposal AND a consumed notification for one executed closure.
            Assert.True(proposals <= 1, $"emitted {proposals} proposals (iteration {iteration})");
            Assert.Equal(executionCount, proposals);
        }
    }
}
