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
///     B7 — eager state cache parity (blueprint §4.6), mirroring Rust shared-core
///     <c>EagerState { is_partial, values: HashMap&lt;String, Option&lt;Bytes&gt;&gt; }</c>
///     (vm/context.rs) and the journaled GetEagerStateCommand layout (vm/transitions/journal.rs).
///
///     Pre-fix the SDK discarded the partial-state flag and treated an absent key under a PARTIAL
///     start as a known-empty value — silently defaulting a Get instead of issuing a
///     GetLazyStateCommand roundtrip (the §4.6.1 regression). It also never journaled eager hits,
///     so eager Gets did not consume a completion id, drifting the id counter on replay. These
///     tests fail against that behavior and pass now.
///
///     Driven directly against <see cref="InvocationStateMachine" />: fresh-path cases call
///     <see cref="InvocationStateMachine.Initialize(string, byte[], string, ulong, int,
///     System.Collections.Generic.Dictionary{string, System.ReadOnlyMemory{byte}?}?, bool)" />
///     with a seed map; replay cases drive <see cref="InvocationStateMachine.StartAsync" /> over a
///     framed wire so the journaled commands decode through the real preflight path. Every wait is
///     bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(System.Threading.Tasks.ValueTask{T})" />.
/// </summary>
public sealed class EagerStateTests
{
    private static readonly string[] SortedKeysAbc = ["a", "b", "c"];
    private static readonly string[] KeysX = ["x"];

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // ---- Direct (fresh-path) rig ----------------------------------------------------------------

    private sealed class FreshRig : IDisposable
    {
        private readonly Pipe _inbound = new();
        private readonly Pipe _outbound = new();

        public FreshRig(Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null,
            bool partialState = true)
        {
            StateMachine = new InvocationStateMachine(
                new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer));
            StateMachine.Initialize("inv-eager", [0x01], "key-1", 0, 1, eagerState, partialState);
        }

        public InvocationStateMachine StateMachine { get; }

        public PipeWriter InboundWriter => _inbound.Writer;

        /// <summary>Signals EOF on the inbound pipe (drives the pump to completion / suspension).</summary>
        public void CompleteInbound() => _inbound.Writer.Complete();

        /// <summary>
        ///     Terminal drain: flushes any buffered commands, completes the outbound writer, then
        ///     reads every emitted frame. Call once, after any pump has completed.
        /// </summary>
        public async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> FlushAndReadOutboundAsync()
        {
            await StateMachine.FlushPendingAsync(CancellationToken.None);
            _outbound.Writer.Complete();
            var reader = new ProtocolReader(_outbound.Reader);
            var frames = new List<(MessageHeader, byte[])>();
            while (await reader.ReadMessageAsync(CancellationToken.None).ConfigureAwait(false) is { } message)
            {
                frames.Add((message.Header, message.Payload.ToArray()));
                message.Dispose();
            }

            return frames;
        }

