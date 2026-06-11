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
///     B6 — fire-and-forget send + lazy invocation-id resolution (blueprint §4.5, 1.8/2.7.4).
///
///     The pre-fix design made <c>SendAsync</c> block on the invocation-id round trip and decoded
///     the id from raw command bytes on replay (UTF-8 garbage). The fixed model returns immediately
///     after flush with a <see cref="InvocationHandle" /> that resolves the id lazily through the
///     park API; an un-awaited handle never parks, and the replayed handle resolves from the
///     buffered <c>CallInvocationIdCompletionNotification</c>.
///
///     Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" /> so the
///     pre-fix blocking-send hang fails in 5 s instead of freezing the run; the
///     <c>[Fact(Timeout=10000)]</c> attribute is defense-in-depth (xunit v2).
/// </summary>
public class SendHandleTests
{
    private const string Service = "Greeter";
    private const string Handler = "Greet";

    /// <summary>
    ///     §4.5.1 — fire-and-forget: SendAsync returns after flush with NO notification delivered;
    ///     the wire carries a OneWayCallCommand whose invocation_id_notification_idx == 1 (first op).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SendAsync_FireAndForget_ReturnsAfterFlushWithoutNotification()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        // No pump, no notification ever delivered: a blocking pre-fix send would hang here.
        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));
        Assert.NotNull(handle);

        rig.CompleteInbound();
        var outbound = await DrainOutboundAsync(rig);
        var send = Assert.Single(outbound, frame => frame.Header.Type == MessageType.OneWayCallCommand);
        var cmd = Gen.OneWayCallCommandMessage.Parser.ParseFrom(send.Payload);
        Assert.Equal(Service, cmd.ServiceName);
        Assert.Equal(Handler, cmd.HandlerName);
        // First completion id is 1 (FirstCompletionId) — the id the runtime answers with the invocation id.
        Assert.Equal(1u, cmd.InvocationIdNotificationIdx);
    }

    /// <summary>
    ///     §4.5.2 — lazy resolution: deliver CallInvocationIdCompletionNotification{id=1, "inv_123"}
    ///     then await GetInvocationIdAsync() → "inv_123".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetInvocationIdAsync_ResolvesFromNotification()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));

        await rig.DeliverAsync(MessageType.CallInvocationIdCompletion,
            CreateCallInvocationIdCompletion(1, "inv_123"));

        var id = await AwaitBounded(handle.GetInvocationIdAsync());
        Assert.Equal("inv_123", id);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     §4.5.3 — stall regression: withholding the notification must NOT block SendAsync; the send
    ///     completes in bounded time even though the id is never resolved.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SendAsync_WithoutNotification_DoesNotBlock()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // The id round trip is a SEPARATE suspension point; the send itself returns promptly.
        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));
        Assert.NotNull(handle);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     §4.5.4 — replay: journal [Input, OneWayCallCommand{idx=1}] +
    ///     CallInvocationIdCompletionNotification{1,"inv_123"} → the replayed handle resolves
    ///     "inv_123" (pre-fix: UTF-8 garbage decoded from the command bytes).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ReplayedSend_ResolvesIdFromBufferedNotification()
    {
        using var rig = new StateMachineRig();

        // Buffer the replay batch (known_entries counts Input + the OneWayCall command + the id
        // notification = 3) BEFORE StartAsync reads it.
        var oneWayCall = ProtobufCodec.CreateSendCommand(Service, Handler, null,
            JsonSerializer.SerializeToUtf8Bytes("hello"), invokeTime: 0, idempotencyKey: null, notificationIdx: 1);
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-1", knownEntries: 3));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(JsonSerializer.SerializeToUtf8Bytes("hi")));
        await rig.DeliverAsync(MessageType.OneWayCallCommand, oneWayCall);
        await rig.DeliverAsync(MessageType.CallInvocationIdCompletion,
            CreateCallInvocationIdCompletion(1, "inv_123"));

        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        // The replay path dequeues + validates the OneWayCall and returns a lazy handle; resolving it
        // consumes the buffered early-completion slot.
        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));
        var id = await AwaitBounded(handle.GetInvocationIdAsync());
        Assert.Equal("inv_123", id);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>§4.5.5 — eager ctor: new InvocationHandle("id") resolves synchronously.</summary>
    [Fact(Timeout = 10000)]
    public async Task EagerConstructor_ResolvesSynchronously()
    {
        var handle = new InvocationHandle("id-eager");
        var task = handle.GetInvocationIdAsync();
        Assert.True(task.IsCompleted);   // no park, no round trip
        Assert.Equal("id-eager", await task);
    }

    /// <summary>
    ///     §4.5.6 — thunk ctor: the resolve thunk is NOT invoked at construction; the first
    ///     GetInvocationIdAsync invokes it exactly once; repeated awaits return the same value.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ThunkConstructor_InvokesResolveLazilyAndOnce()
    {
        var invocations = 0;
        var handle = new InvocationHandle(() =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult("inv_lazy");
        });

        // Construction alone must not run the thunk (an eager resolution task would itself be a parked
        // awaiter and spuriously suspend a send-then-return handler — 1.8).
        Assert.Equal(0, invocations);

        var first = await AwaitBounded(handle.GetInvocationIdAsync());
        var second = await AwaitBounded(handle.GetInvocationIdAsync());
        Assert.Equal("inv_lazy", first);
        Assert.Equal("inv_lazy", second);
        Assert.Equal(1, invocations);   // Lazy<Task> caches; the thunk ran exactly once
    }

    /// <summary>
    ///     §4.5.7 — suspension via the handle: ctx.Send, EOF, then GetInvocationIdAsync with no
    ///     notification → invocation suspends with waiting_completions == [send's idx]; the in-flight
    ///     await unwinds with SuspendedException (1.8 semantics, also §4.7.10b).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetInvocationIdAsync_AfterEofWithoutNotification_Suspends()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));

        // EOF before the id is awaited (request-response / Lambda shape): the park site itself must
        // notice closed input and suspend, otherwise this await hangs forever.
        rig.CompleteInbound();
        await AwaitBounded(pump);

        await Assert.ThrowsAsync<SuspendedException>(
            async () => await AwaitBounded(handle.GetInvocationIdAsync().AsTask()));

        var outbound = await DrainOutboundAsync(rig);
        var suspension = Assert.Single(outbound, frame => frame.Header.Type == MessageType.Suspension);
        var msg = Gen.SuspensionMessage.Parser.ParseFrom(suspension.Payload);
        Assert.Equal(new uint[] { 1 }, msg.WaitingCompletions.ToArray());
        Assert.Empty(msg.WaitingSignals);
    }

    /// <summary>
    ///     §4.5.8 — post-invocation await: a handle stored beyond the invocation; awaiting after
    ///     Dispose/CancelAll → TaskCanceledException (documented behavior, 1.8). The failure-direction
    ///     half (Failure notification → TerminalException) is covered separately below.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetInvocationIdAsync_AfterDispose_ThrowsTaskCanceled()
    {
        var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var handle = await AwaitBounded(
            rig.StateMachine.SendAsync(Service, null, Handler, (object?)"hello", null, null, CancellationToken.None));

        // Dispose latches both completion managers (CancelAll); a handle awaited afterward gets a
        // born-canceled TCS via the latch instead of hanging on a slot nobody can resolve.
        rig.Dispose();

        await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await AwaitBounded(handle.GetInvocationIdAsync().AsTask()));
    }

    // NOTE on §4.5.8's "Failure notification → TerminalException" clause: the wire message that
    // resolves a send handle, CallInvocationIdCompletionNotificationMessage, carries ONLY
    // {completion_id, invocation_id} — it has no Value/Failure result oneof (verified against the
    // generated proto; the failure oneof lives on CallCompletionNotificationMessage, the call RESULT
    // notification, not the invocation-id one). A send-handle id resolution therefore cannot deliver
    // a terminal failure over the wire, so no such test exists here; the TerminalException-from-
    // notification path is covered for Calls/Runs by their result-notification tests.

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
}
