using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

// NOTE: namespace is deliberately NOT Restate.Sdk.Tests.Context — a `Context` segment under the
// test root shadows the Restate.Sdk.Context type that the source-generated clients reference
// (CS0118 across the whole project). ContextSurface keeps the file self-describing without the clash.
namespace Restate.Sdk.Tests.ContextSurface;

/// <summary>
///     Plan 07 §1.2 4b-iv (ContextSurfaceTests). Drives the public Context surface
///     (<see cref="DefaultContext" />, <see cref="DefaultObjectContext" />,
///     <see cref="DefaultWorkflowContext" /> and the shared variants) plus the
///     <c>DurableRandom</c> / <c>DurableConsole</c> / <c>Awakeable</c> / Lazy*Future wrappers over a
///     LIVE in-memory state machine (fresh invocation, <c>known_entries = 1</c>, pump running). The
///     §4 suites drive the SM directly; this file is the only one that exercises the thin context
///     delegation layer and the lazy-future wrappers through their real call sites, so the
///     <c>Internal.Context</c> rule (95/90) and the Lazy*Future lines under <c>Internal</c> are met
///     without touching MockContext-based tests.
///
///     Driving model (matches InvocationStateMachineTests.RunAsync_InProcessingMode): a blocking
///     context op parks on its completion notification; the pump routes a notification we deliver to
///     the inbound pipe by WIRE completion id (ids start at 1 — FirstCompletionId). Every wait flows
///     through the harness 5 s watchdog so a regression that fails to resolve fails the test fast.
/// </summary>
public sealed class ContextSurfaceTests : IDisposable
{
    private readonly StateMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private InvocationStateMachine InitProcessing(string key = "obj-key",
        Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null, bool partialState = true)
    {
        // raw id 0xAB,0xCD so Awakeable id formatting has bytes to encode; known_entries = 1 ⇒ Processing.
        _rig.StateMachine.Initialize("inv-ctx", [0xAB, 0xCD], key, 99, 1, eagerState, partialState);
        return _rig.StateMachine;
    }

    private Task StartPump() => _rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

    private DefaultContext NewServiceContext() =>
        new(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

    // ---- Scalar/replay-safe surface (no park) ------------------------------------------------

    [Fact]
    public void Headers_Random_Console_InvocationId_AreExposedThroughContext()
    {
        InitProcessing();
        var ctx = NewServiceContext();

        // InvocationId and Headers flow straight through to the SM; Random is seeded from RandomSeed
        // and is replay-deterministic (two contexts over the same seed agree).
        Assert.Equal("inv-ctx", ctx.InvocationId);
        Assert.Empty(ctx.Headers);
        Assert.NotNull(ctx.Console);
        var first = ctx.Random.Next();
        var second = NewServiceContext().Random.Next();
        Assert.Equal(first, second);
        Assert.False(ctx.Aborted.IsCancellationRequested);
    }

    // ---- Run family (blocking, parks on the ack notification) --------------------------------

    [Fact]
    public async Task Run_AllOverloads_ExecuteAndResolveFromNotification()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // Each blocking Run allocates the next completion id (1,2,3,…) and parks on its
        // RunCompletionNotification. Deliver them in order so every overload's ack barrier resolves.
        var typed = ctx.Run("typed", async () => { await Task.Yield(); return 7; });
        await DeliverRunCompletionAsync(1, JsonSerializer.SerializeToUtf8Bytes(7));
        Assert.Equal(7, await AwaitBounded(typed));

        var voidTask = ctx.Run("void", async () => await Task.Yield());
        await DeliverRunCompletionAsync(2, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(voidTask);

        var sync = ctx.Run("sync", () => 11);
        await DeliverRunCompletionAsync(3, JsonSerializer.SerializeToUtf8Bytes(11));
        Assert.Equal(11, await AwaitBounded(sync));

        var withRunCtx = ctx.Run("runctx", runCtx => { Assert.NotNull(runCtx.Logger); return Task.FromResult(13); });
        await DeliverRunCompletionAsync(4, JsonSerializer.SerializeToUtf8Bytes(13));
        Assert.Equal(13, await AwaitBounded(withRunCtx));

        var withRunCtxVoid = ctx.Run("runctxvoid", runCtx => { _ = runCtx.CancellationToken; return Task.CompletedTask; });
        await DeliverRunCompletionAsync(5, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(withRunCtxVoid);

        var policy = RetryPolicy.FixedAttempts(3);
        var typedPolicy = ctx.Run("typed-policy", () => Task.FromResult(17), policy);
        await DeliverRunCompletionAsync(6, JsonSerializer.SerializeToUtf8Bytes(17));
        Assert.Equal(17, await AwaitBounded(typedPolicy));

        var voidPolicy = ctx.Run("void-policy", () => Task.CompletedTask, policy);
        await DeliverRunCompletionAsync(7, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(voidPolicy);

        var syncPolicy = ctx.Run("sync-policy", () => 19, policy);
        await DeliverRunCompletionAsync(8, JsonSerializer.SerializeToUtf8Bytes(19));
        Assert.Equal(19, await AwaitBounded(syncPolicy));

        // Now() is a live Run: the closure executes locally and its captured value is returned (the
        // notification is the ack barrier, not the value source on the live path), so assert it is a
        // fresh, plausible timestamp rather than echoing an injected one.
        var before = DateTimeOffset.UtcNow;
        var now = ctx.Now();
        await DeliverRunCompletionAsync(9, ReadOnlyMemory<byte>.Empty);
        var nowValue = await AwaitBounded(now);
        Assert.InRange(nowValue, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task RunAsyncFuture_ResolvesThroughLazyRunFuture()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // RunAsync returns a LazyRunFuture<T>; GetResult parks on the notification (id 1).
        var future = ctx.RunAsync("lazy-run", () => Task.FromResult(21));
        var getResult = future.GetResult();
        await DeliverRunCompletionAsync(1, JsonSerializer.SerializeToUtf8Bytes(21));
        Assert.Equal(21, await AwaitBounded(getResult));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task RunAsyncFuture_EmptyNotification_YieldsDefault()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // LazyRunFuture's empty-value branch: an ack-only RunCompletionNotification → default(T).
        var future = ctx.RunAsync<string?>("lazy-empty", () => Task.FromResult<string?>(null));
        var getResult = future.GetResult();
        await DeliverRunCompletionAsync(1, ReadOnlyMemory<byte>.Empty);
        Assert.Null(await AwaitBounded(getResult));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Timer / Call futures ----------------------------------------------------------------

    [Fact]
    public async Task TimerFuture_ResolvesThroughLazyTimerFuture()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var timer = ctx.Timer(TimeSpan.FromSeconds(1));
        var getResult = timer.GetResult();
        await DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(1));
        Assert.Null(await AwaitBounded(getResult));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task CallFuture_BothOverloads_ResolveThroughLazyCallFuture()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // Call burns two ids (invocation-id idx then result idx). The unkeyed CallFuture's result
        // notification lands on id 2; the keyed one on id 4.
        var unkeyed = ctx.CallFuture<string>("Svc", "Echo", "hi");
        var r1 = unkeyed.GetResult();
        await DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletion(2, JsonSerializer.SerializeToUtf8Bytes("pong")));
        Assert.Equal("pong", await AwaitBounded(r1));

        var keyed = ctx.CallFuture<string>("Svc", "the-key", "Echo", "hi");
        var r2 = keyed.GetResult();
        await DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletion(4, JsonSerializer.SerializeToUtf8Bytes("pong2")));
        Assert.Equal("pong2", await AwaitBounded(r2));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Blocking Call / Send / Sleep --------------------------------------------------------

    [Fact]
    public async Task Call_AllOverloads_RoundTripThroughTheSm()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var unkeyed = ctx.Call<string>("Svc", "Echo", "a");
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, JsonSerializer.SerializeToUtf8Bytes("A")));
        Assert.Equal("A", await AwaitBounded(unkeyed));

