using System.Text;
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
///     Parity Batch A — closes shared-core v0.10.0 gaps G4-G8 at the state-machine / wire level
///     (audit doc docs/research/shared-core/09-parity-audit.md):
///
///       * G4 — get_call_invocation_id: a blocking Call exposes BOTH its result and the child's
///         invocation id via <see cref="CallHandle{TResponse}" /> (mirrors the Send lazy-id round trip).
///       * G5 — per-call/send custom headers land on CallCommand.headers (field 4) /
///         OneWayCall.headers (field 5) and survive replay validation.
///       * G6/G7 — Attach / GetOutput accept WorkflowId and IdempotencyId targets, setting the
///         AttachInvocation / GetInvocationOutput target oneof.
///       * G8 — a supplied-but-empty idempotency key is rejected (TerminalException) BEFORE any
///         journal mutation (EMPTY_IDEMPOTENCY_KEY parity).
///
///     Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" />.
/// </summary>
public class ParityBatchATests
{
    private const int WatchdogMs = 10_000;
    private const string Service = "Greeter";
    private const string Handler = "Greet";

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // ---- G4: get_call_invocation_id ----------------------------------------------------------

    /// <summary>
    ///     G4 — a blocking call's handle resolves the child's invocation id from the
    ///     CallInvocationIdCompletionNotification (first completion id, idx=1) AND its result from the
    ///     CallCompletionNotification (second id, idx=2). Both round trips succeed independently.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallHandle_ResolvesBothInvocationIdAndResult()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var handle = rig.StateMachine.CallHandleAsync<string>(
            Service, null, Handler, "hello", null, null, CancellationToken.None);

        // The call allocates id 1 (invocation id) then id 2 (result). Deliver both.
        await rig.DeliverAsync(MessageType.CallInvocationIdCompletion,
            CreateCallInvocationIdCompletion(1, "inv_child_42"));
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("world")));

        var id = await AwaitBounded(handle.GetInvocationIdAsync());
        var response = await AwaitBounded(handle.GetResponseAsync());

        Assert.Equal("inv_child_42", id);
        Assert.Equal("world", response);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G4 — asking ONLY for the result never blocks on the id round trip: the response resolves
    ///     even though no CallInvocationIdCompletionNotification is ever delivered (the id thunk is
    ///     lazy and unawaited, so it registers no waiter).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallHandle_ResultOnly_DoesNotRequireInvocationId()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var handle = rig.StateMachine.CallHandleAsync<string>(
            Service, null, Handler, "hello", null, null, CancellationToken.None);

        // Only the RESULT (id 2) is delivered — the invocation-id slot (id 1) is left unresolved.
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("world")));

        var response = await AwaitBounded(handle.GetResponseAsync());
        Assert.Equal("world", response);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G4 — the command is emitted exactly once even though the handle exposes two thunks: the
    ///     prefix Task is shared, so awaiting BOTH (id then result) still journals a single CallCommand.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallHandle_EmitsExactlyOneCallCommand()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var handle = rig.StateMachine.CallHandleAsync<string>(
            Service, null, Handler, "hello", null, null, CancellationToken.None);

        await rig.DeliverAsync(MessageType.CallInvocationIdCompletion,
            CreateCallInvocationIdCompletion(1, "inv_child"));
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(handle.GetInvocationIdAsync());
        await AwaitBounded(handle.GetResponseAsync());

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var calls = outbound.Where(f => f.Header.Type == MessageType.CallCommand).ToArray();
        Assert.Single(calls);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(calls[0].Payload);
        Assert.Equal(Service, cmd.ServiceName);
        Assert.Equal(1u, cmd.InvocationIdNotificationIdx);
        Assert.Equal(2u, cmd.ResultCompletionId);

        await AwaitBounded(pump);
    }

    /// <summary>The eager mock-style ctor resolves both halves synchronously (no protocol round trip).</summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallHandle_EagerConstructor_ResolvesSynchronously()
    {
        var handle = new CallHandle<int>(99, "inv-eager");
        Assert.Equal(99, await handle.GetResponseAsync());
        Assert.Equal("inv-eager", await handle.GetInvocationIdAsync());
    }

    // ---- G5: custom headers ------------------------------------------------------------------

    /// <summary>
    ///     G5 — a call with custom headers emits a CallCommand whose <c>headers</c> repeated field
    ///     carries them in declaration order (Target.headers → CallCommand.headers, field 4).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithHeaders_PopulatesCallCommandHeaders()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var headers = new Dictionary<string, string> { ["x-trace"] = "abc", ["x-tenant"] = "acme" };
        // Drive the result so the call completes; deliver id+result.
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);
        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, headers, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(callTask);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var call = Assert.Single(outbound, f => f.Header.Type == MessageType.CallCommand);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(call.Payload);
        Assert.Equal(2, cmd.Headers.Count);
        Assert.Equal("x-trace", cmd.Headers[0].Key);
        Assert.Equal("abc", cmd.Headers[0].Value);
        Assert.Equal("x-tenant", cmd.Headers[1].Key);
        Assert.Equal("acme", cmd.Headers[1].Value);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G5 — a send with custom headers emits a OneWayCallCommand whose <c>headers</c> field 5
    ///     carries them (OneWayCall.headers).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Send_WithHeaders_PopulatesOneWayCallCommandHeaders()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var headers = new Dictionary<string, string> { ["x-source"] = "sdk" };
        await AwaitBounded(rig.StateMachine.SendAsync(
            Service, null, Handler, (object?)"hi", null, null, headers, CancellationToken.None));

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var send = Assert.Single(outbound, f => f.Header.Type == MessageType.OneWayCallCommand);
        var cmd = Gen.OneWayCallCommandMessage.Parser.ParseFrom(send.Payload);
        var header = Assert.Single(cmd.Headers);
        Assert.Equal("x-source", header.Key);
        Assert.Equal("sdk", header.Value);
    }

    /// <summary>
    ///     G5 — a call with NO headers and NO idempotency key emits a minimal CallCommand: the
    ///     <c>headers</c> repeated field stays empty (no spurious entries).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithoutHeaders_EmitsEmptyHeaderList()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(callTask);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var call = Assert.Single(outbound, f => f.Header.Type == MessageType.CallCommand);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(call.Payload);
        Assert.Empty(cmd.Headers);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G5 replay — a call WITH headers replays against a journaled CallCommand carrying those same
    ///     headers: the replay path dequeue+validates by target (service/handler/key) and the headers
    ///     ride along on the journaled command bytes without breaking validation.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithHeaders_ReplayValidates()
    {
        using var rig = new StateMachineRig();

        var headers = new Dictionary<string, string> { ["x-trace"] = "abc" };
        var journaled = ProtobufCodec.CreateCallCommandWithOptions(
            Service, Handler, null, Json("hi"), completionId: 2, invocationIdNotificationIdx: 1,
            idempotencyKey: null, headers: headers);

        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries: 3));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.CallCommand, journaled);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Same target + same headers on replay: dequeue+validate succeeds (no journal mismatch).
        var result = await AwaitBounded(rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, headers, CancellationToken.None));
        Assert.Equal("ok", result);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- G6/G7: Attach / GetOutput targets ---------------------------------------------------

    /// <summary>
    ///     G6 — Attach by a WorkflowId target sets the AttachInvocationCommand's WorkflowTarget oneof
    ///     (workflow_name + workflow_key).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Attach_ByWorkflowId_SetsWorkflowTarget()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.AttachInvocationAsync<string>(
            AttachTarget.WorkflowId("OrderWorkflow", "order-7"), CancellationToken.None);
        await rig.DeliverAsync(MessageType.AttachInvocationCompletion, CreateAttachCompletion(1, Json("done")));
        var result = await AwaitBounded(task);
        Assert.Equal("done", result);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.AttachInvocationCommand);
        var cmd = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.AttachInvocationCommandMessage.TargetOneofCase.WorkflowTarget, cmd.TargetCase);
        Assert.Equal("OrderWorkflow", cmd.WorkflowTarget.WorkflowName);
        Assert.Equal("order-7", cmd.WorkflowTarget.WorkflowKey);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G6 — Attach by an IdempotencyId target sets the IdempotentRequestTarget oneof, including the
    ///     optional service key when supplied.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Attach_ByIdempotencyId_SetsIdempotentRequestTarget()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.AttachInvocationAsync<string>(
            AttachTarget.IdempotencyId("Cart", "checkout", "idem-key-9", serviceKey: "cart-3"),
            CancellationToken.None);
        await rig.DeliverAsync(MessageType.AttachInvocationCompletion, CreateAttachCompletion(1, Json("ok")));
        await AwaitBounded(task);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.AttachInvocationCommand);
        var cmd = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.AttachInvocationCommandMessage.TargetOneofCase.IdempotentRequestTarget, cmd.TargetCase);
        Assert.Equal("Cart", cmd.IdempotentRequestTarget.ServiceName);
        Assert.Equal("checkout", cmd.IdempotentRequestTarget.HandlerName);
        Assert.Equal("idem-key-9", cmd.IdempotentRequestTarget.IdempotencyKey);
        Assert.True(cmd.IdempotentRequestTarget.HasServiceKey);
        Assert.Equal("cart-3", cmd.IdempotentRequestTarget.ServiceKey);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G6 — an IdempotencyId target WITHOUT a service key (stateless service) leaves the
    ///     service_key field unset (HasServiceKey == false), not an empty string.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Attach_ByIdempotencyId_WithoutServiceKey_LeavesServiceKeyUnset()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.AttachInvocationAsync<string>(
            AttachTarget.IdempotencyId("Mailer", "send", "idem-1"), CancellationToken.None);
        await rig.DeliverAsync(MessageType.AttachInvocationCompletion, CreateAttachCompletion(1, Json("ok")));
        await AwaitBounded(task);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.AttachInvocationCommand);
        var cmd = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.False(cmd.IdempotentRequestTarget.HasServiceKey);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G7 — GetOutput by a WorkflowId target sets the GetInvocationOutputCommand WorkflowTarget
    ///     oneof (the get-output twin of G6).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task GetOutput_ByWorkflowId_SetsWorkflowTarget()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.GetInvocationOutputAsync<string>(
            AttachTarget.WorkflowId("Wf", "k-1"), CancellationToken.None);
        await rig.DeliverAsync(MessageType.GetInvocationOutputCompletion,
            CreateGetOutputCompletion(1, Json("out")));
        var result = await AwaitBounded(task);
        Assert.Equal("out", result);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.GetInvocationOutputCommand);
        var cmd = Gen.GetInvocationOutputCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.GetInvocationOutputCommandMessage.TargetOneofCase.WorkflowTarget, cmd.TargetCase);
        Assert.Equal("Wf", cmd.WorkflowTarget.WorkflowName);
        Assert.Equal("k-1", cmd.WorkflowTarget.WorkflowKey);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G7 — GetOutput by an IdempotencyId target sets the GetInvocationOutputCommand
    ///     IdempotentRequestTarget oneof.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task GetOutput_ByIdempotencyId_SetsIdempotentRequestTarget()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.GetInvocationOutputAsync<string>(
            AttachTarget.IdempotencyId("Svc", "h", "idem-x"), CancellationToken.None);
        await rig.DeliverAsync(MessageType.GetInvocationOutputCompletion,
            CreateGetOutputCompletion(1, Json("v")));
        await AwaitBounded(task);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.GetInvocationOutputCommand);
        var cmd = Gen.GetInvocationOutputCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.GetInvocationOutputCommandMessage.TargetOneofCase.IdempotentRequestTarget, cmd.TargetCase);
        Assert.Equal("Svc", cmd.IdempotentRequestTarget.ServiceName);
        Assert.Equal("idem-x", cmd.IdempotentRequestTarget.IdempotencyKey);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G6/G7 — the legacy string-id overloads still emit the InvocationId oneof (delegating to the
    ///     target overload with the InvocationId variant), preserving wire compatibility.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Attach_ByInvocationIdString_StillSetsInvocationIdTarget()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var task = rig.StateMachine.AttachInvocationAsync<string>("target-inv", CancellationToken.None);
        await rig.DeliverAsync(MessageType.AttachInvocationCompletion, CreateAttachCompletion(1, Json("ok")));
        await AwaitBounded(task);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.AttachInvocationCommand);
        var cmd = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.AttachInvocationCommandMessage.TargetOneofCase.InvocationId, cmd.TargetCase);
        Assert.Equal("target-inv", cmd.InvocationId);

        await AwaitBounded(pump);
    }

    // ---- G8: empty idempotency key rejection -------------------------------------------------

    /// <summary>
    ///     G8 — a supplied-but-empty idempotency key on a Call throws TerminalException BEFORE any
    ///     command is journaled (EMPTY_IDEMPOTENCY_KEY parity). No CallCommand is emitted.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithEmptyIdempotencyKey_ThrowsBeforeJournaling()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var ex = await Assert.ThrowsAsync<TerminalException>(() =>
            rig.StateMachine.CallAsync<string>(
                Service, null, Handler, "hi", "", null, CancellationToken.None).AsTask());
        Assert.Contains("idempotency key", ex.Message);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        Assert.DoesNotContain(outbound, f => f.Header.Type == MessageType.CallCommand);
    }

    /// <summary>
    ///     G8 — same guard on the Send path: an empty idempotency key throws before the
    ///     OneWayCallCommand is journaled.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Send_WithEmptyIdempotencyKey_ThrowsBeforeJournaling()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var ex = await Assert.ThrowsAsync<TerminalException>(() =>
            rig.StateMachine.SendAsync(
                Service, null, Handler, (object?)"hi", null, "", null, CancellationToken.None).AsTask());
        Assert.Contains("idempotency key", ex.Message);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        Assert.DoesNotContain(outbound, f => f.Header.Type == MessageType.OneWayCallCommand);
    }

    /// <summary>
    ///     G8 — the typed-request Send overload enforces the same empty-key guard.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task TypedSend_WithEmptyIdempotencyKey_Throws()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        await Assert.ThrowsAsync<TerminalException>(() =>
            rig.StateMachine.SendAsync(
                Service, Handler, "hi", null, null, "", null, CancellationToken.None).AsTask());
    }

    /// <summary>
    ///     G8 — a non-empty idempotency key is accepted and lands on the CallCommand (the guard fires
    ///     ONLY on the empty string, never on a valid key).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task Call_WithNonEmptyIdempotencyKey_Journals()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", "valid-key", null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        await AwaitBounded(callTask);

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundAsync(rig);
        var call = Assert.Single(outbound, f => f.Header.Type == MessageType.CallCommand);
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(call.Payload);
        Assert.Equal("valid-key", cmd.IdempotencyKey);

        await AwaitBounded(pump);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Drains every outbound frame the SM emitted; the rig writer completes on CompleteInbound.</summary>
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

    private static Gen.AttachInvocationCompletionNotificationMessage CreateAttachCompletion(
        uint completionId, ReadOnlyMemory<byte> value) =>
        new() { CompletionId = completionId, Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) } };

    private static Gen.GetInvocationOutputCompletionNotificationMessage CreateGetOutputCompletion(
        uint completionId, ReadOnlyMemory<byte> value) =>
        new() { CompletionId = completionId, Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) } };
}