        public void Dispose()
        {
            StateMachine.Dispose();
            _inbound.Writer.Complete();
            _inbound.Reader.Complete();
            _outbound.Writer.Complete();
            _outbound.Reader.Complete();
        }
    }

    // §4.6.1 — partial-state silent-default regression: an absent key under partial_state=true must
    // issue a GetLazyStateCommand roundtrip, NOT silently default. Pre-fix: silent default, no wire.
    [Fact(Timeout = 10_000)]
    public async Task PartialState_AbsentKey_EmitsLazyCommandAndAwaits()
    {
        using var rig = new FreshRig(partialState: true);
        var sm = rig.StateMachine;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        sm.SetState("a", 42);

        // "b" was never set and the start was partial → its value is UNKNOWN, requiring a roundtrip.
        // The lazy command's completion id is 1 (SetState burns no id; GetState is the first id).
        var getTask = sm.GetStateAsync<int>("b", CancellationToken.None);
        await rig.StateMachine.FlushPendingAsync(CancellationToken.None);

        // Resolve the lazy roundtrip from a notification; without the fix the Get returns default(0)
        // synchronously and this notification would never be consumed.
        await DeliverInboundAsync(rig, MessageType.GetLazyStateCompletion,
            CreateGetStateCompletion(1u, Json(7)));

        var value = await AwaitBounded(getTask);
        Assert.Equal(7, value);

        rig.InboundWriter.Complete();
        await AwaitBounded(pump);
    }

    // §4.6.2 — complete state map: a present key hits eager (Value) and a journaled
    // GetEagerStateCommand carries the value inline; an absent key under a COMPLETE map is
    // known-empty (Void) and also journaled eager — no lazy roundtrip on either.
    [Fact(Timeout = 10_000)]
    public async Task CompleteState_PresentKeyEager_AbsentKeyVoid()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?> { ["x"] = Json(99) };
        using var rig = new FreshRig(eager, partialState: false);
        var sm = rig.StateMachine;

        var present = await AwaitBounded(sm.GetStateAsync<int>("x", CancellationToken.None));
        var absent = await AwaitBounded(sm.GetStateAsync<int>("y", CancellationToken.None));

        Assert.Equal(99, present);
        Assert.Equal(0, absent);

        var frames = await rig.FlushAndReadOutboundAsync();
        // Both Gets journal GetEagerStateCommand (no lazy command), present=Value, absent=Void.
        var eagerCmds = frames.Where(f => f.Header.Type == MessageType.GetEagerStateCommand)
            .Select(f => Gen.GetEagerStateCommandMessage.Parser.ParseFrom(f.Payload)).ToList();
        Assert.Equal(2, eagerCmds.Count);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.GetLazyStateCommand);

        var presentCmd = eagerCmds.Single(c => c.Key.ToStringUtf8() == "x");
        var absentCmd = eagerCmds.Single(c => c.Key.ToStringUtf8() == "y");
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Value, presentCmd.ResultCase);
        Assert.Equal(99, JsonSerializer.Deserialize<int>(presentCmd.Value.Content.Span));
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Void, absentCmd.ResultCase);
    }

    // §4.6.3 — cleared marker: ClearState then Get → default with a Void GetEagerStateCommand and
    // NO lazy command, on BOTH a partial and a complete start (the cleared marker is null, not Remove).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearedMarker_GetReturnsDefault_VoidEagerNoLazy(bool partialState)
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?> { ["x"] = Json(5) };
        using var rig = new FreshRig(eager, partialState);
        var sm = rig.StateMachine;

        sm.ClearState("x");
        var value = await AwaitBounded(sm.GetStateAsync<int>("x", CancellationToken.None));
        Assert.Equal(0, value);

        var frames = await rig.FlushAndReadOutboundAsync();
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.GetLazyStateCommand);
        var eagerCmd = frames.Where(f => f.Header.Type == MessageType.GetEagerStateCommand)
            .Select(f => Gen.GetEagerStateCommandMessage.Parser.ParseFrom(f.Payload))
            .Single(c => c.Key.ToStringUtf8() == "x");
        // Cleared marker (null) decodes as known-absent → Void, never a lazy roundtrip.
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Void, eagerCmd.ResultCase);
    }

    // §4.6.4 — clear_all flips is_partial to false: after ClearAllState on a PARTIAL start, a Get on
    // an unrelated key is known-empty (no lazy roundtrip), because the map is now complete-and-empty.
    [Fact(Timeout = 10_000)]
    public async Task ClearAllState_FlipsPartialFalse_GetIsKnownEmptyNoLazy()
    {
        using var rig = new FreshRig(partialState: true);
        var sm = rig.StateMachine;

        sm.ClearAllState();
        var value = await AwaitBounded(sm.GetStateAsync<int>("z", CancellationToken.None));
        Assert.Equal(0, value);

        var frames = await rig.FlushAndReadOutboundAsync();
        // No lazy roundtrip: clear_all made the map complete, so absent == known-empty (Void eager).
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.GetLazyStateCommand);
        Assert.Contains(frames, f => f.Header.Type == MessageType.GetEagerStateCommand);
    }

    // §4.6.7 — counter determinism: an eager Get consumes a completion id, so a following Sleep
    // carries id = eagerGetId + 1. Eager Get id = 1, so the SleepCommand id must be 2. Pre-fix the
    // eager Get burned NO id, so the Sleep would have carried id 1 and collided on replay.
    [Fact(Timeout = 10_000)]
    public async Task EagerGet_ConsumesCompletionId_NextSleepIdIsPlusOne()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?> { ["x"] = Json(1) };
        using var rig = new FreshRig(eager, partialState: false);
        var sm = rig.StateMachine;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        _ = await AwaitBounded(sm.GetStateAsync<int>("x", CancellationToken.None));  // burns id 1

        // The Sleep's SleepCommand must allocate id 2 (eagerGetId + 1); resolve it via its ack.
        var sleepTask = sm.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        await DeliverInboundAsync(rig, MessageType.SleepCompletion, CreateSleepCompletion(2u));
        await AwaitBounded(sleepTask);

        rig.CompleteInbound();
        await AwaitBounded(pump);

        // The eager Get journaled a GetEagerStateCommand and the Sleep a SleepCommand with id 2.
        var frames = await rig.FlushAndReadOutboundAsync();
        var sleep = frames.Where(f => f.Header.Type == MessageType.SleepCommand)
            .Select(f => Gen.SleepCommandMessage.Parser.ParseFrom(f.Payload)).Single();
        Assert.Equal(2u, sleep.ResultCompletionId);
    }

    // §4.6.9 — eager GetStateKeys order determinism: an unsorted complete map returns SORTED keys
    // (Rust sorts, context.rs), and the journaled GetEagerStateKeysCommand carries the sorted set.
    [Fact(Timeout = 10_000)]
    public async Task CompleteState_GetStateKeys_AreSorted()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["c"] = Json(3),
            ["a"] = Json(1),
            ["b"] = Json(2)
        };
        using var rig = new FreshRig(eager, partialState: false);
        var sm = rig.StateMachine;

        var keys = await AwaitBounded(sm.GetStateKeysAsync(CancellationToken.None));
        Assert.Equal(SortedKeysAbc, keys);

        var frames = await rig.FlushAndReadOutboundAsync();
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.GetLazyStateKeysCommand);
        var keysCmd = frames.Where(f => f.Header.Type == MessageType.GetEagerStateKeysCommand)
            .Select(f => Gen.GetEagerStateKeysCommandMessage.Parser.ParseFrom(f.Payload)).Single();
        Assert.Equal(SortedKeysAbc, keysCmd.Value.Keys.Select(k => k.ToStringUtf8()).ToArray());
    }

    // §4.6.10 — empty-payload normalization (documented §5 divergence): a zero-length state Value
    // reaching the FRESH eager-hit path normalizes to default, identical to the replayed embedded
    // empty-Value path below. Void and empty-Value reach the same DeserializeStateValue outcome on
    // every path, so fresh and replayed runs can never diverge on a zero-length payload.
    [Fact(Timeout = 10_000)]
    public async Task EmptyPayload_NormalizesToDefault_OnFreshEagerPath()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?> { ["e"] = ReadOnlyMemory<byte>.Empty };
        using var rig = new FreshRig(eager, partialState: true);
        var sm = rig.StateMachine;

        var value = await AwaitBounded(sm.GetStateAsync<int>("e", CancellationToken.None));
        Assert.Equal(0, value);

        // The eager hit is journaled with an empty-content Value (not Void), proving the empty-Value
        // path — and the decode still normalizes to default.
        var frames = await rig.FlushAndReadOutboundAsync();
        var eagerCmd = frames.Where(f => f.Header.Type == MessageType.GetEagerStateCommand)
            .Select(f => Gen.GetEagerStateCommandMessage.Parser.ParseFrom(f.Payload)).Single();
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Value, eagerCmd.ResultCase);
        Assert.Equal(0, eagerCmd.Value.Content.Length);
    }

    // ---- Replay-path coverage -------------------------------------------------------------------

    // §4.6.6 / §4.6.8 — GetEagerStateCommand & GetEagerStateKeysCommand replay decode: a journaled
    // command carries its result inline, so replay returns the embedded value with NO wire roundtrip.
    // §4.6.5 — replay determinism: SetState during replay repopulates the cache so a post-replay Get
    // needs no roundtrip; replaying a captured stream yields identical results with zero mismatch.
    [Fact(Timeout = 10_000)]
    public async Task Replay_EagerCommandsDecodeInline_AndSetStateRepopulatesCache()
    {
        using var rig = new ProtocolTestHarness.StateMachineRig();
        var sm = rig.StateMachine;

        // Journal (known_entries counts Input + commands): a Value eager Get, a Void eager Get, an
        // eager keys command, and a SetState — the deterministic frontier a prior attempt recorded.
        var getValue = ProtobufCodec.CreateGetEagerStateCommand("x", Json(11));
        var getVoid = ProtobufCodec.CreateGetEagerStateCommand("y", null);
        var getKeys = ProtobufCodec.CreateGetEagerStateKeysCommand(KeysX);
        var setA = ProtobufCodec.CreateSetStateCommand("a", Json(5).AsSpan());

        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-replay", knownEntries: 5, partialState: true).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.GetEagerStateCommand, getValue.ToByteArray()),
            (MessageType.GetEagerStateCommand, getVoid.ToByteArray()),
            (MessageType.GetEagerStateKeysCommand, getKeys.ToByteArray()),
            (MessageType.SetStateCommand, setA.ToByteArray()));

        var start = await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.Equal(5, start.KnownEntries);
        Assert.True(sm.IsReplaying);

        // Embedded Value decodes to 11; embedded Void decodes to default — no wire read needed.
        Assert.Equal(11, await AwaitBounded(sm.GetStateAsync<int>("x", CancellationToken.None)));
        Assert.Equal(0, await AwaitBounded(sm.GetStateAsync<int>("y", CancellationToken.None)));

        // GetEagerStateKeysCommand decodes its embedded keys to the same string[] the live path made.
        Assert.Equal(KeysX, await AwaitBounded(sm.GetStateKeysAsync(CancellationToken.None)));

        // SetState during replay repopulates the cache (consumes the journaled SetState, drains the
        // queue) so a post-replay Get for "a" resolves from the cache without a roundtrip.
        sm.SetState("a", 5);
        Assert.False(sm.IsReplaying);
        Assert.Equal(5, await AwaitBounded(sm.GetStateAsync<int>("a", CancellationToken.None)));
    }

    // §4.6.10 — the REPLAYED half of empty-payload normalization: a journaled GetEagerStateCommand
    // whose embedded Value content is empty decodes to default, matching the fresh eager path.
    [Fact(Timeout = 10_000)]
    public async Task Replay_EmptyValuePayload_NormalizesToDefault()
    {
        using var rig = new ProtocolTestHarness.StateMachineRig();
        var sm = rig.StateMachine;

        var emptyValueGet = ProtobufCodec.CreateGetEagerStateCommand("e", ReadOnlyMemory<byte>.Empty);

        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-replay-empty", knownEntries: 2, partialState: true).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.GetEagerStateCommand, emptyValueGet.ToByteArray()));

        _ = await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var value = await AwaitBounded(sm.GetStateAsync<byte[]>("e", CancellationToken.None));
        Assert.Null(value);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static async Task DeliverInboundAsync(FreshRig rig, MessageType type, IMessage message)
    {
        var writer = new ProtocolWriter(rig.InboundWriter);
        writer.WriteMessage(type, message.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DeliverFramedAsync(ProtocolTestHarness.StateMachineRig rig,
        params (MessageType Type, byte[] Payload)[] frames)
    {
        foreach (var (type, payload) in frames)
            await rig.DeliverAsync(type, payload).ConfigureAwait(false);
    }
}
