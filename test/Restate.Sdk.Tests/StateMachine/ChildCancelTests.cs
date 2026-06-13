using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Implicit child-cancellation parity — the .NET analogue of Rust's tracked_invocation_ids loop
///     (vm/mod.rs:445-476). A handler that issues child Calls registers each child's invocation-id
///     completion id in <c>_trackedChildren</c>; on inbound CANCEL the single terminal writer
///     (FailTerminalAsync) emits one cancel SendSignalCommand(idx=1, target=childId) per RESOLVED child
///     — strictly BEFORE the terminal 409 OutputCommand, matching Rust where sys_cancel_invocation runs
///     inside do_progress before sys_end.
///
///     Scope (documented divergence, see EmitChildCancelsLocked):
///       * Only children whose invocation-id has ALREADY resolved are cancelled; the .NET cancel fires
///         on the unwinding terminal path where suspending to fetch an unresolved id is impossible, so
///         an unresolved child is deterministically SKIPPED (Rust suspends do_progress instead).
///       * Only Call children are tracked (cancel_children_one_way_calls=false, lib.rs:257).
///       * Child-cancel fires ONLY on the inbound-CANCEL terminal path, never on a generic failure.
///
///     Each handler signals a deterministic ready gate once it has issued its child Calls and parked,
///     so the harness delivers the child invocation-id notifications and the CANCEL frame with no
///     wall-clock race. Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}" />.
/// </summary>
public sealed class ChildCancelTests
{
    // Expected cancel-signal target sequences, hoisted to static readonly fields (CA1861).
    private static readonly string[] TwoChildTargets = { "inv_child_0", "inv_child_1" };
    private static readonly string[] OneChildTarget = { "inv_child_0" };

    // CallFuture allocates TWO completion ids per child in sys_call order (mod.rs:742-744):
    // invocation-id idx FIRST, result id SECOND. Counters start at FirstCompletionId = 1, so the Nth
    // child (0-based) gets invocation-id (2N+1) and result (2N+2).
    private static uint ChildInvocationIdCompletionId(int childIndex) => (uint)(2 * childIndex + 1);

    /// <summary>
    ///     (a) Two child Calls, both invocation-ids resolved, then CANCEL → exactly TWO cancel
    ///     SendSignalCommands (idx=1) targeting the two resolved child ids IN REGISTRY ORDER, each
    ///     strictly before the terminal 409 OutputCommand + End.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task TwoResolvedChildren_InboundCancel_EmitsCancelSignalPerChild()
    {
        using var ready = new ReadyGate();
        var harness = new ChildCancelHarness(ChildCancelServices.TwoCallsDef, () => new ChildCancelServices(ready));

        await AwaitBounded(harness.RunAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready,
            resolveChildren: new[]
            {
                (ChildInvocationIdCompletionId(0), "inv_child_0"),
                (ChildInvocationIdCompletionId(1), "inv_child_1")
            }));

        var frames = AssertFrameOrder(harness.Response);
        var cancelTargets = CancelSignalTargets(frames);
        Assert.Equal(TwoChildTargets, cancelTargets);
        AssertCancelSignalsPrecedeOutput(frames);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (b) Two child Calls but only the FIRST invocation-id resolves before CANCEL → exactly ONE
    ///     cancel SendSignalCommand (for the resolved child). The unresolved child is deterministically
    ///     SKIPPED (no suspend, no extra SendSignal).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task UnresolvedChild_InboundCancel_IsSkipped()
    {
        using var ready = new ReadyGate();
        var harness = new ChildCancelHarness(ChildCancelServices.TwoCallsDef, () => new ChildCancelServices(ready));

        await AwaitBounded(harness.RunAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready,
            resolveChildren: new[] { (ChildInvocationIdCompletionId(0), "inv_child_0") }));

        var frames = AssertFrameOrder(harness.Response);
        Assert.Equal(OneChildTarget, CancelSignalTargets(frames));
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (d) A handler with NO child Calls receives CANCEL → ZERO cancel SendSignalCommands; the
    ///     terminal frame stream is exactly the 409 OutputCommand + End (the pre-existing cancel shape,
    ///     unchanged by the child-cancel path).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task NoChildren_InboundCancel_EmitsNoExtraCommands()
    {
        using var ready = new ReadyGate();
        var harness = new ChildCancelHarness(ChildCancelServices.NoCallsDef, () => new ChildCancelServices(ready));

        await AwaitBounded(harness.RunAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready,
            resolveChildren: Array.Empty<(uint, string)>()));

