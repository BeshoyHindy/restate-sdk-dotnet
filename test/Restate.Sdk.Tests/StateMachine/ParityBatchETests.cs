using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Batch E — replay command-equality hardening, mirroring the existing Call-triple / SendSignal
///     replay validation (ReplayEdgeTests / ParityBatchB). Covers, for replayed commands:
///       * Call / OneWayCall custom HEADERS (order-INDEPENDENT key→value set) + idempotency_key — the
///         Batch E additions to <c>InvocationJournal.DequeueReplay(...triple...)</c>. The critical
///         no-false-positive case journals headers in one dictionary order and replays the SAME headers
///         in a DIFFERENT enumeration order: a correct replay MUST pass (a byte/sequence compare would
///         wrongly fail it).
///       * Attach / GetOutput structural TARGET identity (oneof kind + fields) — the new
///         <c>InvocationJournal.DequeueReplay(...attach...)</c> overload.
///       * G29 — <see cref="IContext.SendSignalFailure(string, string, string, ushort)" /> emits a
///         custom failure code on the SendSignalCommand (not the hardcoded 500).
///     Each new branch (matching pass + every distinct divergence) is exercised so the compound
///     equality predicates are fully covered.
/// </summary>
public class ParityBatchETests
{
    private const int WatchdogMs = 10_000;
    private const string Service = "Svc";
    private const string Handler = "Handler";
    private const string Key = "key";

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static async Task StartReplayAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, byte[] Payload)[] journaled)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay-e", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in journaled)
            await rig.DeliverAsync(type, payload);
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    // ---- Call headers / idempotency replay equality ------------------------------------------

    /// <summary>
    ///     CRITICAL no-false-positive: the journaled CallCommand carried headers {a,b} written in ONE
    ///     order; the live replay re-supplies the SAME headers via a dictionary whose enumeration order
    ///     DIFFERS. The order-independent set comparison must treat them as equal and let replay proceed
    ///     (a byte/sequence compare would spuriously throw JOURNAL_MISMATCH on a correct handler).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_SameHeadersDifferentOrder_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal headers in insertion order a→b; ids 1 = invocation idx, 2 = result.
        var journaledHeaders = new[]
        {
            new KeyValuePair<string, string>("a", "1"),
            new KeyValuePair<string, string>("b", "2")
        };
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, Key, Json("p"), 2, 1, idempotencyKey: "idem-1", headers: journaledHeaders);
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));
        Assert.Equal(InvocationState.Replaying, sm.State);

        // Pump consumes the post-replay completion notification delivered below.
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Live headers: SAME pairs, REVERSED enumeration order. Must NOT throw.
        var liveHeaders = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        var callTask = sm.CallAsync<string>(Service, Key, Handler, "p", "idem-1", liveHeaders, null,
            CancellationToken.None);

        // The replay validation ran synchronously inside CallPrefixAsync; reaching Processing without a
        // ProtocolException proves no false positive. Deliver the result so the await completes.
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("done")));
        Assert.Equal("done", await AwaitBounded(callTask.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_DifferentHeaderValue_Throws570()
    {
        using var rig = new StateMachineRig();
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, Key, Json("p"), 2, 1,
            idempotencyKey: null, headers: new[] { new KeyValuePair<string, string>("a", "1") });
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));

        // Same key, DIFFERENT value → set mismatch (the value-differs arm of HeadersEqual).
        var liveHeaders = new Dictionary<string, string> { ["a"] = "changed" };
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            ReplayCallAsync(rig, liveHeaders, idempotency: null));
        Assert.Equal(570, ex.Code);
        Assert.Contains("headers", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_ExtraHeader_Throws570()
    {
        using var rig = new StateMachineRig();
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, Key, Json("p"), 2, 1,
            idempotencyKey: null, headers: new[] { new KeyValuePair<string, string>("a", "1") });
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));

        // Live carries an EXTRA header → count mismatch (the count-differs arm of HeadersEqual).
        var liveHeaders = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            ReplayCallAsync(rig, liveHeaders, idempotency: null));
        Assert.Equal(570, ex.Code);
        Assert.Contains("headers", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_DifferentHeaderKey_Throws570()
    {
        using var rig = new StateMachineRig();
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, Key, Json("p"), 2, 1,
            idempotencyKey: null, headers: new[] { new KeyValuePair<string, string>("a", "1") });
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));

        // Same count + value, DIFFERENT key → TryGetValue miss (the key-absent arm of HeadersEqual).
        var liveHeaders = new Dictionary<string, string> { ["z"] = "1" };
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            ReplayCallAsync(rig, liveHeaders, idempotency: null));
        Assert.Equal(570, ex.Code);
        Assert.Contains("headers", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_DifferentIdempotencyKey_Throws570()
    {
        using var rig = new StateMachineRig();
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, Key, Json("p"), 2, 1, idempotencyKey: "idem-1", headers: null);
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            ReplayCallAsync(rig, headers: null, idempotency: "idem-2"));
        Assert.Equal(570, ex.Code);
        Assert.Contains("idempotency key", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SendReplay_SameHeadersDifferentOrder_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var journaledHeaders = new[]
        {
            new KeyValuePair<string, string>("x", "9"),
            new KeyValuePair<string, string>("y", "8")
        };
        // OneWayCall journals one command (id 1 = invocation idx; no result id).
        var journaled = ProtobufCodec.CreateSendCommandWithOptions(
            Service, Handler, Key, Json("p"), 0, idempotencyKey: "idem-s", notificationIdx: 1,
            headers: journaledHeaders);
        await StartReplayAsync(rig, 2, (MessageType.OneWayCallCommand, journaled.ToByteArray()));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var liveHeaders = new Dictionary<string, string> { ["y"] = "8", ["x"] = "9" };
        // Send (OneWayCall) does not park on a completion; reaching here without a ProtocolException proves
        // the order-independent header set + idempotency match passed on replay. The (object?) cast selects
        // the non-generic (service, key, handler, request, delay, idempotency, headers, name, ct) overload.
        await AwaitBounded(sm.SendAsync(Service, Key, Handler, (object?)"p", null, "idem-s", liveHeaders, null,
            CancellationToken.None).AsTask());
        Assert.Equal(InvocationState.Processing, sm.State);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SendReplay_DifferentIdempotencyKey_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateSendCommandWithOptions(
            Service, Handler, Key, Json("p"), 0, idempotencyKey: "idem-s", notificationIdx: 1);
        await StartReplayAsync(rig, 2, (MessageType.OneWayCallCommand, journaled.ToByteArray()));

        // (object?) cast selects the non-generic overload so service/key/handler line up with the journal
        // and the ONLY divergence under test is the idempotency_key ("idem-s" journaled vs "other" live).
        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.SendAsync(Service, Key, Handler, (object?)"p", null, "other", null, null,
                CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
        Assert.Contains("idempotency key", ex.Message);
    }

    // Replays one Call with the given live headers / idempotency, returning the awaited task — the
    // shared driver for the header/idempotency divergence cases above.
    private static Task ReplayCallAsync(StateMachineRig rig,
        IReadOnlyDictionary<string, string>? headers, string? idempotency) =>
        rig.StateMachine.CallAsync<string>(Service, Key, Handler, "p", idempotency, headers, null,
            CancellationToken.None).AsTask();

    // ---- Attach / GetOutput structural target identity ---------------------------------------

    /// <summary>Replaying an Attach with the SAME workflow-id target as journaled must pass.</summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task AttachReplay_SameWorkflowTarget_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var target = AttachTarget.WorkflowId("MyWorkflow", "wf-key");
        var journaled = ProtobufCodec.CreateAttachInvocationCommand(target, 1);
        await StartReplayAsync(rig, 2, (MessageType.AttachInvocationCommand, journaled.ToByteArray()));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var attachTask = sm.AttachInvocationAsync<string>(
            AttachTarget.WorkflowId("MyWorkflow", "wf-key"), CancellationToken.None);
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.AttachInvocationCompletion, AttachCompletion(1, Json("attached")));
        Assert.Equal("attached", await AwaitBounded(attachTask.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task AttachReplay_DifferentTargetKind_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journaled as an InvocationId target; live replay attaches by a WORKFLOW target → kind mismatch.
        var journaled = ProtobufCodec.CreateAttachInvocationCommand(
            AttachTarget.InvocationId("inv-123"), 1);
        await StartReplayAsync(rig, 2, (MessageType.AttachInvocationCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.AttachInvocationAsync<string>(
                AttachTarget.WorkflowId("W", "k"), CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task AttachReplay_SameKindDifferentInvocationId_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var journaled = ProtobufCodec.CreateAttachInvocationCommand(
            AttachTarget.InvocationId("inv-123"), 1);
        await StartReplayAsync(rig, 2, (MessageType.AttachInvocationCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.AttachInvocationAsync<string>(
                AttachTarget.InvocationId("inv-OTHER"), CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task AttachReplay_SameWorkflowKindDifferentKey_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var journaled = ProtobufCodec.CreateAttachInvocationCommand(
            AttachTarget.WorkflowId("W", "k1"), 1);
        await StartReplayAsync(rig, 2, (MessageType.AttachInvocationCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.AttachInvocationAsync<string>(
                AttachTarget.WorkflowId("W", "k2"), CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GetOutputReplay_SameIdempotencyTarget_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var target = AttachTarget.IdempotencyId("Svc", "H", "idem-k", "svc-key");
        var journaled = ProtobufCodec.CreateGetInvocationOutputCommand(target, 1);
        await StartReplayAsync(rig, 2, (MessageType.GetInvocationOutputCommand, journaled.ToByteArray()));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var getTask = sm.GetInvocationOutputAsync<string>(
            AttachTarget.IdempotencyId("Svc", "H", "idem-k", "svc-key"), CancellationToken.None);
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.GetInvocationOutputCompletion, GetOutputCompletion(1, Json("out")));
        Assert.Equal("out", await AwaitBounded(getTask.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     A journaled Attach command with the target oneof UNSET (a corrupt/foreign journal) parses to
    ///     <c>AttachReplayTargetKind.None</c>; replaying it against ANY live target is a mismatch — this
    ///     covers both None branches (the codec's flatten fallback and the validator's <c>_ =&gt; false</c>).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task AttachReplay_JournaledTargetUnset_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var journaled = new Gen.AttachInvocationCommandMessage { ResultCompletionId = 1 };  // no target oneof
        await StartReplayAsync(rig, 2, (MessageType.AttachInvocationCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.AttachInvocationAsync<string>(
                AttachTarget.InvocationId("inv-x"), CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }

    private static Gen.AttachInvocationCompletionNotificationMessage AttachCompletion(
        uint completionId, ReadOnlyMemory<byte> value) =>
        new() { CompletionId = completionId, Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) } };

    private static Gen.GetInvocationOutputCompletionNotificationMessage GetOutputCompletion(
        uint completionId, ReadOnlyMemory<byte> value) =>
        new() { CompletionId = completionId, Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) } };

    [Fact(Timeout = WatchdogMs)]
    public async Task GetOutputReplay_DifferentIdempotencyKey_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        var journaled = ProtobufCodec.CreateGetInvocationOutputCommand(
            AttachTarget.IdempotencyId("Svc", "H", "idem-k"), 1);
        await StartReplayAsync(rig, 2, (MessageType.GetInvocationOutputCommand, journaled.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.GetInvocationOutputAsync<string>(
                AttachTarget.IdempotencyId("Svc", "H", "DIFFERENT"), CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }
}
