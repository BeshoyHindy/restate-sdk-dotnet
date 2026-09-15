using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

public class InvocationStateMachineTests : IDisposable
{
    /// <summary>Allows three attempts with negligible backoff, so retry tests stay fast.</summary>
    private static readonly RetryPolicy ThreeAttempts = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(1)
    };

    /// <summary>Allows two attempts, for tests that must exhaust the policy.</summary>
    private static readonly RetryPolicy TwoAttempts = new()
    {
        MaxAttempts = 2,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(1)
    };

    /// <summary>
    ///     The completion id carried by the replayed RunCommand that
    ///     <see cref="StartReplayedRunWithoutCompletionAsync" /> stages.
    /// </summary>
    private const uint ReplayedRunCompletionId = 1;

    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly ProtocolReader _reader;
    private readonly ProtocolWriter _writer;

    public InvocationStateMachineTests()
    {
        _reader = new ProtocolReader(_inbound.Reader);
        _writer = new ProtocolWriter(_outbound.Writer);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
    }

    private InvocationStateMachine CreateSm()
    {
        return new InvocationStateMachine(_reader, _writer);
    }

    // ------- Initialization -------

    [Fact]
    public void InitialState_IsWaitingStart()
    {
        using var sm = CreateSm();
        Assert.Equal(InvocationState.WaitingStart, sm.State);
    }

    [Fact]
    public void Initialize_WithNoKnownEntries_TransitionsToProcessing()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 0);

        Assert.Equal(InvocationState.Processing, sm.State);
        Assert.Equal("inv-123", sm.InvocationId);
        Assert.Equal("my-key", sm.Key);
        Assert.Equal(42UL, sm.RandomSeed);
    }

    [Fact]
    public void Initialize_WithKnownEntries_TransitionsToReplaying()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 3);

        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    [Fact]
    public void Initialize_ThrowsWhenCalledTwice()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        Assert.Throws<InvalidOperationException>(() =>
            sm.Initialize("inv-2", "", 0, 0));
    }

    [Fact]
    public void JsonOptions_IsExposed()
    {
        using var sm = CreateSm();
        Assert.NotNull(sm.JsonOptions);
    }

    // ------- State guards -------

    [Fact]
    public async Task Send_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sm.SendAsync("Svc", null, "Handler", (object?)null, default, CancellationToken.None).AsTask());
    }

    [Fact]
    public void SetState_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        Assert.Throws<InvalidOperationException>(() =>
            sm.SetState("key", 42));
    }

    [Fact]
    public async Task RunAsync_ThrowsInClosedState()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);
        await sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sm.RunAsync("test", () => Task.FromResult(1), CancellationToken.None));
    }

    // ------- Side effects -------

    [Fact]
    public async Task RunAsync_InProcessingMode_ExecutesAction()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var executed = false;
        var result = await sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
            return 42;
        }, CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_Void_InProcessingMode_ExecutesAction()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var executed = false;
        await sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(executed);
    }

    [Fact]
    public async Task RunAsync_ReExecutedAfterReplay_AppliesRetryPolicy()
    {
        using var sm = CreateSm();
        await StartReplayedRunWithoutCompletionAsync(sm);

        var attempts = 0;
        var result = await sm.RunAsync("effect", () =>
        {
            if (++attempts < 3)
                throw new InvalidOperationException("transient");
            return Task.FromResult(42);
        }, CancellationToken.None, ThreeAttempts);

        Assert.Equal(3, attempts);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_Void_ReExecutedAfterReplay_AppliesRetryPolicy()
    {
        using var sm = CreateSm();
        await StartReplayedRunWithoutCompletionAsync(sm);

        var attempts = 0;
        await sm.RunAsync("effect", () =>
        {
            if (++attempts < 3)
                throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        }, CancellationToken.None, ThreeAttempts);

        Assert.Equal(3, attempts);

        var frames = await DrainOutboundAsync();
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.ProposeRunCompletion, frame.Type);
        var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(ReplayedRunCompletionId, proposal.ResultCompletionId);
    }

    [Fact]
    public async Task RunAsync_ReExecutedAfterReplay_ExhaustedRetriesFailTheReplayedRun()
    {
        using var sm = CreateSm();
        await StartReplayedRunWithoutCompletionAsync(sm);
        var replayedWait = ReplayedRunWait(sm);

        var attempts = 0;
        var alwaysFails = new Func<Task<int>>(() =>
        {
            attempts++;
            throw new InvalidOperationException("permanent");
        });

        await Assert.ThrowsAsync<TerminalException>(async () =>
            await sm.RunAsync("effect", alwaysFails, CancellationToken.None, TwoAttempts));

        Assert.Equal(2, attempts);
        AssertFailureProposalForReplayedRun(await DrainOutboundAsync());
        await AssertWaitFailedTerminallyAsync(replayedWait);
    }

    [Fact]
    public async Task RunAsync_Void_ReExecutedAfterReplay_ExhaustedRetriesFailTheReplayedRun()
    {
        using var sm = CreateSm();
        await StartReplayedRunWithoutCompletionAsync(sm);
        var replayedWait = ReplayedRunWait(sm);

        var attempts = 0;
        var alwaysFails = new Func<Task>(() =>
        {
            attempts++;
            throw new InvalidOperationException("permanent");
        });

        await Assert.ThrowsAsync<TerminalException>(async () =>
            await sm.RunAsync("effect", alwaysFails, CancellationToken.None, TwoAttempts));

        Assert.Equal(2, attempts);
        AssertFailureProposalForReplayedRun(await DrainOutboundAsync());
        await AssertWaitFailedTerminallyAsync(replayedWait);
    }

    /// <summary>
    ///     Asserts the re-executed run wrote nothing but a failure proposal for the replayed
    ///     completion id. A second RunCommand would duplicate the journaled entry, and a proposal
    ///     under a fresh id would leave the replayed one uncompletable.
    /// </summary>
    private static void AssertFailureProposalForReplayedRun(List<(MessageType Type, byte[] Payload)> frames)
    {
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.ProposeRunCompletion, frame.Type);

        var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(ReplayedRunCompletionId, proposal.ResultCompletionId);
        Assert.Equal(Gen.ProposeRunCompletionMessage.ResultOneofCase.Failure, proposal.ResultCase);
    }

    /// <summary>
    ///     Asserts the replayed run's wait already carries the terminal failure. Checking
    ///     completion first keeps an unresolved wait — the bug this guards — a failed assertion
    ///     rather than a test that hangs.
    /// </summary>
    private static async Task AssertWaitFailedTerminallyAsync(Task<CompletionResult> wait)
    {
        Assert.True(wait.IsCompleted);
        await Assert.ThrowsAsync<TerminalException>(async () => await wait);
    }

    /// <summary>
    ///     The wait the state machine registered for the replayed run's completion id — the same
    ///     source a combinator awaiting that run's future observes. It is held in a private field
    ///     with no accessor, so the test reads it directly.
    /// </summary>
    private static Task<CompletionResult> ReplayedRunWait(InvocationStateMachine sm)
    {
        var field = typeof(InvocationStateMachine)
            .GetField("_completions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completions = (CompletionManager<int>)field.GetValue(sm)!;
        return completions.GetOrRegister((int)ReplayedRunCompletionId).Task;
    }

    /// <summary>
    ///     Starts the state machine on a resumed journal whose RunCommand was persisted but whose
    ///     ProposeRunCompletion never landed, so the replayed Run has to be re-executed.
    /// </summary>
    private async Task StartReplayedRunWithoutCompletionAsync(InvocationStateMachine sm)
    {
        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-run-retry"),
            DebugId = "inv-run-retry",
            KnownEntries = 2,
            Key = "",
            RandomSeed = 7
        };
        await WriteInboundAsync(MessageType.Start, start.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.RunCommand, ProtobufCodec.CreateRunCommand("effect", 1).ToByteArray());

        await sm.StartAsync(CancellationToken.None);
        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    // ------- Scope and limit key -------

    [Fact]
    public async Task Call_WithScopeAndLimitKey_WritesThemOnTheCallCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.CallAsync<string>("Svc", "obj-key", "Handler", (object?)"payload",
            CallOptions.WithScope("tenant-a", "customer-7"), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.CallCommand, frames[0].Type);
        var call = Gen.CallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("tenant-a", call.Scope);
        Assert.Equal("customer-7", call.LimitKey);
    }

    [Fact]
    public async Task Call_WithoutOptions_OmitsScopeAndLimitKey()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.CallAsync<string>("Svc", null, "Handler", (object?)"payload", default, CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var call = Gen.CallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        // Optional fields: absent, not empty strings — an empty scope means "unscoped" on the wire.
        Assert.False(call.HasScope);
        Assert.False(call.HasLimitKey);
        Assert.False(call.HasIdempotencyKey);
    }

    [Fact]
    public async Task Send_WithScopeAndLimitKey_WritesThemOnTheOneWayCallCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.SendAsync("Svc", "obj-key", "Handler", (object?)"payload",
            SendOptions.WithScope("tenant-a", "customer-7"), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.OneWayCallCommand, frames[0].Type);
        var send = Gen.OneWayCallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("tenant-a", send.Scope);
        Assert.Equal("customer-7", send.LimitKey);
    }

    [Fact]
    public async Task Send_WithoutOptions_OmitsScopeAndLimitKey()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.SendAsync("Svc", null, "Handler", (object?)"payload", default, CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var send = Gen.OneWayCallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.False(send.HasScope);
        Assert.False(send.HasLimitKey);
    }

    [Fact]
    public async Task TypedCall_WithScope_WritesItOnTheCallCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.CallAsync<string, string>("Svc", "Handler", "payload", null,
            CallOptions.WithScope("tenant-a"), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var call = Gen.CallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("tenant-a", call.Scope);
        Assert.False(call.HasLimitKey);
    }

    [Fact]
    public async Task CallFuture_WithScope_WritesItOnTheCallCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = sm.CallFutureAsync("Svc", null, "Handler", (object?)"payload",
            CallOptions.WithScope("tenant-a"), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var call = Gen.CallCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("tenant-a", call.Scope);
    }

    [Fact]
    public async Task LimitKeyWithoutScope_Throws()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        // The server only honours a limit key inside a scope, so asking for one without a scope
        // is a usage error rather than a silently dropped option.
        var call = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sm.CallAsync<string>("Svc", null, "Handler", (object?)null,
                new CallOptions { LimitKey = "customer-7" }, CancellationToken.None));
        Assert.Equal("options", call.ParamName);

        var send = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sm.SendAsync("Svc", null, "Handler", (object?)null,
                new SendOptions { LimitKey = "customer-7" }, CancellationToken.None));
        Assert.Equal("options", send.ParamName);

        Assert.Empty(await DrainOutboundAsync());
    }

    // ------- Calls -------

    [Fact]
    public async Task Send_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        // SendAsync now awaits a completion — we can't test it without a protocol writer that sends back the invocation ID
        // Just verify it doesn't throw synchronously for now
        var task = sm.SendAsync("Greeter", null, "Greet", (object?)"hello", default, CancellationToken.None);
        Assert.False(task.IsCompleted); // awaiting invocation ID notification
    }

    // ------- State -------

    [Fact]
    public void SetState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("count", 42);
    }

    [Fact]
    public void ClearState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("count", 42);
        sm.ClearState("count");
    }

    [Fact]
    public void ClearAllState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("a", 1);
        sm.SetState("b", 2);
        sm.ClearAllState();
    }

    [Fact]
    public async Task GetStateAsync_CompleteStateKeyPresent_ResolvesLocallyAndJournalsEagerCommand()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["count"] = JsonSerializer.SerializeToUtf8Bytes(42)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState);

        var value = await sm.GetStateAsync<int>("count", CancellationToken.None);
        Assert.Equal(42, value);

        var frames = await DrainOutboundAsync();
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.GetEagerStateCommand, frame.Type);

        var msg = Gen.GetEagerStateCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal("count", msg.Key.ToStringUtf8());
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Value, msg.ResultCase);
        Assert.Equal(42, JsonSerializer.Deserialize<int>(msg.Value.Content.Span));
    }

    [Fact]
    public async Task GetStateAsync_CompleteStateKeyAbsent_ReturnsDefaultAndJournalsEagerVoid()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        var value = await sm.GetStateAsync<int>("missing", CancellationToken.None);
        Assert.Equal(0, value);

        var frames = await DrainOutboundAsync();
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.GetEagerStateCommand, frame.Type);

        var msg = Gen.GetEagerStateCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Void, msg.ResultCase);
    }

    [Fact]
    public async Task GetStateAsync_WithEmptyEagerValue_ReturnsDefault()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["empty"] = ReadOnlyMemory<byte>.Empty
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState);

        var value = await sm.GetStateAsync<int>("empty", CancellationToken.None);
        Assert.Equal(0, value);
    }

    [Fact]
    public async Task GetStateAsync_PartialStateKeyPresent_ResolvesLocally()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["count"] = JsonSerializer.SerializeToUtf8Bytes(7)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState, stateIsPartial: true);

        var value = await sm.GetStateAsync<int>("count", CancellationToken.None);
        Assert.Equal(7, value);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.GetEagerStateCommand, Assert.Single(frames).Type);
    }

    [Fact]
    public async Task GetStateAsync_PartialStateKeyAbsent_FallsBackToLazyCommand()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["other"] = JsonSerializer.SerializeToUtf8Bytes(1)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState, stateIsPartial: true);

        // A partial map that misses the key cannot answer locally — the read goes to the wire.
        var pending = sm.GetStateAsync<int>("missing", CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);

        var frames = await DrainOutboundAsync();
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.GetLazyStateCommand, frame.Type);
        Assert.Equal("missing",
            Gen.GetLazyStateCommandMessage.Parser.ParseFrom(frame.Payload).Key.ToStringUtf8());
    }

    [Fact]
    public async Task SetState_UnderPartialState_DoesNotMakeOtherKeysLocallyKnown()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, initialState: null, stateIsPartial: true);

        sm.SetState("a", 1);

        // "a" was just written, so it is locally known and resolves eagerly...
        var a = await sm.GetStateAsync<int>("a", CancellationToken.None);
        Assert.Equal(1, a);

        // ...but "b" must NOT be reported as absent just because SetState created the local
        // map (the pre-V7 latent bug): under partial state it falls back to a lazy read.
        var pending = sm.GetStateAsync<int>("b", CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);

        var frames = await DrainOutboundAsync();
        Assert.Equal(3, frames.Count);
        Assert.Equal(MessageType.SetStateCommand, frames[0].Type);
        Assert.Equal(MessageType.GetEagerStateCommand, frames[1].Type);
        Assert.Equal(MessageType.GetLazyStateCommand, frames[2].Type);
    }

    [Fact]
    public async Task GetStateKeysAsync_CompleteState_ResolvesLocallyAndJournalsEagerCommand()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["a"] = JsonSerializer.SerializeToUtf8Bytes(1),
            ["b"] = JsonSerializer.SerializeToUtf8Bytes(2)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState);

        var keys = await sm.GetStateKeysAsync(CancellationToken.None);
        Array.Sort(keys);
        Assert.Equal(["a", "b"], keys);

        var frames = await DrainOutboundAsync();
        var frame = Assert.Single(frames);
        Assert.Equal(MessageType.GetEagerStateKeysCommand, frame.Type);
        Assert.Equal(2, Gen.GetEagerStateKeysCommandMessage.Parser.ParseFrom(frame.Payload).Value.Keys.Count);
    }

    [Fact]
    public async Task GetStateKeysAsync_PartialState_FallsBackToLazyCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, initialState: null, stateIsPartial: true);

        var pending = sm.GetStateKeysAsync(CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.GetLazyStateKeysCommand, Assert.Single(frames).Type);
    }

    [Fact]
    public async Task ClearAllState_MakesPartialStateComplete()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, initialState: null, stateIsPartial: true);

        sm.ClearAllState();

        // After ClearAllState the (empty) local view is authoritative: reads resolve eagerly.
        var value = await sm.GetStateAsync<int>("anything", CancellationToken.None);
        Assert.Equal(0, value);

        var frames = await DrainOutboundAsync();
        Assert.Equal(2, frames.Count);
        Assert.Equal(MessageType.ClearAllStateCommand, frames[0].Type);
        Assert.Equal(MessageType.GetEagerStateCommand, frames[1].Type);
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Void,
            Gen.GetEagerStateCommandMessage.Parser.ParseFrom(frames[1].Payload).ResultCase);
    }

    // ------- Awakeable -------

    [Fact]
    public void Awakeable_InProcessingMode_ReturnsIdAndTcs()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB, 0xCD], "", 0, 0);

        var (id, tcs) = sm.Awakeable();

        // Format: "sign_1" + Base64UrlSafe(rawId + BigEndian32(signalIndex))
        Assert.StartsWith("sign_1", id);
        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task Awakeable_SignalIndices_StartAtFirstUserIndex()
    {
        using var sm = new InvocationStateMachine(_reader, _writer,
            negotiatedVersion: ServiceProtocolVersion.V7);
        sm.Initialize("inv-1", [0xAB, 0xCD], "", 0, 0);

        // Signal indices 0-16 are reserved for protocol built-ins (CANCEL = 1); handing
        // them to awakeables would let a runtime CANCEL resolve a user awakeable.
        _ = sm.Awakeable();
        _ = sm.Awakeable();
        await sm.SuspendAsync(CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(frames[^1].Payload);
        Assert.Equal(new uint[] { 17, 18 }, suspension.AwaitingOn.WaitingSignals);
    }

    [Fact]
    public async Task CancelSignal_FailsPendingWaitsTerminally_InsteadOfResolvingAwakeable()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        var (_, awakeableTcs) = sm.Awakeable();
        var sleep = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Built-in CANCEL (signal idx 1): must fail pending waits with a terminal 409,
        // never resolve an awakeable with an empty payload.
        await WriteInboundAsync(MessageType.SignalNotification,
            new Gen.SignalNotificationMessage { Idx = 1, Void = new Gen.Void() }.ToByteArray());

        var awakeableEx = await Assert.ThrowsAsync<TerminalException>(() => awakeableTcs.Task);
        Assert.Equal(409, awakeableEx.Code);
        var sleepEx = await Assert.ThrowsAsync<TerminalException>(() => sleep.Task);
        Assert.Equal(409, sleepEx.Code);

        // Waits registered after the cancellation fail immediately.
        var late = sm.Awakeable();
        var lateEx = await Assert.ThrowsAsync<TerminalException>(() => late.Tcs.Task);
        Assert.Equal(409, lateEx.Code);

        _inbound.Writer.Complete();
        await incoming;
    }

    /// <summary>Writes a framed protocol message into the inbound pipe.</summary>
    private async Task WriteInboundAsync(MessageType type, byte[] payload)
    {
        var header = new byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        await _inbound.Writer.WriteAsync(header);
        await _inbound.Writer.WriteAsync(payload);
    }

    [Fact]
    public void ResolveAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.ResolveAwakeable("some-id", new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void RejectAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.RejectAwakeable("some-id", "timeout");
    }

    // ------- Promises -------

    [Fact]
    public void ResolvePromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.ResolvePromise("approval", new { Approved = true });
    }

    [Fact]
    public void RejectPromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.RejectPromise("approval", "denied");
    }

    // ------- Output / Error -------

    [Fact]
    public async Task CompleteAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.CompleteAsync(new byte[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    [Fact]
    public async Task FailAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.FailAsync(500, "something broke", CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    [Fact]
    public async Task FailAsync_DefaultBehavior_IsRetry()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.FailAsync(500, "transient", CancellationToken.None);

        var frames = await DrainOutboundAsync();
        Assert.Equal(2, frames.Count);
        Assert.Equal(MessageType.Error, frames[0].Type);
        Assert.Equal(MessageType.End, frames[1].Type);

        var error = Gen.ErrorMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal(500u, error.Code);
        // RETRY is wire value 0 (never serialized) — identical to pre-V7 ErrorMessages.
        Assert.Equal(Gen.ErrorBehavior.Retry, error.Behavior);
    }

    [Fact]
    public async Task FailAsync_ExplicitBehavior_IsSerialized()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.FailAsync(500, "paused", Gen.ErrorBehavior.Pause, CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);

        var frames = await DrainOutboundAsync();
        var error = Gen.ErrorMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal(Gen.ErrorBehavior.Pause, error.Behavior);
    }

    // ------- Replay -------

    [Fact]
    public void Send_InReplayMode_AdvancesIndexAndTransitions()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 3);

        Assert.Equal(InvocationState.Replaying, sm.State);

        // During replay, SendAsync consumes staged journal entries populated by the StartAsync
        // drain and resolves results through the completion manager. Exercising that requires
        // protocol-level test infrastructure (see ProtocolIntegrationTests resume tests).
        // For now, verify the state machine is in the right state.
        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    /// <summary>
    ///     Starts the state machine on a resumed journal holding the input command plus a single
    ///     replayed command, so the matching handler operation re-traverses it.
    /// </summary>
    private async Task<InvocationStateMachine> StartReplayingAsync(MessageType commandType, byte[] commandPayload)
    {
        var sm = CreateSm();

        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-replay"),
            DebugId = "inv-replay",
            KnownEntries = 2,
            Key = "key",
            RandomSeed = 0
        };
        await WriteInboundAsync(MessageType.Start, start.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(commandType, commandPayload);

        await sm.StartAsync(CancellationToken.None);
        Assert.Equal(InvocationState.Replaying, sm.State);
        return sm;
    }

    [Fact]
    public async Task SetState_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.SetStateCommand,
            ProtobufCodec.CreateSetStateCommand("count", JsonSerializer.SerializeToUtf8Bytes(1)).ToByteArray());

        sm.SetState("count", 1);
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task ClearState_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.ClearStateCommand,
            ProtobufCodec.CreateClearStateCommand("count").ToByteArray());

        sm.ClearState("count");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task ClearAllState_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.ClearAllStateCommand,
            ProtobufCodec.CreateClearAllStateCommand().ToByteArray());

        sm.ClearAllState();
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task ResolveAwakeable_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.CompleteAwakeableCommand,
            ProtobufCodec.CreateCompleteAwakeableSuccess("id", ReadOnlySpan<byte>.Empty).ToByteArray());

        sm.ResolveAwakeable("id", ReadOnlyMemory<byte>.Empty);
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task RejectAwakeable_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.CompleteAwakeableCommand,
            ProtobufCodec.CreateCompleteAwakeableFailure("id", 500, "reason").ToByteArray());

        sm.RejectAwakeable("id", "reason");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task ResolvePromise_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseSuccess("name", JsonSerializer.SerializeToUtf8Bytes("value"), 1)
                .ToByteArray());

        sm.ResolvePromise("name", "value");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public async Task RejectPromise_InReplayMode_AdvancesIndex()
    {
        using var sm = await StartReplayingAsync(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseFailure("name", 500, "reason", 1).ToByteArray());

        sm.RejectPromise("name", "reason");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    // ------- Signals -------

    [Fact]
    public async Task NamedSignal_ResolvesFromNotification()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        var tcs = sm.RegisterSignal("approval");
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Name = "approval",
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("ok")) }
        }.ToByteArray());

        var result = await tcs.Task;
        Assert.Equal("\"ok\"", Encoding.UTF8.GetString(result.Value.Span));
        _ = incoming;
    }

    [Fact]
    public async Task NamedSignal_RejectionFailsTheWaitTerminally()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        var tcs = sm.RegisterSignal("approval");
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Name = "approval",
            Failure = new Gen.Failure { Code = 403, Message = "denied" }
        }.ToByteArray());

        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal(403, ex.Code);
        Assert.Equal("denied", ex.Message);
        _ = incoming;
    }

    [Fact]
    public void UnnamedSignals_AndAwakeables_ShareOneIndexAllocator()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        // Signal indices 0-16 are reserved for built-ins, so user signals start at 17 whichever
        // API allocates them.
        var (first, _) = sm.RegisterSignal();
        var (second, _) = sm.RegisterSignal();
        var (awakeableId, _) = sm.Awakeable();

        Assert.Equal(InvocationStateMachine.FirstUserSignalIndex, first);
        Assert.Equal(InvocationStateMachine.FirstUserSignalIndex + 1, second);
        // The awakeable took the third index rather than colliding with the signals.
        Assert.StartsWith("sign_1", awakeableId);
        var (fourth, _) = sm.RegisterSignal();
        Assert.Equal(InvocationStateMachine.FirstUserSignalIndex + 3, fourth);
    }

    [Fact]
    public async Task UnnamedSignal_ResolvesFromNotification()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        var (index, tcs) = sm.RegisterSignal();
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Idx = (uint)index,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(7)) }
        }.ToByteArray());

        var result = await tcs.Task;
        Assert.Equal("7", Encoding.UTF8.GetString(result.Value.Span));
        _ = incoming;
    }

    [Fact]
    public async Task ReplayedSignalNotification_ResolvesTheWaitRegisteredAfterwards()
    {
        using var sm = CreateSm();

        // A resumed journal whose signal was already resolved: the notification is replayed ahead
        // of the handler, so awaiting it must resolve from the journal instead of waiting again.
        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-replay-signal"),
            DebugId = "inv-replay-signal",
            KnownEntries = 2,
            Key = "",
            RandomSeed = 0
        };
        await WriteInboundAsync(MessageType.Start, start.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Name = "approval",
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("granted")) }
        }.ToByteArray());

        await sm.StartAsync(CancellationToken.None);

        var tcs = sm.RegisterSignal("approval");

        // Already resolved: the wait completes without another notification.
        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var replayed = await tcs.Task;
        Assert.Equal("\"granted\"", Encoding.UTF8.GetString(replayed.Value.Span));
    }

    [Fact]
    public async Task Suspend_WithPendingNamedSignal_AdvertisesItOnTheWire()
    {
        using var sm = new InvocationStateMachine(_reader, _writer,
            negotiatedVersion: ServiceProtocolVersion.V7);
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        _ = sm.RegisterSignal("approval");
        await sm.SuspendAsync(CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("approval", Assert.Single(suspension.AwaitingOn.WaitingNamedSignals));
        Assert.Empty(suspension.AwaitingOn.WaitingSignals);
        Assert.Empty(suspension.AwaitingOn.WaitingCompletions);
    }

    // ------- Journal mismatch -------

    [Fact]
    public async Task ReplayedCommandOfMatchingType_ReplaysUnchanged()
    {
        using var sm = await StartReplayingAsync(MessageType.SleepCommand,
            ProtobufCodec.CreateSleepCommand(123_456UL, 1).ToByteArray());

        // The replayed sleep resolves through the completion manager instead of re-sending the
        // command, so replay ends without anything being written outbound.
        var tcs = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(InvocationState.Processing, sm.State);
        Assert.False(tcs.Task.IsCompleted);
        Assert.Empty(await DrainOutboundAsync());
    }

    [Fact]
    public async Task ReplayedCommandOfWrongType_FailsWithJournalMismatch()
    {
        // The journal recorded a sleep; this attempt's handler writes state instead.
        using var sm = await StartReplayingAsync(MessageType.SleepCommand,
            ProtobufCodec.CreateSleepCommand(123_456UL, 1).ToByteArray());

        var ex = Assert.Throws<TerminalException>(() => sm.SetState("count", 1));

        Assert.Equal(570, ex.Code);
        Assert.Contains("journal mismatch", ex.Message);
        Assert.Contains("journal index 1", ex.Message);
        Assert.Contains("SetState", ex.Message);
        Assert.Contains("Sleep (SleepCommand)", ex.Message);
    }

    [Fact]
    public void ReplayPastStagedEntries_FailsWithJournalMismatch()
    {
        using var sm = CreateSm();
        // A replay boundary of one command with nothing staged for it: the handler's first
        // operation runs past the end of the replayed journal.
        sm.Initialize("inv-1", "key", 0, 1);

        var ex = Assert.Throws<TerminalException>(() => sm.SetState("count", 1));

        Assert.Equal(570, ex.Code);
        Assert.Contains("journal mismatch", ex.Message);
        Assert.Contains("journal index 0", ex.Message);
        Assert.Contains("no command at that index", ex.Message);
        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    // ------- Resolving signals on other invocations -------

    [Fact]
    public async Task ResolveSignal_ByName_WritesTheSendSignalCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        await sm.ResolveSignalAsync("inv-target", "approval", null,
            JsonSerializer.SerializeToUtf8Bytes("granted"), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.SendSignalCommand, frames[0].Type);
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("inv-target", command.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Name, command.SignalIdCase);
        Assert.Equal("approval", command.Name);
        Assert.Equal("\"granted\"", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task ResolveSignal_ByIndex_WritesTheSendSignalCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        await sm.ResolveSignalAsync("inv-target", null, 17,
            JsonSerializer.SerializeToUtf8Bytes(42), CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx, command.SignalIdCase);
        Assert.Equal(17u, command.Idx);
        Assert.Equal("42", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task RejectSignal_ByName_WritesTheFailureOnTheCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        await sm.RejectSignalAsync("inv-target", "approval", null, "denied", 500, CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal("approval", command.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Failure, command.ResultCase);
        Assert.Equal(500u, command.Failure.Code);
        Assert.Equal("denied", command.Failure.Message);
    }

    [Fact]
    public async Task RejectSignal_ByIndex_WritesTheFailureOnTheCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB], "", 0, 0);

        await sm.RejectSignalAsync("inv-target", null, 17, "denied", 500, CancellationToken.None);

        var frames = await DrainOutboundAsync();
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(frames[0].Payload);
        Assert.Equal(17u, command.Idx);
        Assert.Equal("denied", command.Failure.Message);
    }

    [Fact]
    public async Task ResolveSignal_DuringReplay_ConsumesTheJournalEntryWithoutResending()
    {
        using var sm = CreateSm();

        // The signal was already sent on a previous attempt: replaying must re-traverse the
        // journaled SendSignalCommand, not send the signal a second time.
        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-replay-send-signal"),
            DebugId = "inv-replay-send-signal",
            KnownEntries = 2,
            Key = "",
            RandomSeed = 0
        };
        await WriteInboundAsync(MessageType.Start, start.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.SendSignalCommand,
            ProtobufCodec.CreateResolveSignalCommand("inv-target", "approval", null,
                JsonSerializer.SerializeToUtf8Bytes("granted")).ToByteArray());

        await sm.StartAsync(CancellationToken.None);
        Assert.Equal(InvocationState.Replaying, sm.State);

        await sm.ResolveSignalAsync("inv-target", "approval", null,
            JsonSerializer.SerializeToUtf8Bytes("granted"), CancellationToken.None);

        Assert.Equal(InvocationState.Processing, sm.State);
        Assert.Empty(await DrainOutboundAsync());
    }

    // ------- Suspension -------

    /// <summary>Completes the outbound writer and reads back every frame the SM flushed.</summary>
    private async Task<List<(MessageType Type, byte[] Payload)>> DrainOutboundAsync()
    {
        _outbound.Writer.Complete();
        var frames = new List<(MessageType Type, byte[] Payload)>();
        using var reader = new ProtocolReader(_outbound.Reader);
        while (await reader.ReadMessageAsync() is { } msg)
        {
            frames.Add((msg.Header.Type, msg.Payload.ToArray()));
            msg.Dispose();
        }

        return frames;
    }

    [Fact]
    public async Task SuspendAsync_LegacyVersion_UsesLegacyWaitingLists()
    {
        using var sm = new InvocationStateMachine(_reader, _writer,
            negotiatedVersion: ServiceProtocolVersion.V6);
        sm.Initialize("inv-1", "", 0, 0);

        _ = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        await sm.SuspendAsync(CancellationToken.None);

        Assert.Equal(InvocationState.Suspended, sm.State);

        var frames = await DrainOutboundAsync();
        Assert.Equal(2, frames.Count);
        Assert.Equal(MessageType.SleepCommand, frames[0].Type);
        Assert.Equal(MessageType.Suspension, frames[1].Type);

        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(frames[1].Payload);
        Assert.Equal(new uint[] { 0 }, suspension.WaitingCompletions);
        Assert.Empty(suspension.WaitingSignals);
        Assert.Null(suspension.AwaitingOn);
    }

    [Fact]
    public async Task SuspendAsync_V7_UsesAwaitingOnFuture()
    {
        using var sm = new InvocationStateMachine(_reader, _writer,
            negotiatedVersion: ServiceProtocolVersion.V7);
        sm.Initialize("inv-1", "", 0, 0);

        _ = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        _ = sm.Awakeable();
        await sm.SuspendAsync(CancellationToken.None);

        Assert.Equal(InvocationState.Suspended, sm.State);

        var frames = await DrainOutboundAsync();
        Assert.Equal(MessageType.Suspension, frames[^1].Type);

        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(frames[^1].Payload);
        Assert.NotNull(suspension.AwaitingOn);
        Assert.Equal(Gen.CombinatorType.FirstCompleted, suspension.AwaitingOn.CombinatorType);
        Assert.Equal(new uint[] { 0 }, suspension.AwaitingOn.WaitingCompletions);
        // The first user awakeable uses signal index 17 (0-16 are reserved built-ins).
        Assert.Equal(new uint[] { 17 }, suspension.AwaitingOn.WaitingSignals);
        Assert.Empty(suspension.WaitingCompletions);
        Assert.Empty(suspension.WaitingSignals);
    }

    [Fact]
    public async Task SuspendAsync_NothingPending_FailsRetryablyInstead()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.SuspendAsync(CancellationToken.None);

        // The protocol requires at least one element to wait on — with nothing pending the
        // invocation can never be woken, so it must fail retryably instead of suspending.
        Assert.Equal(InvocationState.Closed, sm.State);

        var frames = await DrainOutboundAsync();
        Assert.Equal(2, frames.Count);
        Assert.Equal(MessageType.Error, frames[0].Type);
        Assert.Equal(500u, Gen.ErrorMessage.Parser.ParseFrom(frames[0].Payload).Code);
        Assert.Equal(MessageType.End, frames[1].Type);
    }

    [Fact]
    public async Task SuspendAsync_IsIdempotent()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        _ = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        await sm.SuspendAsync(CancellationToken.None);
        await sm.SuspendAsync(CancellationToken.None);

        var frames = await DrainOutboundAsync();
        Assert.Equal(2, frames.Count); // SleepCommand + exactly one Suspension
        Assert.Equal(MessageType.Suspension, frames[1].Type);
    }

    [Fact]
    public async Task ProcessIncomingMessages_InputEof_PoisonsPendingAndFutureWaits()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var pending = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        _inbound.Writer.Complete();
        await incoming;

        Assert.True(sm.InputClosed);
        await Assert.ThrowsAsync<SuspensionException>(() => pending.Task);

        // Waits registered after EOF suspend immediately (critical for Lambda, where the
        // whole request is buffered and EOF precedes handler execution).
        var late = await sm.SleepFutureAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        await Assert.ThrowsAsync<SuspensionException>(() => late.Task);
    }

    [Fact]
    public async Task ProcessIncomingMessages_ReaderFault_PoisonsPendingWaits()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var pending = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        var incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Transport failure: the read loop must not exit without failing pending waits,
        // or a handler parked on `await tcs.Task` leaks forever.
        _inbound.Writer.Complete(new IOException("transport reset"));

        await Assert.ThrowsAsync<IOException>(() => incoming);
        Assert.True(sm.InputClosed);
        await Assert.ThrowsAsync<SuspensionException>(() => pending.Task);
    }

    [Fact]
    public async Task ProcessIncomingMessages_Cancelled_CancelsPendingWaits()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var pending = await sm.SleepFutureAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var incoming = sm.ProcessIncomingMessagesAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => incoming);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Task);
    }

    // ------- Disposal -------

    [Fact]
    public void Dispose_CancelsAllPendingCompletions()
    {
        var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var (_, tcs) = sm.Awakeable();
        Assert.False(tcs.Task.IsCompleted);

        sm.Dispose();
        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void Dispose_TransitionsToClosed()
    {
        var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.Dispose();
        Assert.Equal(InvocationState.Closed, sm.State);
    }
}