        var frames = AssertFrameOrder(harness.Response);
        Assert.Empty(CancelSignalTargets(frames));
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.SendSignalCommand);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (e) A one-way Send child is NOT tracked: issuing a Send then receiving CANCEL emits NO cancel
    ///     SendSignalCommand even though the send's invocation-id resolved (cancel_children_one_way_calls
    ///     =false, lib.rs:257). This pins scope-limitation #2.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task OneWaySendChild_InboundCancel_IsNotTracked()
    {
        using var ready = new ReadyGate();
        var harness = new ChildCancelHarness(ChildCancelServices.OneWaySendDef, () => new ChildCancelServices(ready));

        // The Send allocates ONE completion id (the invocation-id idx) = 1; resolve it so we prove that
        // even a RESOLVED one-way child produces no cancel signal (it was never tracked).
        await AwaitBounded(harness.RunAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready,
            resolveChildren: new[] { (1u, "inv_send_child") }));

        var frames = AssertFrameOrder(harness.Response);
        Assert.Empty(CancelSignalTargets(frames));
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (c) Replay determinism: a CANCEL attempt that REPLAYS the first attempt's Call + child-id +
    ///     Sleep as known-entries reproduces the SAME child-cancel. The handler re-runs the Call (which
    ///     re-tracks the child from the replayed CallCommand) and the child's invocation-id arrives in
    ///     the replay batch, so on the post-replay CANCEL the SDK emits EXACTLY ONE cancel SendSignal for
    ///     the resolved child — IDENTICAL to a fresh run. (The child-cancel itself is always written
    ///     fresh on the cancel attempt, never replayed: a 409 terminal Output ends the invocation, so it
    ///     can never re-enter a later replay batch. This pins that the registry rebuilds identically from
    ///     replayed commands, so the resolved-child set — and thus the SendSignal — is deterministic.)
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task Replay_ChildCancel_IsDeterministic()
    {
        using var ready = new ReadyGate();
        var harness = new ChildCancelHarness(ChildCancelServices.OneCallDef, () => new ChildCancelServices(ready));

        await AwaitBounded(harness.RunReplayAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        // The Call command is consumed from the replay queue (not echoed); the child-cancel SendSignal
        // is written FRESH on the cancel attempt for the replayed-and-resolved child.
        Assert.Equal(OneChildTarget, CancelSignalTargets(frames));
        AssertCancelSignalsPrecedeOutput(frames);
        AssertCancelOutput(frames);
    }

    // ---- Assertions ----------------------------------------------------------------------------

    /// <summary>Extracts the target_invocation_id of every cancel SendSignalCommand (idx=1), in order.</summary>
    private static IReadOnlyList<string> CancelSignalTargets(
        IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames) =>
        frames.Where(frame => frame.Header.Type == MessageType.SendSignalCommand)
              .Select(frame => Gen.SendSignalCommandMessage.Parser.ParseFrom(frame.Payload))
              .Where(msg => msg.SignalIdCase == Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx
                            && msg.Idx == InvocationStateMachine.CancelSignalId)
              .Select(msg => msg.TargetInvocationId)
              .ToList();

    /// <summary>Every SendSignalCommand must come strictly before the OutputCommand on the wire.</summary>
    private static void AssertCancelSignalsPrecedeOutput(
        IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
    {
        var outputIndex = IndexOf(frames, MessageType.OutputCommand);
        Assert.True(outputIndex >= 0, "expected a terminal OutputCommand");
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Header.Type == MessageType.SendSignalCommand)
                Assert.True(i < outputIndex,
                    $"child-cancel SendSignalCommand at {i} must precede the OutputCommand at {outputIndex}");
    }

    private static int IndexOf(IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames, MessageType type)
    {
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Header.Type == type)
                return i;
        return -1;
    }

    private static void AssertCancelOutput(IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
    {
        var output = Assert.Single(frames, frame => frame.Header.Type == MessageType.OutputCommand);
        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Failure, msg.ResultCase);
        Assert.Equal(409u, msg.Failure.Code);
        Assert.Equal("cancelled", msg.Failure.Message);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.End);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
    }

    // ---- Deterministic ready gate --------------------------------------------------------------

    private sealed class ReadyGate : IDisposable
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Signal() => _tcs.TrySetResult();
        public Task Wait() => _tcs.Task;
        public void Dispose() => _tcs.TrySetResult();
    }

    // ---- Full-stack harness --------------------------------------------------------------------

    /// <summary>
    ///     Drives <see cref="InvocationHandler.HandleAsync" /> over a duplex pipe. After the handler
    ///     signals the ready gate (child Calls issued + parked on Sleep), it delivers the requested
    ///     child invocation-id completions to resolve the tracked children, then a CANCEL frame.
    /// </summary>
    private sealed class ChildCancelHarness(ServiceDefinition service, Func<object> instanceFactory)
    {
        private readonly System.IO.Pipelines.Pipe _request = new();
        private readonly System.IO.Pipelines.Pipe _response = new();

        public byte[] Response { get; private set; } = [];

        public async Task RunAsync(byte[] input, ReadyGate ready,
            (uint CompletionId, string InvocationId)[] resolveChildren)
        {
            var handler = new InvocationHandler();
            var handlerDef = service.Handlers[0];

            var start = CreateStartMessage(service.Name, knownEntries: 1);
            var writer = new ProtocolWriter(_request.Writer);
            writer.WriteMessage(MessageType.Start, start.ToByteArray());
            writer.WriteMessage(MessageType.InputCommand, CreateInputCommand(input).ToByteArray());
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            var drain = DrainResponseAsync();
            var handlerTask = handler.HandleAsync(_request.Reader, _response.Writer, service, handlerDef,
                new SingleInstanceProvider(instanceFactory()), CancellationToken.None);

            await ready.Wait().WaitAsync(WatchdogTimeout).ConfigureAwait(false);

            // Resolve the chosen child invocation-ids FIRST, so they are buffered in _completions before
            // the CANCEL unwind reads them. A child we omit stays unresolved → it is skipped.
            var deliver = new ProtocolWriter(_request.Writer);
            foreach (var (completionId, invocationId) in resolveChildren)
                deliver.WriteMessage(MessageType.CallInvocationIdCompletion,
                    CreateCallInvocationIdCompletion(completionId, invocationId).ToByteArray());
            deliver.WriteMessage(MessageType.SignalNotification,
                CreateSignalNotification(InvocationStateMachine.CancelSignalId).ToByteArray());
            await deliver.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            await handlerTask.ConfigureAwait(false);
            await _response.Writer.CompleteAsync().ConfigureAwait(false);
            Response = await drain.ConfigureAwait(false);
            try { await _request.Writer.CompleteAsync().ConfigureAwait(false); } catch { /* already torn */ }
        }

        /// <summary>
        ///     Replay driver: the Call command, its resolved child invocation-id, and the Sleep park are
        ///     fed as a known-entries batch, so the handler REPLAYS them — re-running the Call re-tracks
        ///     the child from the replayed CallCommand. After replay drains the handler re-parks on Sleep;
        ///     CANCEL is delivered live and the child-cancel SendSignal is emitted FRESH on this attempt
        ///     (a 409 terminal Output ends the invocation, so a child-cancel never re-enters a replay
        ///     batch — it is always written on the cancel attempt that produces it).
        /// </summary>
        public async Task RunReplayAsync(byte[] input, ReadyGate ready)
        {
            var handler = new InvocationHandler();
            var handlerDef = service.Handlers[0];

            // known_entries = 1 (Input) + 3 replayed entries below (counts commands AND notifications,
            // proto:60-61).
            var start = CreateStartMessage(service.Name, knownEntries: 4);
            var writer = new ProtocolWriter(_request.Writer);
            writer.WriteMessage(MessageType.Start, start.ToByteArray());
            writer.WriteMessage(MessageType.InputCommand, CreateInputCommand(input).ToByteArray());

            // [1] the journaled CallCommand (result idx=2, invocation-id idx=1). Signature is
            // CreateCallCommand(service, handler, key, parameter, completionId, invocationIdNotificationIdx).
            // Replaying it re-allocates ids 1/2 identically and re-appends the child to _trackedChildren.
            writer.WriteMessage(MessageType.CallCommand,
                ProtobufCodec.CreateCallCommand(ChildCancelServices.ChildServiceName,
                    ChildCancelServices.ChildHandlerName, null, ReadOnlySpan<byte>.Empty, 2, 1).ToByteArray());
            // [2] the child's resolved invocation-id (buffered in _completions; read at CANCEL time).
            writer.WriteMessage(MessageType.CallInvocationIdCompletion,
                CreateCallInvocationIdCompletion(1, "inv_child_0").ToByteArray());
            // [3] the Sleep the handler parks on (result_completion_id = 3). After this the replay queue
            // is drained, so parking on the (uncompleted) Sleep notification is legal.
            writer.WriteMessage(MessageType.SleepCommand,
                ProtobufCodec.CreateSleepCommand(0, 3).ToByteArray());
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            var drain = DrainResponseAsync();
            var handlerTask = handler.HandleAsync(_request.Reader, _response.Writer, service, handlerDef,
                new SingleInstanceProvider(instanceFactory()), CancellationToken.None);

            await ready.Wait().WaitAsync(WatchdogTimeout).ConfigureAwait(false);

            // The handler is now re-parked on Sleep id=3 (replay drained). Deliver CANCEL live: the
            // unwind emits the child-cancel SendSignal fresh for the replayed-and-resolved child.
            var deliver = new ProtocolWriter(_request.Writer);
            deliver.WriteMessage(MessageType.SignalNotification,
                CreateSignalNotification(InvocationStateMachine.CancelSignalId).ToByteArray());
            await deliver.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            await handlerTask.ConfigureAwait(false);
            await _response.Writer.CompleteAsync().ConfigureAwait(false);
            Response = await drain.ConfigureAwait(false);
            try { await _request.Writer.CompleteAsync().ConfigureAwait(false); } catch { /* already torn */ }
        }

        private async Task<byte[]> DrainResponseAsync()
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var read = await _response.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var segment in read.Buffer) buffer.Write(segment.Span);
                _response.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted) break;
            }

            return buffer.ToArray();
        }
    }

    private sealed class SingleInstanceProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => instance;
    }

    /// <summary>Handlers that issue child Calls/Sends then park on Sleep until inbound CANCEL.</summary>
    private sealed class ChildCancelServices(ChildCancelTests.ReadyGate ready)
    {
        public const string ChildServiceName = "ChildSvc";
        public const string ChildHandlerName = "child";

        private readonly ChildCancelTests.ReadyGate _ready = ready;

        // Two non-blocking child Calls (tracked), then park on a long Sleep. The futures are never
        // awaited, so only the invocation-id notifications matter for the cancel read.
        public static readonly ServiceDefinition TwoCallsDef =
            BuildService("ChildCancelTwoCallsSvc", async (instance, ctx) =>
            {
                var inst = (ChildCancelServices)instance;
                _ = ctx.CallFuture<string>(ChildServiceName, ChildHandlerName);
                _ = ctx.CallFuture<string>(ChildServiceName, ChildHandlerName);
                inst._ready.Signal();
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        // Exactly one child Call (for the replay determinism case), then park.
        public static readonly ServiceDefinition OneCallDef =
            BuildService("ChildCancelOneCallSvc", async (instance, ctx) =>
            {
                var inst = (ChildCancelServices)instance;
                _ = ctx.CallFuture<string>(ChildServiceName, ChildHandlerName);
                inst._ready.Signal();
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        // No child Calls — pure park; CANCEL must add no SendSignal.
        public static readonly ServiceDefinition NoCallsDef =
            BuildService("ChildCancelNoCallsSvc", async (instance, ctx) =>
            {
                ((ChildCancelServices)instance)._ready.Signal();
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        // A one-way Send child (NOT tracked) then park — proves cancel_children_one_way_calls=false.
        public static readonly ServiceDefinition OneWaySendDef =
            BuildService("ChildCancelOneWaySendSvc", async (instance, ctx) =>
            {
                var inst = (ChildCancelServices)instance;
                await ctx.Send(ChildServiceName, ChildHandlerName).ConfigureAwait(false);
                inst._ready.Signal();
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        private static ServiceDefinition BuildService(
            string name, Func<object, Context, Task<object?>> body) =>
            new()
            {
                Name = name,
                Type = ServiceType.Service,
                Factory = provider => provider.GetService(typeof(ChildCancelServices))!,
                Handlers = new[]
                {
                    new HandlerDefinition
                    {
                        Name = "Run",
                        IsShared = false,
                        HasInput = true,
                        HasOutput = true,
                        InputDeserializer = data => JsonSerializer.Deserialize<string>(data.FirstSpan),
                        Invoker = (instance, ctx, _, _) => body(instance, ctx)
                    }
                }
            };
    }
}
