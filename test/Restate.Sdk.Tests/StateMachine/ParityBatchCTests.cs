using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Parity Batch C — closes shared-core v0.10.0 gaps at the state-machine / wire level
///     (audit doc docs/research/shared-core/09-parity-audit.md):
///
///       * G20 — per-call/send custom command name lands on CallCommand.name (field 12) /
///         OneWayCall.name (field 12) and is threaded as expectedName into the replay validation so
///         Rust's name equality (header_eq) is honored.
///       * G28/G30 — RejectAwakeable / RejectPromise accept a custom Restate/HTTP failure code
///         (default 500), matching shared-core complete_awakeable/complete_promise Failure{code}.
///
///     Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" />.
/// </summary>
public class ParityBatchCTests
{
    private const int WatchdogMs = 10_000;
    private const string Service = "Greeter";
    private const string Handler = "Greet";

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // ---- G20: custom command name ------------------------------------------------------------

    /// <summary>
    ///     G20 — a call with a custom name emits a CallCommand whose <c>name</c> (field 12) carries it.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithName_PopulatesCallCommandName()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, "charge-card", CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(callTask);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var call = Assert.Single(outbound, f => f.Header.Type == MessageType.CallCommand);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(call.Payload);
        Assert.Equal("charge-card", cmd.Name);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G20 — a send with a custom name emits a OneWayCallCommand whose <c>name</c> (field 12)
    ///     carries it.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Send_WithName_PopulatesOneWayCallCommandName()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        await AwaitBounded(rig.StateMachine.SendAsync(
            Service, null, Handler, (object?)"hi", null, null, null, "notify-user", CancellationToken.None));

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var send = Assert.Single(outbound, f => f.Header.Type == MessageType.OneWayCallCommand);
        var cmd = Gen.OneWayCallCommandMessage.Parser.ParseFrom(send.Payload);
        Assert.Equal("notify-user", cmd.Name);
    }

    /// <summary>
    ///     G20 — a call WITHOUT a name emits a minimal CallCommand: <c>name</c> stays empty (the
    ///     proto default), so no name is journaled and the existing no-name replay path is unaffected.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithoutName_EmitsEmptyName()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(callTask);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var call = Assert.Single(outbound, f => f.Header.Type == MessageType.CallCommand);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(call.Payload);
        Assert.Equal("", cmd.Name);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G20 replay — a call WITH a name replays against a journaled CallCommand carrying that same
    ///     name. The live name is threaded as expectedName, so Rust's name equality is honored and the
    ///     dequeue+validate succeeds (no JOURNAL_MISMATCH).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithName_ReplayValidates()
    {
        using var rig = new StateMachineRig();

        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, null, Json("hi"), completionId: 2, invocationIdNotificationIdx: 1,
            idempotencyKey: null, headers: null, name: "charge-card");

        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries: 3));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.CallCommand, journaled);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Same target + same name on replay: dequeue+validate succeeds.
        var result = await AwaitBounded(rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, "charge-card", CancellationToken.None));
        Assert.Equal("ok", result);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G20 replay — a NAME MISMATCH between the journaled CallCommand name and the live name on
    ///     replay is a non-deterministic divergence → ProtocolException (JOURNAL_MISMATCH), exactly as
    ///     Rust's header_eq name comparison would fault. This proves the live name really is threaded
    ///     into the replay comparison (otherwise a wrong name would be silently accepted).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithName_ReplayNameMismatch_Throws()
    {
        using var rig = new StateMachineRig();

        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, null, Json("hi"), completionId: 2, invocationIdNotificationIdx: 1,
            idempotencyKey: null, headers: null, name: "journaled-name");

        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries: 3));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.CallCommand, journaled);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Live name differs from journaled name → mismatch on the dequeue+validate (thrown synchronously
        // inside the prefix lock before any completion await), so CallAsync faults rather than parks.
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            rig.StateMachine.CallAsync<string>(
                Service, null, Handler, "hi", null, null, "live-name", CancellationToken.None).AsTask());
        Assert.Equal(ProtocolException.JournalMismatchCode, ex.Code);

        rig.CompleteInbound();
        try { await AwaitBounded(pump); } catch (ProtocolException) { /* pump observes the same fault */ }
    }

    /// <summary>
    ///     G20 replay — a send WITH a name replays against a journaled OneWayCallCommand carrying that
    ///     same name: the live name is threaded as expectedName and the dequeue+validate succeeds.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Send_WithName_ReplayValidates()
    {
        using var rig = new StateMachineRig();

        var journaled = ProtobufCodec.CreateSendCommandWithOptions(
            Service, Handler, null, Json("hi"), invokeTime: 0, idempotencyKey: null,
            notificationIdx: 1, headers: null, name: "notify-user");

        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries: 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.OneWayCallCommand, journaled);
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));

        // Same target + same name on replay: dequeue+validate succeeds, returns a handle.
        var handle = await AwaitBounded(rig.StateMachine.SendAsync(
            Service, null, Handler, (object?)"hi", null, null, null, "notify-user", CancellationToken.None));
        Assert.NotNull(handle);

        rig.CompleteInbound();
    }

    // ---- G28/G30: custom reject codes --------------------------------------------------------

    /// <summary>
    ///     G28 — RejectAwakeable with a custom code emits a CompleteAwakeableCommand whose failure
    ///     carries that code (not the hard-coded 500).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RejectAwakeable_WithCustomCode_EmitsThatCode()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        rig.StateMachine.RejectAwakeable("sign_1abc", "not found", code: 404);

        var outbound = await FlushAndReadAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.CompleteAwakeableCommand);
        var cmd = Gen.CompleteAwakeableCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.CompleteAwakeableCommandMessage.ResultOneofCase.Failure, cmd.ResultCase);
        Assert.Equal(404u, cmd.Failure.Code);
        Assert.Equal("not found", cmd.Failure.Message);
    }

    /// <summary>
    ///     G28 — the default RejectAwakeable code remains 500 (back-compat), proving the new parameter
    ///     defaults rather than forcing a change at every call site.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RejectAwakeable_DefaultCode_Is500()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        rig.StateMachine.RejectAwakeable("sign_1abc", "boom");

        var outbound = await FlushAndReadAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.CompleteAwakeableCommand);
        var cmd = Gen.CompleteAwakeableCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(500u, cmd.Failure.Code);
    }

    /// <summary>
    ///     G30 — RejectPromise with a custom code emits a CompletePromiseCommand whose failure carries
    ///     that code; the failure ack then surfaces as a TerminalException via ThrowIfFailure.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RejectPromise_WithCustomCode_EmitsThatCode()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var reject = rig.StateMachine.RejectPromise("approval", "denied", CancellationToken.None, code: 409);
        // The CompletePromiseCommand is completable (id 1): deliver a Void (success) ack so the await
        // resolves without throwing — the failure code rides the EMITTED command, not the ack.
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });
        await AwaitBounded(reject);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.CompletePromiseCommand);
        var cmd = Gen.CompletePromiseCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.CompletePromiseCommandMessage.CompletionOneofCase.CompletionFailure, cmd.CompletionCase);
        Assert.Equal(409u, cmd.CompletionFailure.Code);
        Assert.Equal("denied", cmd.CompletionFailure.Message);

        await AwaitBounded(pump);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Drains every outbound frame the SM emitted (250ms read bound; writer may stay open).</summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> ReadAllOutboundAsync(
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
        catch (OperationCanceledException) { /* no more buffered frames */ }

        return frames;
    }

    /// <summary>
    ///     For synchronous emit ops (RejectAwakeable writes but never flushes), flush the buffered
    ///     command to the outbound pipe before draining it.
    /// </summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> FlushAndReadAsync(
        StateMachineRig rig)
    {
        await rig.StateMachine.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        return await ReadAllOutboundAsync(rig).ConfigureAwait(false);
    }
}