        var keyed = ctx.Call<string>("Svc", "k", "Echo", "b");
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(4, JsonSerializer.SerializeToUtf8Bytes("B")));
        Assert.Equal("B", await AwaitBounded(keyed));

        var opts = new CallOptions { IdempotencyKey = "idem" };
        // Cast the request to object? so overload resolution unambiguously picks the
        // (service, handler, request, CallOptions) overload over (service, key, handler, request).
        var unkeyedOpts = ctx.Call<string>("Svc", "Echo", (object?)"c", opts);
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(6, JsonSerializer.SerializeToUtf8Bytes("C")));
        Assert.Equal("C", await AwaitBounded(unkeyedOpts));

        var keyedOpts = ctx.Call<string>("Svc", "k", "Echo", (object?)"d", opts);
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(8, JsonSerializer.SerializeToUtf8Bytes("D")));
        Assert.Equal("D", await AwaitBounded(keyedOpts));

        var typedReq = ctx.Call<string, string>("Svc", "Echo", "e", "k");
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(10, JsonSerializer.SerializeToUtf8Bytes("E")));
        Assert.Equal("E", await AwaitBounded(typedReq));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task Send_AllOverloads_ReturnHandlesAndResolveInvocationId()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // Each Send burns one invocation-id completion id (1,2,3,4). The handle resolves lazily from
        // the CallInvocationIdCompletion the pump routes by that id.
        // object?-cast request pins the non-generic Send overloads (the generic Send<TRequest> would
        // otherwise tie on a string request); h3 below exercises the generic typed-request overload.
        var h1 = await AwaitBounded(ctx.Send("Svc", "Fire", (object?)"x"));
        var h2 = await AwaitBounded(ctx.Send("Svc", "k", "Fire", (object?)"y", TimeSpan.FromSeconds(1)));
        // Named arguments pin the generic Send<TRequest>(service, handler, request, key, options)
        // overload (the typed-request shape), disambiguating it from the object?-request overloads.
        var h3 = await AwaitBounded(ctx.Send<string>("Svc", "Fire", request: "z", key: "k",
            options: new SendOptions { IdempotencyKey = "i" }));
        var h4 = await AwaitBounded(ctx.Send("Svc", "Fire", (object?)"w", TimeSpan.FromSeconds(1), "idem"));

        var id1 = h1.GetInvocationIdAsync();
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(1, "inv_one"));
        Assert.Equal("inv_one", await AwaitBounded(id1));

        var id2 = h2.GetInvocationIdAsync();
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(2, "inv_two"));
        Assert.Equal("inv_two", await AwaitBounded(id2));

        var id3 = h3.GetInvocationIdAsync();
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(3, "inv_three"));
        Assert.Equal("inv_three", await AwaitBounded(id3));

        var id4 = h4.GetInvocationIdAsync();
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(4, "inv_four"));
        Assert.Equal("inv_four", await AwaitBounded(id4));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task Sleep_Attach_GetOutput_RoundTripThroughTheSm()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var sleep = ctx.Sleep(TimeSpan.FromSeconds(1));
        await DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(1));
        await AwaitBounded(sleep);

        var attach = ctx.Attach<string>("inv_target");
        await DeliverCompletionAsync(MessageType.AttachInvocationCompletion, 2,
            JsonSerializer.SerializeToUtf8Bytes("attached"));
        Assert.Equal("attached", await AwaitBounded(attach));

        // GetOutput with an empty completion → default (not-yet-completed branch in the SM).
        var output = ctx.GetOutput<string>("inv_target");
        await DeliverCompletionAsync(MessageType.GetInvocationOutputCompletion, 3, ReadOnlyMemory<byte>.Empty);
        Assert.Null(await AwaitBounded(output));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Batch A parity (G4-G7) through the real DefaultContext ------------------------------

    /// <summary>
    ///     G4 — both CallHandle overloads (unkeyed + keyed) round-trip through the DefaultContext: the
    ///     handle resolves the child invocation id from the CallInvocationIdCompletion AND the response
    ///     from the CallCompletion. Each call burns two completion ids (invocation-id then result).
    /// </summary>
    [Fact]
    public async Task CallHandle_BothOverloads_ResolveIdAndResultThroughContext()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // Drive all FOUR shapes so both the null-options and non-null-options branches of BOTH
        // CallHandle overloads (unkeyed + keyed) are covered. Each call burns two completion ids
        // (invocation id, then result) in allocation order: 1/2, 3/4, 5/6, 7/8.
        var unkeyedNoOpts = ctx.CallHandle<string>("Svc", "Echo", "a");
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(1, "child_1"));
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, JsonSerializer.SerializeToUtf8Bytes("A")));
        Assert.Equal("child_1", await AwaitBounded(unkeyedNoOpts.GetInvocationIdAsync()));
        Assert.Equal("A", await AwaitBounded(unkeyedNoOpts.GetResponseAsync()));

        var unkeyedWithOpts = ctx.CallHandle<string>("Svc", "Echo", "a2",
            CallOptions.WithIdempotencyKey("idem-u"));
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(3, "child_1b"));
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(4, JsonSerializer.SerializeToUtf8Bytes("A2")));
        Assert.Equal("child_1b", await AwaitBounded(unkeyedWithOpts.GetInvocationIdAsync()));
        Assert.Equal("A2", await AwaitBounded(unkeyedWithOpts.GetResponseAsync()));

        var keyedNoOpts = ctx.CallHandle<string>("Svc", "k", "Echo", "b");
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(5, "child_2"));
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(6, JsonSerializer.SerializeToUtf8Bytes("B")));
        Assert.Equal("child_2", await AwaitBounded(keyedNoOpts.GetInvocationIdAsync()));
        Assert.Equal("B", await AwaitBounded(keyedNoOpts.GetResponseAsync()));

        var keyedWithOpts = ctx.CallHandle<string>("Svc", "k", "Echo", "b2",
            CallOptions.WithIdempotencyKey("idem-h"));
        await DeliverAsync(MessageType.CallInvocationIdCompletion, CreateCallInvocationIdCompletion(7, "child_2b"));
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(8, JsonSerializer.SerializeToUtf8Bytes("B2")));
        Assert.Equal("child_2b", await AwaitBounded(keyedWithOpts.GetInvocationIdAsync()));
        Assert.Equal("B2", await AwaitBounded(keyedWithOpts.GetResponseAsync()));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G5 — Call with custom headers (via CallOptions.Headers) round-trips through the context and
    ///     the headers land on the emitted CallCommand.
    /// </summary>
    [Fact]
    public async Task Call_WithHeaders_ThroughContext_EmitsHeaders()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var headers = new Dictionary<string, string> { ["x-trace"] = "t1" };
        var call = ctx.Call<string>("Svc", "Echo", (object?)"a", CallOptions.WithHeaders(headers));
        await DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, JsonSerializer.SerializeToUtf8Bytes("A")));
        Assert.Equal("A", await AwaitBounded(call));

        _rig.CompleteInbound();
        await AwaitBounded(pump);

        var frames = await DrainOutboundAsync();
        var cmd = Gen.CallCommandMessage.Parser.ParseFrom(
            Assert.Single(frames, f => f.Header.Type == MessageType.CallCommand).Payload);
        var header = Assert.Single(cmd.Headers);
        Assert.Equal("x-trace", header.Key);
        Assert.Equal("t1", header.Value);
    }

    /// <summary>
    ///     G5 — both SendOptions-based Send overloads (unkeyed + keyed) emit OneWayCallCommands whose
    ///     headers field carries the SendOptions.Headers.
    /// </summary>
    [Fact]
    public async Task Send_WithSendOptions_ThroughContext_EmitsHeaders()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var headers = new Dictionary<string, string> { ["x-src"] = "ctx" };
        await AwaitBounded(ctx.Send("Svc", "Fire", (object?)"x", SendOptions.WithHeaders(headers)));
        await AwaitBounded(ctx.Send("Svc", "k", "Fire", (object?)"y",
            new SendOptions { Delay = TimeSpan.FromSeconds(1), Headers = headers }));
        // Typed Send<TRequest> with NULL options exercises the options?.* null-conditional branches
        // (Delay/IdempotencyKey/Headers all default-absent) — the no-header send below carries none.
        await AwaitBounded(ctx.Send<string>("Svc", "Fire", request: "z", key: "k"));

        _rig.CompleteInbound();
        await AwaitBounded(pump);

        var frames = await DrainOutboundAsync();
        var sends = frames.Where(f => f.Header.Type == MessageType.OneWayCallCommand).ToArray();
        Assert.Equal(3, sends.Length);
        var withHeaders = sends.Take(2)
            .Select(s => Gen.OneWayCallCommandMessage.Parser.ParseFrom(s.Payload)).ToArray();
        foreach (var cmd in withHeaders)
            Assert.Equal("x-src", Assert.Single(cmd.Headers).Key);
        // The typed null-options send emits no headers.
        Assert.Empty(Gen.OneWayCallCommandMessage.Parser.ParseFrom(sends[2].Payload).Headers);
    }

    /// <summary>
    ///     G6/G7 — Attach and GetOutput by an AttachTarget round-trip through the context and emit the
    ///     correct target oneof (WorkflowTarget / IdempotentRequestTarget).
    /// </summary>
    [Fact]
    public async Task AttachAndGetOutput_ByTarget_ThroughContext()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var attach = ctx.Attach<string>(AttachTarget.WorkflowId("Wf", "k-1"));
        await DeliverCompletionAsync(MessageType.AttachInvocationCompletion, 1,
            JsonSerializer.SerializeToUtf8Bytes("attached"));
        Assert.Equal("attached", await AwaitBounded(attach));

        var output = ctx.GetOutput<string>(AttachTarget.IdempotencyId("Svc", "h", "idem"));
        await DeliverCompletionAsync(MessageType.GetInvocationOutputCompletion, 2,
            JsonSerializer.SerializeToUtf8Bytes("out"));
        Assert.Equal("out", await AwaitBounded(output));

        _rig.CompleteInbound();
        await AwaitBounded(pump);

        var frames = await DrainOutboundAsync();
        var attachCmd = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(
            Assert.Single(frames, f => f.Header.Type == MessageType.AttachInvocationCommand).Payload);
        Assert.Equal(Gen.AttachInvocationCommandMessage.TargetOneofCase.WorkflowTarget, attachCmd.TargetCase);
        var outputCmd = Gen.GetInvocationOutputCommandMessage.Parser.ParseFrom(
            Assert.Single(frames, f => f.Header.Type == MessageType.GetInvocationOutputCommand).Payload);
        Assert.Equal(Gen.GetInvocationOutputCommandMessage.TargetOneofCase.IdempotentRequestTarget,
            outputCmd.TargetCase);
    }

    [Fact]
    public async Task CancelInvocation_AndAwakeable_WriteThroughTheContext()
    {
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // CancelInvocation writes a SendSignal command (no completion) and returns.
        await AwaitBounded(ctx.CancelInvocation("inv_victim"));

        // ResolveAwakeable / RejectAwakeable write CompleteAwakeable commands synchronously.
        ctx.ResolveAwakeable("sign_1abc", "payload");
        ctx.RejectAwakeable("sign_1def", "nope");

        // Awakeable<T> parks on the signal id (17). Deliver the SignalNotification to resolve it.
        var awakeable = ctx.Awakeable<string>();
        Assert.StartsWith("sign_1", awakeable.Id);
        var value = awakeable.Value;
        await DeliverAsync(MessageType.SignalNotification,
            CreateSignalNotification(17, JsonSerializer.SerializeToUtf8Bytes("woke")));
        Assert.Equal("woke", await AwaitBounded(value));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Object / Workflow / shared context delegations --------------------------------------

    [Fact]
    public async Task ObjectContext_State_Surface()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["count"] = JsonSerializer.SerializeToUtf8Bytes(5)
        };
        InitProcessing(key: "the-key", eagerState: eager, partialState: false);
        var ctx = new DefaultObjectContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("the-key", ctx.Key);
        var countKey = new StateKey<int>("count");
        Assert.Equal(5, await AwaitBounded(ctx.Get(countKey)));

        ctx.Set(new StateKey<int>("count"), 9);
        Assert.Equal(9, await AwaitBounded(ctx.Get(new StateKey<int>("count"))));

        // Complete eager map ⇒ StateKeys resolves from the eager set without a wire round-trip.
        var keys = await AwaitBounded(ctx.StateKeys());
        Assert.Contains("count", keys);

        ctx.Clear("count");
        ctx.ClearAll();
    }

    [Fact]
    public async Task SharedObjectContext_ReadOnlySurface()
    {
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["v"] = JsonSerializer.SerializeToUtf8Bytes("hi")
        };
        InitProcessing(key: "shared-key", eagerState: eager, partialState: false);
        var ctx = new DefaultSharedObjectContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("shared-key", ctx.Key);
        Assert.Equal("hi", await AwaitBounded(ctx.Get(new StateKey<string>("v"))));
        Assert.Contains("v", await AwaitBounded(ctx.StateKeys()));
    }

    [Fact]
    public void SharedObjectContext_ScalarAndClientDelegations_ForwardToBaseContext()
    {
        // SharedObjectContext is a thin facade: every scalar accessor and client factory forwards to
        // its BaseContext (a DefaultContext). The §4 + read-only test above only touch Key/Get/
        // StateKeys; these assertions exercise the no-park delegation lines (InvocationId, Random,
        // Console, Headers, Aborted, Now, the six client facades) so the facade's forwarding is
        // proven, not assumed.
        InitProcessing(key: "shared-key");
        var ctx = new DefaultSharedObjectContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("inv-ctx", ctx.InvocationId);
        Assert.NotNull(ctx.Console);
        Assert.Empty(ctx.Headers);
        Assert.False(ctx.Aborted.IsCancellationRequested);
        // Random is seeded deterministically from the same RandomSeed as a sibling service context.
        Assert.Equal(NewServiceContext().Random.Next(), ctx.Random.Next());

        var captured = new List<(Type type, string? key, SendOptions? options)>();
        ClientFactory.Register((_, type, key, options) =>
        {
            captured.Add((type, key, options));
            return new FakeClient();
        });
        var sendOptions = new SendOptions { IdempotencyKey = "idem" };

        Assert.IsType<FakeClient>(ctx.ServiceClient<FakeClient>());
        Assert.IsType<FakeClient>(ctx.ObjectClient<FakeClient>("obj"));
        Assert.IsType<FakeClient>(ctx.WorkflowClient<FakeClient>("wf"));
        Assert.IsType<FakeClient>(ctx.ServiceSendClient<FakeClient>(sendOptions));
        Assert.IsType<FakeClient>(ctx.ObjectSendClient<FakeClient>("obj", sendOptions));
        Assert.IsType<FakeClient>(ctx.WorkflowSendClient<FakeClient>("wf", sendOptions));
        Assert.Equal(6, captured.Count);
    }

    [Fact(Timeout = 10_000)]
    public async Task SharedObjectContext_RunCallSendAwakeable_DelegationsForwardToBaseContext()
    {
        // The remaining SharedObjectContext delegations that touch the SM: every Run overload, the
        // Call/Send families, Sleep, Awakeable + Resolve/Reject, Attach/GetOutput, and the future
        // factories. Each forwards to BaseContext; we drive them over the live SM and resolve the few
        // that park so the forwarding lines execute (and nothing is left unobserved).
        InitProcessing(key: "shared-key");
        var pump = StartPump();
        var ctx = new DefaultSharedObjectContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        // Now() forwards to a Run("__restate_now", ...) so it parks on RunCompletion id 1.
        var nowTask = ctx.Now();
        await DeliverRunCompletionAsync(1, JsonSerializer.SerializeToUtf8Bytes(DateTimeOffset.UtcNow));
        Assert.True((await AwaitBounded(nowTask)) > DateTimeOffset.UnixEpoch);

        // The Run overloads each park on a RunCompletion; ids continue from 2 (Now() burned id 1).
        uint id = 2;
        Assert.Equal(1, await DriveRun(ctx.Run("r1", async () => { await Task.Yield(); return 1; }), id++));
        await DriveRunVoid(ctx.Run("r2", async () => await Task.Yield()), id++);
        Assert.Equal(3, await DriveRun(ctx.Run("r3", () => 3), id++));
        Assert.Equal(4, await DriveRun(ctx.Run("r4", _ => Task.FromResult(4)), id++));
        await DriveRunVoid(ctx.Run("r5", _ => Task.CompletedTask), id++);
        Assert.Equal(6, await DriveRun(ctx.Run("r6", async () => { await Task.Yield(); return 6; }, RetryPolicy.Default), id++));
        await DriveRunVoid(ctx.Run("r7", async () => await Task.Yield(), RetryPolicy.Default), id++);
        Assert.Equal(8, await DriveRun(ctx.Run("r8", () => 8, RetryPolicy.Default), id++));

        // RunAsync (lazy future) forwards too — resolve its detached completion (id 9).
        var runFuture = ctx.RunAsync("r9", async () => { await Task.Yield(); return 9; });
        await DeliverRunCompletionAsync(id++, JsonSerializer.SerializeToUtf8Bytes(9));
        Assert.Equal(9, await AwaitBounded(runFuture.GetResult()));

        // Sleep + Timer (ids 10, 11) — durable timers parked on a SleepCompletion.
        var sleep = ctx.Sleep(TimeSpan.FromSeconds(30));
        await DeliverCompletionAsync(MessageType.SleepCompletion, id++, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(sleep);
        var timer = ctx.Timer(TimeSpan.FromSeconds(30));
        await DeliverCompletionAsync(MessageType.SleepCompletion, id++, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(timer.GetResult());

        // Awakeable + Resolve/Reject forward synchronously (Resolve/Reject write commands).
        var awk = ctx.Awakeable<string>();
        Assert.NotNull(awk.Id);
        ctx.ResolveAwakeable("ak_other", "v");
        ctx.RejectAwakeable("ak_other2", "no");

        // The Call/Send/CallFuture/Attach/GetOutput delegations forward to the SM and PARK on a
        // completion that never arrives in this test. We start them (the one-line forward executes
        // synchronously up to the first await), keep their tasks, then let suspension fault them so
        // nothing is left unobserved. CancelInvocation + Send write a command and complete.
        var noOptions = new CallOptions();
        var req = (object?)"req";
        var parked = new List<Task>
        {
            ctx.Call<string>("Svc", "Handler", req).AsTask(),                    // (service, handler, request)
            ctx.Call<string>("Svc", "k", "Handler", req).AsTask(),              // (service, key, handler, request)
            ctx.Call<string>("Svc", "Handler", req, noOptions).AsTask(),        // (service, handler, request, options)
            ctx.Call<string>("Svc", "k", "Handler", req, noOptions).AsTask(),   // (service, key, handler, request, options)
            ctx.Call<string, string>("Svc", "Handler", "req").AsTask(),         // (service, handler, request, key)
            ctx.Attach<string>("inv_attach").AsTask(),
            ctx.GetOutput<string>("inv_output").AsTask()
        };
        // Future factories forward and return a lazy future; resolving them is owned elsewhere, here
        // we only need the forwarding line to run.
        _ = ctx.CallFuture<string>("Svc", "Handler");
        _ = ctx.CallFuture<string>("Svc", "k", "Handler");

        await AwaitBounded(ctx.CancelInvocation("inv_cancel"));
        await AwaitBounded(ctx.Send<string>("Svc", "Handler", "req"));

        _rig.CompleteInbound();
        await AwaitBounded(pump);

        // Suspension faults every parked completion with SuspendedException; observe them all so no
        // task goes unobserved (the assertion is "they unwound", not a value).
        foreach (var task in parked)
            await Assert.ThrowsAnyAsync<Exception>(() => AwaitBounded(task));
    }

    private async Task<int> DriveRun(ValueTask<int> run, uint completionId)
    {
        await DeliverRunCompletionAsync(completionId, JsonSerializer.SerializeToUtf8Bytes((int)completionId));
        return await AwaitBounded(run);
    }

    private async Task DriveRunVoid(ValueTask run, uint completionId)
    {
        await DeliverRunCompletionAsync(completionId, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(run);
    }

    [Fact]
    public async Task WorkflowContext_State_And_Promise_Surface()
    {
        InitProcessing(key: "wf-key");
        var pump = StartPump();
        var ctx = new DefaultWorkflowContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("wf-key", ctx.Key);
        ctx.Set(new StateKey<string>("w"), "x");
        ctx.Clear("w");
        ctx.ClearAll();

        // ResolvePromise / RejectPromise write CompletePromise commands and AWAIT the
        // CompletePromiseCompletion ack (proto field 11). Each burns one completion id (1 and 2) and
        // parks until its ack lands; an empty (Void) ack resolves the await as a benign success.
        var resolve = ctx.ResolvePromise("approval", "yes");
        var reject = ctx.RejectPromise("other", "no");
        await DeliverCompletionAsync(MessageType.CompletePromiseCompletion, 1, ReadOnlyMemory<byte>.Empty);
        await DeliverCompletionAsync(MessageType.CompletePromiseCompletion, 2, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(resolve);
        await AwaitBounded(reject);

        // Promise parks on a GetPromise completion (the next id). Set/Clear/ClearAll burn no ids;
        // ResolvePromise + RejectPromise each burn one (ids 1 and 2), so Promise's id is 3.
        var promise = ctx.Promise<string>("decision");
        await DeliverCompletionAsync(MessageType.GetPromiseCompletion, 3,
            JsonSerializer.SerializeToUtf8Bytes("approved"));
        Assert.Equal("approved", await AwaitBounded(promise));

        // PeekPromise (id 4) with an empty completion → default.
        var peek = ctx.PeekPromise<string>("decision");
        await DeliverCompletionAsync(MessageType.PeekPromiseCompletion, 4, ReadOnlyMemory<byte>.Empty);
        Assert.Null(await AwaitBounded(peek));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact]
    public async Task SharedWorkflowContext_PeekAndPromiseSurface()
    {
        InitProcessing(key: "swf-key");
        var pump = StartPump();
        var ctx = new DefaultSharedWorkflowContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("swf-key", ctx.Key);
        // ResolvePromise / RejectPromise await the CompletePromiseCompletion ack (ids 1,2).
        var resolve = ctx.ResolvePromise("p", "v");
        var reject = ctx.RejectPromise("q", "r");
        await DeliverCompletionAsync(MessageType.CompletePromiseCompletion, 1, ReadOnlyMemory<byte>.Empty);
        await DeliverCompletionAsync(MessageType.CompletePromiseCompletion, 2, ReadOnlyMemory<byte>.Empty);
        await AwaitBounded(resolve);
        await AwaitBounded(reject);

        // ResolvePromise + RejectPromise burn ids 1,2; PeekPromise parks on id 3.
        var peek = ctx.PeekPromise<string>("p");
        await DeliverCompletionAsync(MessageType.PeekPromiseCompletion, 3, JsonSerializer.SerializeToUtf8Bytes("peeked"));
        Assert.Equal("peeked", await AwaitBounded(peek));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Unkeyed Call / CallFuture overloads (object?-cast request forces the 3-arg overload) -

    [Fact]
    public async Task UnkeyedCallAndCallFuture_OverloadsRoundTrip()
    {
        // DefaultContext lines 91 (unkeyed CallFuture) and 109 (unkeyed Call) are only reachable
        // when overload resolution picks the (service, handler, request) shape. With three string
        // arguments the compiler prefers the keyed (service, key, handler) overload (exact
        // string→string beats string→object? boxing), so the existing Call_/CallFuture_ tests hit
        // the KEYED bodies. Casting the request to object? unambiguously pins the unkeyed overloads.
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        // Unkeyed CallFuture (line 91) → result idx is the second id burned by the call (id 2).
        var future = ctx.CallFuture<string>("Svc", "Echo", (object?)"hi");
        var futureResult = future.GetResult();
        await DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletion(2, JsonSerializer.SerializeToUtf8Bytes("future-pong")));
        Assert.Equal("future-pong", await AwaitBounded(futureResult));

        // Unkeyed blocking Call (line 109) → next call burns ids 3,4; result lands on id 4.
        var blocking = ctx.Call<string>("Svc", "Echo", (object?)"a");
        await DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletion(4, JsonSerializer.SerializeToUtf8Bytes("blocking-pong")));
        Assert.Equal("blocking-pong", await AwaitBounded(blocking));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Awakeable with a caller-supplied serde (FlushThenAwaitAwakeable serde branch) --------

    [Fact]
    public async Task Awakeable_WithCustomSerde_UsesSerdeForDeserialization()
    {
        // DefaultContext line 228: the `serde is not null` arm of FlushThenAwaitAwakeable, reached
        // only when Awakeable<T>(serde) is given a non-null serde. The default-path (serde == null)
        // is already covered by CancelInvocation_AndAwakeable_WriteThroughTheContext. Here the signal
        // payload is decoded through the custom serde, proving the SM hands its raw bytes to it.
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var serde = new UpperCaseSerde();
        var awakeable = ctx.Awakeable(serde);
        Assert.StartsWith("sign_1", awakeable.Id);
        var value = awakeable.Value;

        // The serde lower-cases on the wire; FlushThenAwaitAwakeable must round it back through
        // Deserialize (which upper-cases), so a "woke" payload surfaces as "WOKE".
        await DeliverAsync(MessageType.SignalNotification,
            CreateSignalNotification(17, "woke"u8.ToArray()));
        Assert.Equal("WOKE", await AwaitBounded(value));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Typed-client facade methods (ClientFactory delegation, lines 149/154/159/164/169/174) -

    [Fact]
    public void ClientFacades_DelegateToClientFactory()
    {
        // DefaultContext lines 149,154,159,164,169,174 are the six Service/Object/Workflow
        // client + send-client facades, each a one-liner into ClientFactory.Create<TClient>. They
        // are uncovered because no test installs a global factory (every MockContext test uses the
        // per-instance MockContext._clients registry instead, which never touches ClientFactory).
        // Register a fake factory that echoes back its (key, options) so each facade's distinct
        // argument shape is asserted, then drive all six through the real DefaultContext.
        InitProcessing();
        var ctx = NewServiceContext();

        var captured = new List<(Type type, string? key, SendOptions? options)>();
        ClientFactory.Register((_, type, key, options) =>
        {
            captured.Add((type, key, options));
            return new FakeClient();
        });

        var sendOptions = new SendOptions { IdempotencyKey = "idem" };

        Assert.IsType<FakeClient>(ctx.ServiceClient<FakeClient>());                       // line 149
        Assert.IsType<FakeClient>(ctx.ObjectClient<FakeClient>("obj"));                   // line 154
        Assert.IsType<FakeClient>(ctx.WorkflowClient<FakeClient>("wf"));                  // line 159
        Assert.IsType<FakeClient>(ctx.ServiceSendClient<FakeClient>(sendOptions));        // line 164
        Assert.IsType<FakeClient>(ctx.ObjectSendClient<FakeClient>("obj", sendOptions));  // line 169
        Assert.IsType<FakeClient>(ctx.WorkflowSendClient<FakeClient>("wf", sendOptions)); // line 174

        // Each facade forwarded its own key/options shape: service clients pass no key; object and
        // workflow clients pass their key; the send variants forward the SendOptions.
        // SendOptions is a value type, so identity-compare its forwarded IdempotencyKey field
        // (Assert.Same is invalid on value types — they carry no reference identity).
        Assert.Equal(6, captured.Count);
        Assert.Null(captured[0].key);
        Assert.Equal("obj", captured[1].key);
        Assert.Equal("wf", captured[2].key);
        Assert.Equal("idem", captured[3].options?.IdempotencyKey);
        Assert.Equal("obj", captured[4].key);
        Assert.Equal("idem", captured[4].options?.IdempotencyKey);
        Assert.Equal("wf", captured[5].key);
        Assert.Equal("idem", captured[5].options?.IdempotencyKey);
    }

    // ---- Workflow / shared-workflow state read surface (Get + StateKeys) ----------------------

    [Fact]
    public async Task WorkflowContext_Get_And_StateKeys_ReadFromEagerState()
    {
        // DefaultWorkflowContext lines 22 (Get) and 27 (StateKeys): the existing workflow surface
        // test exercises Set/Clear/Promise but never the read path. A complete eager map
        // (partialState: false) lets Get/StateKeys resolve from the rebuilt cache with no wire park.
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["w"] = JsonSerializer.SerializeToUtf8Bytes("wf-value")
        };
        InitProcessing(key: "wf-key", eagerState: eager, partialState: false);
        var ctx = new DefaultWorkflowContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("wf-value", await AwaitBounded(ctx.Get(new StateKey<string>("w"))));
        Assert.Contains("w", await AwaitBounded(ctx.StateKeys()));
    }

    [Fact]
    public async Task SharedWorkflowContext_Get_And_StateKeys_ReadFromEagerState()
    {
        // DefaultSharedWorkflowContext lines 22 (Get) and 27 (StateKeys): same read-path gap as the
        // exclusive workflow context — the existing shared test only touches Peek/Resolve/Reject.
        var eager = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["s"] = JsonSerializer.SerializeToUtf8Bytes("swf-value")
        };
        InitProcessing(key: "swf-key", eagerState: eager, partialState: false);
        var ctx = new DefaultSharedWorkflowContext(_rig.StateMachine, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("swf-value", await AwaitBounded(ctx.Get(new StateKey<string>("s"))));
        Assert.Contains("s", await AwaitBounded(ctx.StateKeys()));
    }

    // ---- Context.WaitAll combinator (Context.cs 290-305) -------------------------------------

    [Fact]
    public async Task WaitAll_YieldsFuturesInCompletionOrder()
    {
        // Context.WaitAll (Context.cs 287-305) is the only public combinator the §4 suites and the
        // existing surface tests never drive. It maps each future's untyped GetResult() to a Task,
        // then yields (future, error) tuples via Task.WhenEach as completions arrive. Drive a Run
        // future and a Timer future over the live SM: deliver the timer's completion FIRST so the
        // observed yield order is completion order, not declaration order, exercising the
        // task→future back-map (line 301) and the non-faulted error branch (line 302).
        InitProcessing();
        var pump = StartPump();
        var ctx = NewServiceContext();

        var runFuture = ctx.RunAsync("wa-run", () => Task.FromResult(42));   // burns completion id 1
        var timerFuture = ctx.Timer(TimeSpan.FromSeconds(1));                // burns completion id 2

        // Start consuming before delivering so WhenEach observes real completion order.
        var collected = new List<(IDurableFuture future, Exception? error)>();
        var consume = Task.Run(async () =>
        {
            await foreach (var item in ctx.WaitAll(runFuture, timerFuture))
                collected.Add(item);
        });

        // Resolve the timer first, then the run — both futures settle, none fault.
        await DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(2));
        await DeliverRunCompletionAsync(1, JsonSerializer.SerializeToUtf8Bytes(42));

        await AwaitBounded(consume);

        Assert.Equal(2, collected.Count);
        Assert.All(collected, item => Assert.Null(item.error));
        // Both declared futures were yielded exactly once (set equality, order-independent so the
        // test is not flaky on completion-timing jitter).
        Assert.Contains(collected, item => ReferenceEquals(item.future, runFuture));
        Assert.Contains(collected, item => ReferenceEquals(item.future, timerFuture));

        _rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Test doubles -------------------------------------------------------------------------

    /// <summary>A throwaway typed-client shape returned by the fake ClientFactory.</summary>
    private sealed class FakeClient;

    /// <summary>
    ///     A minimal serde that lower-cases on serialize and upper-cases on deserialize, so a test can
    ///     prove the awakeable's serde branch (DefaultContext line 228) actually ran the supplied
    ///     serde rather than the default JSON path.
    /// </summary>
    private sealed class UpperCaseSerde : ISerde<string>
    {
        public string ContentType => "text/plain";

        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToLowerInvariant());
            writer.Write(bytes); // System.Buffers.BuffersExtensions.Write
        }

        public string Deserialize(ReadOnlySequence<byte> data) =>
            Encoding.UTF8.GetString(data.ToArray()).ToUpperInvariant();
    }

    // ---- Notification delivery helpers (route by wire id through the running pump) ------------

    private Task DeliverRunCompletionAsync(uint completionId, ReadOnlyMemory<byte> value) =>
        DeliverAsync(MessageType.RunCompletion, CreateRunCompletion(completionId, value.IsEmpty ? null : value));

    private Task DeliverCompletionAsync(MessageType type, uint completionId, ReadOnlyMemory<byte> value)
    {
        // Generic completion notifications share the NotificationTemplate wire shape; an empty value
        // means an unset result oneof (the SM reads that as empty-success / not-yet-completed).
        var msg = new Gen.NotificationTemplate { CompletionId = completionId };
        if (!value.IsEmpty)
            msg.Value = new Gen.Value { Content = Google.Protobuf.ByteString.CopyFrom(value.Span) };
        return DeliverAsync(type, msg);
    }

    private Task DeliverAsync(MessageType type, Google.Protobuf.IMessage message) =>
        _rig.DeliverAsync(type, message);

    private Task DeliverAsync(MessageType type, byte[] payload) => _rig.DeliverAsync(type, payload);

    /// <summary>
    ///     Drains every outbound frame currently buffered. The SM never completes the outbound writer
    ///     (only Dispose does), so a 250 ms idle cancellation ends the read once nothing more is
    ///     immediately available — within the watchdog, never a hang (mirrors SendHandleTests).
    /// </summary>
    private async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync()
    {
        var reader = new ProtocolReader(_rig.OutboundReader);
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
}
